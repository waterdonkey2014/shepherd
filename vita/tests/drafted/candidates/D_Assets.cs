// Kit #7 tests track: MACHINE-DRAFTED probe candidates (see D_Objects.cs). Area: runtime-made assets the game makes or reads -- Texture2D
// pixels and formats, Sprite.Create geometry, Mesh data and bounds, MaterialPropertyBlock values, Camera projection maths (with an
// explicit aspect, so the screen size of either side does not enter).
using System;
using UnityEngine;

namespace ShepherdOracle.Drafted
{
    /// <summary>Texture2D made in code: SetPixel(s) / GetPixel / GetPixelBilinear / GetPixels32 / Apply, sizes, formats, wrap / filter modes.</summary>
    public static class D_Texture
    {
        public static void Register() { Suites.All["d-asset-texture"] = Case; }
        static void Case(Rng r, Out o)
        {
            int w = 1 + r.Int(16), h = 1 + r.Int(16); var fmt = r.Pick(new[] { TextureFormat.RGBA32, TextureFormat.ARGB32, TextureFormat.RGB24, TextureFormat.Alpha8 });
            Texture2D t = null;
            try
            {
                t = new Texture2D(w, h, fmt, r.Bool());
                o.Raw("info", t.width + "x" + t.height + " " + t.format + " mips " + t.mipmapCount + " readable " + t.isReadable);
                var px = new Color32[w * h]; for (int i = 0; i < px.Length; i++) px[i] = new Color32((byte)r.Int(256), (byte)r.Int(256), (byte)r.Int(256), (byte)r.Int(256));
                t.SetPixels32(px); t.Apply();
                int x = r.Int(w), y = r.Int(h);
                o.Raw("GetPixel", DW.C(t.GetPixel(x, y))); o.Raw("GetPixelOut", DW.C(t.GetPixel(x + w, y - h)));
                t.wrapMode = r.Bool() ? TextureWrapMode.Clamp : TextureWrapMode.Repeat; t.filterMode = r.Bool() ? FilterMode.Point : FilterMode.Bilinear;
                o.Raw("Bilinear", DW.C(t.GetPixelBilinear(r.Unit() * 1.5f - 0.25f, r.Unit())));
                var back = t.GetPixels32(); uint hsh = 2166136261; foreach (var c in back) { hsh = (hsh ^ c.r) * 16777619; hsh = (hsh ^ c.g) * 16777619; hsh = (hsh ^ c.b) * 16777619; hsh = (hsh ^ c.a) * 16777619; }
                o.Raw("pixels32", back.Length + ":" + hsh.ToString("x8"));
                t.SetPixel(x, y, new Color(0.1f, 0.2f, 0.3f, 0.4f)); o.Raw("SetPixel", DW.C(t.GetPixel(x, y)));
                o.Raw("texel", Out.H(t.texelSize.x) + "," + Out.H(t.texelSize.y));
            }
            catch (Exception e) { o.Exc("texture", e); }
            finally { if (t != null) UnityEngine.Object.DestroyImmediate(t); }
        }
    }

