using System.Reflection;

namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>kit #7 spine track, "native clipping" test (Mac only, never in an eboot): `Shepherd.Engine spineclipfuzz [cases] [seed]` drives the
/// game's own Spine clipping code (Spine.SkeletonClipping / Triangulator from spine-unity.dll, by reflection: ClipStart's polygon work --
/// MakeClockwise, Triangulate, Decompose, each polygon made clockwise and closed -- then ClipTriangles) and the native port
/// (SHEPHERD_SPINE_CLIP_LIB, the dylib built from engine/Derived/native) on the same random inputs -- convex and concave clipping polygons of
/// 3-24 vertices, triangle soups inside, across and outside them, near-degenerate (collinear, tiny, huge) cases -- and compares every output
/// float's bits, every count and index. Exit 0 = no difference.</summary>
[ProfileOnly]
public static class SpineClipFuzz
{
    public static bool Handles(string mode) => mode == "spineclipfuzz";
    const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static int Run(string[] args)
    {
        int cases = args.Length > 1 ? int.Parse(args[1]) : 20000, seed = args.Length > 2 ? int.Parse(args[2]) : 7;
        var dll = Path.Combine(Program.GameManaged, "spine-unity.dll");
        // Vita host (a SHEPHERD_ENGINE_TOOLS=1 eboot under vlx: review fix R2, the ARM32 fuzz): by name -- the preload hook finds the card's
        // copy; LoadFrom turns "ux0:..." into "/ux0:..." and fails
        var asm = VitaHost.On ? Assembly.Load("spine-unity") : Assembly.LoadFrom(dll);
        Type T(string n) => asm.GetType(n, true);
        var scT = T("Spine.SkeletonClipping"); var trT = T("Spine.Triangulator"); var elF = T("Spine.ExposedList`1").MakeGenericType(typeof(float));
        var elI = T("Spine.ExposedList`1").MakeGenericType(typeof(int)); var elL = T("Spine.ExposedList`1").MakeGenericType(elF);
        var makeCw = scT.GetMethod("MakeClockwise", S); var triangulate = trT.GetMethod("Triangulate"); var decompose = trT.GetMethod("Decompose"); var clipTris = scT.GetMethod("ClipTriangles");
        FieldInfo F(Type t, string n) => t.GetField(n, I) ?? throw new MissingFieldException(t.Name, n);
        var fPolys = F(scT, "clippingPolygons"); var fTri = F(scT, "triangulator"); var fOutV = F(scT, "clippedVertices"); var fOutUV = F(scT, "clippedUVs"); var fOutT = F(scT, "clippedTriangles");
        var fItemsF = F(elF, "Items"); var fCountF = F(elF, "Count"); var fItemsI = F(elI, "Items"); var fCountI = F(elI, "Count"); var fItemsL = F(elL, "Items"); var fCountL = F(elL, "Count");
        var addF = elF.GetMethod("Add"); var clearF = elF.GetMethod("Clear");
        // the native port through SpineClipNative's Mac binding (the same entry points the eboot links)
        if (!Runtime.SpineClipNativeTest.Load(out var why)) { Log.Error("[spineclipfuzz] no native port: " + why); return 2; }
        var rnd = new Random(seed); long starts = 0, tris = 0, triangles = 0, bad = 0, clippedOut = 0, declined = 0;
        var clipper = Activator.CreateInstance(scT);
        for (int c = 0; c < cases; c++)
        {
            // a clipping polygon: convex (sorted angles) or a random star / zigzag (concave), sometimes collinear or tiny / huge
            int nv = 3 + rnd.Next(22); float scale = rnd.Next(10) switch { 0 => 1e-4f, 1 => 1e4f, _ => 1f + (float)rnd.NextDouble() * 50f };
            float cx = (float)(rnd.NextDouble() * 20 - 10), cy = (float)(rnd.NextDouble() * 20 - 10);
            var poly = new float[nv * 2]; int kind = rnd.Next(4);
            for (int i = 0; i < nv; i++)
            {
                double ang = kind == 0 ? 2 * Math.PI * i / nv : 2 * Math.PI * (i + rnd.NextDouble() * 0.8) / nv;
                double rad = kind switch { 0 => 1.0, 1 => 0.3 + rnd.NextDouble(), 2 => (i % 2 == 0 ? 1.0 : 0.35), _ => rnd.NextDouble() < 0.1 ? 0.0 : 0.5 + rnd.NextDouble() };
                if (rnd.Next(2) == 0) ang = -ang;   // either winding
                poly[2 * i] = cx + (float)(Math.Cos(ang) * rad) * scale; poly[2 * i + 1] = cy + (float)(Math.Sin(ang) * rad) * scale;
                if (rnd.Next(40) == 0 && i > 0) { poly[2 * i] = poly[2 * i - 2]; poly[2 * i + 1] = poly[2 * i - 1]; }   // a repeated vertex
            }
            // managed ClipStart's polygon work on a copy (the clipper's own list), the port on another
            var lst = Activator.CreateInstance(elF); foreach (var x in poly) addF.Invoke(lst, new object[] { x });
            makeCw.Invoke(null, new[] { lst });
            var tr = fTri.GetValue(clipper);
            var triIdx = triangulate.Invoke(tr, new[] { lst });
            var polys = decompose.Invoke(tr, new[] { lst, triIdx });
            int np = (int)fCountL.GetValue(polys); var pitems = (Array)fItemsL.GetValue(polys);
            var mpoly = new List<float[]>();
            for (int p = 0; p < np; p++) { var pl = pitems.GetValue(p); makeCw.Invoke(null, new[] { pl }); var it = (float[])fItemsF.GetValue(pl); addF.Invoke(pl, new object[] { it[0] }); it = (float[])fItemsF.GetValue(pl); addF.Invoke(pl, new object[] { it[1] }); int cnt = (int)fCountF.GetValue(pl); mpoly.Add(((float[])fItemsF.GetValue(pl))[..cnt]); }
            var listAfter = ((float[])fItemsF.GetValue(lst))[..(int)fCountF.GetValue(lst)];
            starts++;
            var npoly = (float[])poly.Clone();
            int nnp = Runtime.SpineClipNativeTest.Start(npoly, poly.Length, out var nPolys, out var nCounts);
            if (nnp < 0) { declined++; continue; }
            string err = null;
            if (!Same(listAfter, npoly, poly.Length)) err = "the polygon list after MakeClockwise differs";
            else if (nnp != np) err = $"{np} managed polygons, {nnp} native";
            else { int off = 0; for (int p = 0; p < np && err == null; p++) { if (mpoly[p].Length != nCounts[p]) err = $"polygon {p} length"; else if (!Same(mpoly[p], nPolys[off..(off + nCounts[p])], nCounts[p])) err = $"polygon {p} floats"; off += nCounts[p]; } }
            if (err != null) { bad++; if (bad <= 10) Log.Warn($"[spineclipfuzz] case {c} ClipStart: {err}"); continue; }
            // triangles: a soup in a box around the polygon
            int nt = 1 + rnd.Next(12), nverts = 3 + rnd.Next(10);
            var verts = new float[nverts * 2]; var uvs = new float[nverts * 2];
            for (int i = 0; i < verts.Length; i++) { verts[i] = (i % 2 == 0 ? cx : cy) + (float)(rnd.NextDouble() * 3 - 1.5) * scale; uvs[i] = (float)rnd.NextDouble(); }
            if (rnd.Next(10) == 0) { verts[2] = verts[0]; verts[3] = verts[1]; }   // a degenerate triangle
            var idx = new int[nt * 3]; for (int i = 0; i < idx.Length; i++) idx[i] = rnd.Next(nverts);
            fPolys.SetValue(clipper, polys);
            clipTris.Invoke(clipper, new object[] { verts, verts.Length, idx, idx.Length, uvs });
            var mv = (float[])fItemsF.GetValue(fOutV.GetValue(clipper)); int mvc = (int)fCountF.GetValue(fOutV.GetValue(clipper));
            var mu = (float[])fItemsF.GetValue(fOutUV.GetValue(clipper));
            var mt = (int[])fItemsI.GetValue(fOutT.GetValue(clipper)); int mtc = (int)fCountI.GetValue(fOutT.GetValue(clipper));
            fPolys.SetValue(clipper, null);
            tris++; triangles += nt;
            var r = Runtime.SpineClipNativeTest.Tris(verts, idx, uvs, nPolys, nCounts, nnp);
            if (r.NV != mvc) err = $"vertex count managed {mvc} native {r.NV}";
            else if (!Same(mv, r.V, mvc)) err = "vertex floats";
            else if (!Same(mu, r.UV, mvc)) err = "uv floats";
            else if (r.NT != mtc) err = $"index count managed {mtc} native {r.NT}";
            else { for (int i = 0; i < mtc; i++) if (mt[i] != r.T[i]) { err = $"index {i}"; break; } }
            if (err == null && mt.Length < r.MaxT) err = $"triangle list capacity {mt.Length} < native largest {r.MaxT}";
            if (mvc != verts.Length / 2 * 2 || mtc != idx.Length) clippedOut++;
            if (err != null) { bad++; if (bad <= 10) Log.Warn($"[spineclipfuzz] case {c} ClipTriangles: {err}"); }
        }
        Log.Info($"[spineclipfuzz] {cases} cases (seed {seed}): {starts} ClipStart compared ({declined} declined by the port), {tris} ClipTriangles compared ({triangles} triangles, {clippedOut} with clipping changing the output): {bad} differ");
        return bad == 0 ? 0 : 1;
    }
    static bool Same(float[] a, float[] b, int n) { for (int i = 0; i < n; i++) if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false; return true; }
}
#else
[ProfileOnly]
public static class SpineClipFuzz { public static bool Handles(string mode) => false; public static int Run(string[] args) => 2; }
#endif
