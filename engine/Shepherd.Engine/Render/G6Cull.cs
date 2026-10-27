using UnityEngine;
using Shepherd.Engine.Native;
using Shepherd.Engine.Impl;
namespace Shepherd.Engine.Render;

/// <summary>Device kit #7, track probes -- G6: honour each shader's Cull state for meshes (fidelity; vita/notes/kit7-probes.md; the
/// trick survey's G6 "decal cubes: possible double-strength decal light"). The engine draws every triangle of a mesh (the GL backend
/// disables face culling once), while Unity culls by the main pass's <c>Cull Off | Front | Back</c> -- Back when the shader says nothing.
/// A closed mesh drawn with a Cull Back / Front shader (a decal or light volume, a cube) showed both its halves: blended twice. Now a
/// MeshRenderer draw whose shader culls drops exactly the triangles Unity's GPU would cull, before the draw is built (the same draw, the
/// same order, fewer triangles; no GL state change). Facing as Unity's GPU decides it: the clip-space orientation of the triangle
/// (det |x y w| of its three corners: positive = counter-clockwise on screen with y up = a back face in Unity's clockwise-front convention;
/// right for triangles that cross the near plane too), flipped when the object's world matrix mirrors (a negative determinant: Unity
/// inverts culling for negatively scaled objects). Render textures need no flip (they are stored top-down like the screen: SceneRenderer).
/// Degenerate triangles are kept (nothing to rasterize either way). Only Normal / Blend / Grab mesh draws (the engine's Glow is its own
/// approximation of a volumetric shader, not that shader's output: left as it is); SpriteRenderers and UI are not touched (a Cull Back
/// sprite shader exists in the game's set -- how Unity's sprite flip meets it is to be checked against Unity first); Spine skeletons are
/// MeshRenderers with Cull Off shaders.
/// Where the mode comes from: the shader asset's own serialized state (m_ParsedForm, main pass, m_State.culling -- the Mac reads the game's
/// bundles, which carry it), or, when the asset has no culling field (the Vita pack's shader reduction keeps only what the engine read
/// before kit #7), the owner-local table data/shader-cull.tsv (name TAB mode TAB property-or-empty; made on the Mac from the owner's
/// install by <c>Shepherd.Assets.Cli shadercull</c>, staged like data/video-lengths.tsv; never in git); neither = unknown = drawn as
/// before (both faces). A material property that drives it (<c>Cull [_Cull]</c>) is read per draw, as Unity does.
/// <c>SHEPHERD_K7_G6=0</c> = kit #6 (every triangle drawn). <c>SHEPHERD_K7_G6_LOG=1</c>: a census line per shader the first time a mesh draw
/// meets it with a mode other than Off (mode, source, triangles culled, and whether the culled ones face away from the camera by the
/// mesh's own normals: the sign check). <c>SHEPHERD_K7_CULL_TABLE=&lt;file&gt;|0</c>: another table / none.</summary>
[ProfileOnly]
public static class G6Cull
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_K7_G6") != "0";
    static readonly bool LogOn = Environment.GetEnvironmentVariable("SHEPHERD_K7_G6_LOG") == "1";
    static Dictionary<string, (byte mode, string prop)> _table;
    static bool _tableTried;
    /// <summary>Draws / triangles culled since the start (the [g6] end line and the census).</summary>
    public static long Draws, Culled;

    static string TablePath()
    {
        var e = Environment.GetEnvironmentVariable("SHEPHERD_K7_CULL_TABLE");
        if (e == "0") return null;
        if (!string.IsNullOrEmpty(e)) return e;
        try { var p = Path.Combine(Program.Root, "data", "shader-cull.tsv"); return File.Exists(p) ? p : null; } catch (Exception) { return null; }
    }
    static void LoadTable()
    {
        _tableTried = true;
        var path = TablePath();
        if (path == null) return;
        try
        {
            var t = new Dictionary<string, (byte, string)>(StringComparer.Ordinal);
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw; int h = line.IndexOf('#'); if (h >= 0) line = line[..h];
                var f = line.Split('\t');
                if (f.Length < 2 || f[0].Length == 0 || !byte.TryParse(f[1].Trim(), out var m) || m > 2) continue;
                t[f[0]] = (m, f.Length > 2 && f[2].Trim().Length > 0 ? f[2].Trim() : null);
            }
            _table = t;
            Log.Info("[g6] shader cull table " + path + ": " + t.Count + " shader(s) (for shader assets that carry no culling field: the Vita pack's)");
        }
        catch (Exception e) { Log.Warn("[g6] shader cull table " + path + " unreadable (" + e.Message + "): unknown culling = both faces, as kit #6"); }
    }

    /// <summary>ShaderPolicy.Parse, at the end: the mode from the table when the asset carried no culling field.</summary>
    public static void Complete(ShaderPolicy.State st, string name)
    {
        if (st == null || st.CullFrom != 0 || name == null) return;
        if (!_tableTried) LoadTable();
        if (_table != null && _table.TryGetValue(name, out var e))
        {
            st.Cull = e.mode; st.CullProp = e.prop == null ? 0 : ShaderIds.Id(e.prop); st.CullFrom = 2;
        }
    }

    static int Mode(NMaterial mat, ShaderPolicy.State st)
    {
        if (st.CullProp != 0 && mat != null && mat.P.TryGetValue(st.CullProp, out var v))
            return v switch { float f => (int)f, int i => i, _ => st.Cull };
        return st.Cull;
    }

    static float[] _xs = new float[256], _ys = new float[256], _ws = new float[256];
    static bool[] _drop = new bool[128];
    static readonly HashSet<string> _logged = new();

    /// <summary>A Normal / Blend / Grab mesh draw: the submesh's triangle list without the triangles Unity would cull (the culled ones as -1, which
    /// Emit's index filter drops; the array is reused: valid until the next call), or <paramref name="idx"/> itself when nothing is culled (the
    /// shader culls nothing, the mode is unknown, every triangle faces the right way).</summary>
    public static int[] Filter(NMaterial mat, NMesh mesh, int sub, int[] idx, in Matrix4x4 lw, in Matrix4x4 vp, Vector3 cam, NGameObject go)
    {
        var st = mat?.Shader?.State;
        if (st == null || st.Cull == 255 || mesh == null || idx == null || idx.Length < 3) return idx;
        if (sub < mesh.SubTopology.Count && mesh.SubTopology[sub] != 0) return idx;   // not a triangle list (Unity's MeshTopology.Triangles = 0)
        int mode = Mode(mat, st);
        if (mode != 1 && mode != 2) return idx;   // Off (0)
        Vector3[] verts = mesh.Pos;
        int nv = verts.Length;
        if (_xs.Length < nv) { int c = Math.Max(nv, _xs.Length * 2); _xs = new float[c]; _ys = new float[c]; _ws = new float[c]; }
        var m = M.Mul(vp, lw);
        for (int i = 0; i < nv; i++)
        {
            var p = verts[i];
            _xs[i] = m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03;
            _ys[i] = m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13;
            _ws[i] = m.m30 * p.x + m.m31 * p.y + m.m32 * p.z + m.m33;
        }
        bool mirror = lw.m00 * (lw.m11 * lw.m22 - lw.m12 * lw.m21) - lw.m01 * (lw.m10 * lw.m22 - lw.m12 * lw.m20) + lw.m02 * (lw.m10 * lw.m21 - lw.m11 * lw.m20) < 0;
        int n = idx.Length / 3 * 3, away = 0, drops = 0;
        if (_drop.Length < n / 3) _drop = new bool[Math.Max(n / 3, _drop.Length * 2)];
        for (int t = 0, k = 0; t < n; t += 3, k++)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            bool drop = false;
            if ((uint)a < (uint)nv && (uint)b < (uint)nv && (uint)c < (uint)nv)
            {
                double det = (double)_xs[a] * ((double)_ys[b] * _ws[c] - (double)_ys[c] * _ws[b]) - (double)_ys[a] * ((double)_xs[b] * _ws[c] - (double)_xs[c] * _ws[b])
                             + (double)_ws[a] * ((double)_xs[b] * _ys[c] - (double)_xs[c] * _ys[b]);
                if (det != 0)
                {
                    bool back = mirror ? det < 0 : det > 0;
                    drop = mode == 2 ? back : !back;
                }
            }
            _drop[k] = drop;
            if (drop)
            {
                drops++;
                if (LogOn) away += FacesAway(mesh, a, b, c, in lw, cam) ? 1 : 0;
            }
        }
        if (LogOn && _logged.Add(mat.ShaderName))
            Log.Info("[g6] '" + mat.ShaderName + "' main pass Cull " + (mode == 2 ? "Back" : "Front") + (st.CullProp != 0 ? " [" + ShaderIds.Name(st.CullProp) + "]" : "") +
                     " (from the " + (st.CullFrom == 1 ? "shader asset" : "cull table") + ") on '" + (go?.Name ?? "?") + "' mesh '" + mesh.Name + "': " + drops + " of " + n / 3 +
                     " triangles culled in this draw; " + away + " of the culled face away from the camera by the mesh's " + (mesh.Normals != null && mesh.Normals.Length == nv ? "normals" : "geometry (centroid from the centre of its bounds: right for convex meshes such as a cube)") +
                     (mirror ? " (mirrored object: culling inverted, as Unity does)" : ""));
        if (drops == 0) return idx;
        Draws++; Culled += drops;
        if (drops == n / 3) return Array.Empty<int>();   // every triangle culled: nothing to draw (Emit draws nothing for fewer than 3 indices)
        // kit #7 review (no allocation per culled draw): an array of idx's own length, reused for every draw of that length and every frame, with
        // the culled triangles -- and any indices past the last whole triangle -- written as -1. Every consumer runs Emit's index filter
        // (i >= 0 && i < nv) and copies what it keeps before the next draw (WorldEmitter's IBuf; MtRender's snapshot by content, whose
        // replay is the same EmitMesh), so the draw gets exactly the kept triangles in order, as from a compacted array.
        if (!_out.TryGetValue(idx.Length, out var r)) _out[idx.Length] = r = new int[idx.Length];
        for (int t = 0, k = 0; t < n; t += 3, k++)
            if (_drop[k]) { r[t] = -1; r[t + 1] = -1; r[t + 2] = -1; }
            else { r[t] = idx[t]; r[t + 1] = idx[t + 1]; r[t + 2] = idx[t + 2]; }
        for (int t = n; t < r.Length; t++) r[t] = -1;
        return r;
    }
    static readonly Dictionary<int, int[]> _out = new();
    /// <summary>Census only: the triangle's own normal (its vertices' normals, in world space) points away from the camera.</summary>
    static bool FacesAway(NMesh mesh, int a, int b, int c, in Matrix4x4 lw, Vector3 cam)
    {
        var nn = mesh.Normals; Vector3 nrm;
        if (nn != null && nn.Length == mesh.Pos.Length) nrm = nn[a] + nn[b] + nn[c];
        else
        {   // no normals kept: the outward direction of a convex mesh = the triangle's centroid from the centre of the vertex box
            var mn = mesh.Pos[0]; var mx = mn;
            foreach (var q in mesh.Pos) { mn = Vector3.Min(mn, q); mx = Vector3.Max(mx, q); }
            nrm = (mesh.Pos[a] + mesh.Pos[b] + mesh.Pos[c]) / 3f - (mn + mx) * 0.5f;
        }
        var w = new Vector3(lw.m00 * nrm.x + lw.m01 * nrm.y + lw.m02 * nrm.z, lw.m10 * nrm.x + lw.m11 * nrm.y + lw.m12 * nrm.z, lw.m20 * nrm.x + lw.m21 * nrm.y + lw.m22 * nrm.z);
        var p = (mesh.Pos[a] + mesh.Pos[b] + mesh.Pos[c]) / 3f;
        var wp = new Vector3(lw.m00 * p.x + lw.m01 * p.y + lw.m02 * p.z + lw.m03, lw.m10 * p.x + lw.m11 * p.y + lw.m12 * p.z + lw.m13, lw.m20 * p.x + lw.m21 * p.y + lw.m22 * p.z + lw.m23);
        return Vector3.Dot(w, cam - wp) < 0;
    }
    /// <summary>The end summary (Boot), when anything was culled or the census is on.</summary>
    public static string Summary() => On && (Draws > 0 || LogOn) ? "[g6] honour Cull: " + Draws + " mesh draw(s) culled " + Culled + " triangle(s) in all (SHEPHERD_K7_G6=0: every triangle drawn, kit #6)" : null;
}