    /// <summary>Sprite.Create on a code texture: rect, pivot, bounds, pixelsPerUnit, border, textureRect, vertices / uv / triangles.</summary>
    public static class D_Sprite
    {
        public static void Register() { Suites.All["d-asset-sprite"] = Case; }
        static void Case(Rng r, Out o)
        {
            Texture2D t = null; Sprite s = null;
            try
            {
                int w = 8 + r.Int(64), h = 8 + r.Int(64); t = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var rect = new Rect(r.Int(w / 2), r.Int(h / 2), 1 + r.Int(w / 2), 1 + r.Int(h / 2)); var pivot = new Vector2(r.Unit(), r.Unit()); float ppu = r.Pick(new[] { 100f, 1f, 16f, 32f, 64.5f });
                var border = new Vector4(r.Int(3), r.Int(3), r.Int(3), r.Int(3));
                s = Sprite.Create(t, rect, pivot, ppu, 0, SpriteMeshType.FullRect, border);
                o.Raw("rect", Out.H(s.rect.x) + "," + Out.H(s.rect.y) + "," + Out.H(s.rect.width) + "," + Out.H(s.rect.height));
                o.Raw("pivot", Out.H(s.pivot.x) + "," + Out.H(s.pivot.y)); o.F("ppu", s.pixelsPerUnit); o.Raw("border", DW.V(s.border));
                o.Raw("bounds", DW.V(s.bounds.center) + "|" + DW.V(s.bounds.extents));
                o.Raw("textureRect", Out.H(s.textureRect.x) + "," + Out.H(s.textureRect.width)); o.B("packed", s.packed);
                var v = s.vertices; var uv = s.uv; var tri = s.triangles;
                o.I("counts", v.Length * 10000 + uv.Length * 100 + tri.Length);
                for (int i = 0; i < v.Length && i < 4; i++) o.Raw("v" + i, Out.H(v[i].x) + "," + Out.H(v[i].y) + "|" + Out.H(uv[i].x) + "," + Out.H(uv[i].y));
                o.S("tri", string.Join(",", Array.ConvertAll(tri, x => x.ToString())));
            }
            catch (Exception e) { o.Exc("sprite", e); }
            finally { if (s != null) UnityEngine.Object.DestroyImmediate(s); if (t != null) UnityEngine.Object.DestroyImmediate(t); }
        }
    }

    /// <summary>Mesh data: vertices / triangles / colors / uv round trips, RecalculateBounds, bounds, sub-meshes, GetIndices, the index format.</summary>
    public static class D_Mesh
    {
        public static void Register() { Suites.All["d-asset-mesh"] = Case; }
        static void Case(Rng r, Out o)
        {
            var m = new Mesh();
            try
            {
                int n = 3 + r.Int(20); var v = new Vector3[n]; var uv = new Vector2[n];
                for (int i = 0; i < n; i++) { v[i] = new Vector3(Gen.FM(r) * 0.01f, Gen.FM(r) * 0.01f, r.Chance(50) ? 0f : Gen.FM(r) * 0.01f); uv[i] = new Vector2(r.Unit(), r.Unit()); }
                var tri = new int[(n / 3) * 3]; for (int i = 0; i < tri.Length; i++) tri[i] = r.Int(n);
                m.vertices = v; m.uv = uv; m.triangles = tri;
                o.Raw("bounds", DW.V(m.bounds.center) + "|" + DW.V(m.bounds.size));
                m.RecalculateBounds(); o.Raw("bounds2", DW.V(m.bounds.center) + "|" + DW.V(m.bounds.size));
                o.I("vertexCount", m.vertexCount); o.I("subMeshCount", m.subMeshCount); o.I("indexFormat", (int)m.indexFormat);
                o.S("tri", string.Join(",", Array.ConvertAll(m.triangles, x => x.ToString())));
                m.subMeshCount = 2; m.SetIndices(new[] { 0, 1, 2, 1 }, MeshTopology.Lines, 1);
                o.S("indices1", string.Join(",", Array.ConvertAll(m.GetIndices(1), x => x.ToString()))); o.I("topology1", (int)m.GetTopology(1));
                var cols = new Color32[n]; for (int i = 0; i < n; i++) cols[i] = new Color32((byte)i, 2, 3, 4); m.colors32 = cols; o.I("colors", m.colors32.Length);
                m.Clear(); o.I("afterClear", m.vertexCount); o.Raw("boundsCleared", DW.V(m.bounds.size));
            }
            catch (Exception e) { o.Exc("mesh", e); }
            finally { UnityEngine.Object.DestroyImmediate(m); }
        }
    }

    /// <summary>MaterialPropertyBlock (no shader needed): Set / Get float, color, vector by name and by id, Clear, isEmpty, HasProperty.</summary>
    public static class D_PropertyBlock
    {
        public static void Register() { Suites.All["d-asset-propblock"] = Case; }
        static void Case(Rng r, Out o)
        {
            var b = new MaterialPropertyBlock();
            o.B("empty0", b.isEmpty);
            string nf = "_F" + r.Int(5), nc = "_C" + r.Int(5), nv = "_V" + r.Int(5);
            float f = Gen.F(r); var c = new Color(r.Unit(), r.Unit(), r.Unit(), r.Unit()); var v = new Vector4(Gen.FM(r), 1f, 2f, 3f);
            b.SetFloat(nf, f); b.SetColor(nc, c); b.SetVector(nv, v);
            o.B("empty1", b.isEmpty); o.F("getFloat", b.GetFloat(nf)); o.Raw("getColor", DW.C(b.GetColor(nc))); o.Raw("getVector", DW.V(b.GetVector(nv)));
            o.F("getMissing", b.GetFloat("_Missing")); o.Raw("getMissingColor", DW.C(b.GetColor("_MissingC")));
            o.F("byId", b.GetFloat(Shader.PropertyToID(nf))); o.B("hasFloat", b.HasFloat(nf)); o.B("hasColorAsFloat", b.HasFloat(nc));
            b.SetFloat(nf, f + 1f); o.F("overwrite", b.GetFloat(nf));
            b.SetInt("_I", r.Int(1000)); o.I("getInt", b.GetInt("_I"));
            b.Clear(); o.B("empty2", b.isEmpty); o.F("afterClear", b.GetFloat(nf));
        }
    }

    /// <summary>Camera projection maths with an explicit aspect and no screen dependence: projectionMatrix (perspective / orthographic),
    /// worldToCameraMatrix, cameraToWorldMatrix, and the viewport conversions (ViewportToWorldPoint / WorldToViewportPoint).</summary>
    public static class D_Camera
    {
        public static void Register() { Suites.All["d-asset-camera"] = Case; }
        static string M(Matrix4x4 m) { var sb = new System.Text.StringBuilder(); for (int i = 0; i < 16; i++) { if (i > 0) sb.Append(','); sb.Append(Out.H(m[i])); } return sb.ToString(); }
        static void Case(Rng r, Out o)
        {
            var g = new GameObject("cam");
            try
            {
                var cam = g.AddComponent<Camera>(); cam.enabled = false;
                g.transform.SetPositionAndRotation(new Vector3(r.Exact(-18), r.Exact(-18), r.Exact(-18)), Quaternion.Euler(r.Int(90) - 45, r.Int(360), 0));
                cam.orthographic = r.Bool(); cam.aspect = 0.5f + r.Unit() * 2f; cam.nearClipPlane = 0.01f + r.Unit(); cam.farClipPlane = cam.nearClipPlane + 10f + r.Unit() * 500f;
                if (cam.orthographic) cam.orthographicSize = 0.5f + r.Unit() * 20f; else cam.fieldOfView = 10f + r.Unit() * 120f;
                o.Raw("in", (cam.orthographic ? "ortho " : "persp ") + Out.H(cam.aspect) + " " + Out.H(cam.nearClipPlane) + " " + Out.H(cam.farClipPlane));
                o.Raw("proj", M(cam.projectionMatrix)); o.Raw("w2c", M(cam.worldToCameraMatrix)); o.Raw("c2w", M(cam.cameraToWorldMatrix));
                var vp = new Vector3(r.Unit(), r.Unit(), cam.nearClipPlane + r.Unit() * 10f);
                o.Raw("v2w", DW.V(cam.ViewportToWorldPoint(vp)));
                var wp = g.transform.position + g.transform.forward * (2f + r.Unit() * 10f) + new Vector3(r.Unit(), r.Unit(), 0f);
                o.Raw("w2v", DW.V(cam.WorldToViewportPoint(wp)));
                var ray = cam.ViewportPointToRay(new Vector3(r.Unit(), r.Unit(), 0f)); o.Raw("vray", DW.V(ray.origin) + "|" + DW.V(ray.direction));
                o.F("fov", cam.fieldOfView); o.F("ortho", cam.orthographicSize); o.I("depth", (int)cam.depth); o.I("clearFlags", (int)cam.clearFlags);
                o.Raw("bg", DW.C(cam.backgroundColor)); o.I("cullingMask", cam.cullingMask);
            }
            catch (Exception e) { o.Exc("camera", e); }
            finally { UnityEngine.Object.DestroyImmediate(g); }
        }
    }
}
