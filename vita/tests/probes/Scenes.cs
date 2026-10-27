// Kit #7 tests track (vita/notes/kit7-tests.md "One-feature test scenes + a reference renderer", from RT64 / N64 Recompiled's method:
// test scenes of one feature each, a reference renderer, no per-scene renderer hacks). Each scene is built by the SAME code on both sides
// from solid-colour sprites / UI images placed on a 16-pixels-per-unit grid (every quad edge falls exactly between two pixel centres, so
// rasterisation rules cannot disagree) and rendered by one orthographic camera into a W x H = 960 x 544 target (the Vita's screen):
//   Unity (the oracle player, batchmode with graphics): Camera.Render into a RenderTexture, read back -> <out>/<scene>.unity.rgba
//   Shepherd (the Mac test host, vita/tests/TestHost "scene"): the engine's SceneRenderer draws into a recording backend -> the sorted draw
//            list (<out>/<scene>.draws.tsv: texture, blend, clip, every vertex) -> the reference renderer (a CPU rasteriser of exactly
//            those draws, TestHost/RefRaster.cs) -> <out>/<scene>.shepherd.rgba
// A mismatch then names its stage: the draw list (order / geometry / colour, read from the TSV) or the rasteriser.
// Scenes (each seeded: <scene>:<seed>):
//   order   sprites at random grid cells and sortingOrder, one z: the higher order on top
//   z       one sortingOrder, random z: the farther drawn first (Unity sorts transparent renderers back to front)
//   tie     one sortingOrder, one z: the tie order Unity applies (creation order? instance id?) -- recorded, compared
//   mixed   order + z + tie together, tinted (alpha 1)
//   alpha   overlapping half-transparent tints (Sprites-Default's premultiplied blend), 8-bit-exact colours
//   uimask  a Canvas (screen space - camera) with a Mask (stencil) parent and children that cross its edge
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ShepherdOracle
{
    public static partial class Scenes
    {
        public const int W = 960, H = 544, Ppu = 16;   // (the Vita's screen: Shepherd's Vita profile projects with Screen = 960 x 544; 1 unit = 16 px)
        public static readonly string[] All = { "order", "z", "tie", "mixed", "alpha", "uimask" };
        static Texture2D _white; static Sprite _sprite;
        public static Sprite WhiteSprite()
        {
            if (_sprite != null) return _sprite;
            _white = new Texture2D(16, 16, TextureFormat.RGBA32, false); var px = new Color32[256]; for (int i = 0; i < 256; i++) px[i] = new Color32(255, 255, 255, 255);
            _white.SetPixels32(px); _white.filterMode = FilterMode.Point; _white.Apply();
            _sprite = Sprite.Create(_white, new Rect(0, 0, 16, 16), new Vector2(0.5f, 0.5f), Ppu, 0, SpriteMeshType.FullRect);
            return _sprite;
        }
        static Color32 C(Rng r, byte a = 255) => new Color32((byte)(32 + r.Int(7) * 32), (byte)(32 + r.Int(7) * 32), (byte)(32 + r.Int(7) * 32), a);

        /// <summary>Builds scene `name` with `seed`; returns the camera and the objects made (destroyed by the caller). The camera looks down +z
        /// from z = -10, orthographic, 1 unit = 16 px, background opaque black.</summary>
        public static Camera Build(string name, int seed, List<GameObject> made, StringBuilder log)
        {
            var r = new Rng((ulong)seed * 0x9E3779B97F4A7C15UL + (ulong)name.Length);
            var camGo = new GameObject("SceneCam"); made.Add(camGo); var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = H / 2f / Ppu; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0f, 0f, 0f, 1f);
            cam.nearClipPlane = 0.3f; cam.farClipPlane = 100f; camGo.transform.position = new Vector3(0f, 0f, -10f);
            var spr = WhiteSprite();
            int n = 4 + r.Int(8);
            if (name == "uimask") { BuildMask(r, cam, made, log); return cam; }
            for (int i = 0; i < n; i++)
            {
                var g = new GameObject("q" + i); made.Add(g); var sr = g.AddComponent<SpriteRenderer>(); sr.sprite = spr;
                int w = 1 + r.Int(4), h = 1 + r.Int(3); g.transform.localScale = new Vector3(w, h, 1f);
                // centre so that every edge is on an integer unit: (w odd -> x + 0.5) ...
                float x = r.Int(12) - 6 + ((w & 1) == 1 ? 0.5f : 0f), y = r.Int(8) - 4 + ((h & 1) == 1 ? 0.5f : 0f);
                float z = name == "z" || name == "mixed" ? r.Int(9) - 4 : 0f;
                g.transform.position = new Vector3(x, y, z);
                sr.sortingOrder = name == "order" || name == "mixed" ? r.Int(6) - 2 : 0;
                sr.color = name == "alpha" ? (Color)C(r, (byte)(64 + r.Int(3) * 64)) : (Color)C(r);
                log.Append("q").Append(i).Append(" pos ").Append(x).Append(',').Append(y).Append(',').Append(z).Append(" size ").Append(w).Append('x').Append(h).Append(" order ").Append(sr.sortingOrder).Append(" color ").Append(((Color32)sr.color).ToString()).Append('\n');
            }
            return cam;
        }
        static void BuildMask(Rng r, Camera cam, List<GameObject> made, StringBuilder log)
        {
            var cgo = new GameObject("Canvas", typeof(RectTransform)); made.Add(cgo); var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = cam; canvas.planeDistance = 5f;
            var maskGo = new GameObject("Mask", typeof(RectTransform)); made.Add(maskGo); maskGo.transform.SetParent(cgo.transform, false);
            var mrt = (RectTransform)maskGo.transform; mrt.sizeDelta = new Vector2(16 * (3 + r.Int(4)), 16 * (2 + r.Int(3))); mrt.anchoredPosition = new Vector2(16 * (r.Int(5) - 2), 16 * (r.Int(3) - 1));
            var mimg = maskGo.AddComponent<UnityEngine.UI.Image>(); mimg.sprite = WhiteSprite(); mimg.color = new Color32(64, 64, 64, 255);
            var mask = maskGo.AddComponent<UnityEngine.UI.Mask>(); mask.showMaskGraphic = r.Bool();
            int n = 2 + r.Int(4);
            for (int i = 0; i < n; i++)
            {
                var g = new GameObject("img" + i, typeof(RectTransform)); made.Add(g); g.transform.SetParent(maskGo.transform, false);
                var rt = (RectTransform)g.transform; rt.sizeDelta = new Vector2(16 * (1 + r.Int(5)), 16 * (1 + r.Int(4))); rt.anchoredPosition = new Vector2(16 * (r.Int(9) - 4), 16 * (r.Int(5) - 2));
                var img = g.AddComponent<UnityEngine.UI.Image>(); img.sprite = WhiteSprite(); img.color = C(r);
                log.Append("img").Append(i).Append(" size ").Append(rt.sizeDelta.ToString()).Append(" at ").Append(rt.anchoredPosition.ToString()).Append('\n');
            }
            log.Append("mask ").Append(mrt.sizeDelta.ToString()).Append(" at ").Append(mrt.anchoredPosition.ToString()).Append(" showGraphic ").Append(mask.showMaskGraphic).Append('\n');
        }

#if UNITY_2019_1_OR_NEWER
        /// <summary>The oracle player: each scene (name or name:seed; "all" = every scene with seeds 1..4) rendered and read back.</summary>
        public static IEnumerator RunUnity(string names, string outDir, StringBuilder log)
        {
            var list = new List<string>();
            foreach (var n in (names == "all" ? string.Join(",", All) : names).Split(','))
                if (n.Contains(":")) list.Add(n); else for (int s = 1; s <= 4; s++) list.Add(n + ":" + s);
            foreach (var spec in list)
            {
                var p = spec.Split(':'); var made = new List<GameObject>(); var slog = new StringBuilder();
                var cam = Build(p[0], int.Parse(p[1]), made, slog);
                yield return null;   // (a frame: the UI's layout and its canvas meshes are built in the frame's update)
                var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                cam.targetTexture = rt; cam.Render();
                RenderTexture.active = rt; var tex = new Texture2D(W, H, TextureFormat.RGBA32, false); tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply(); RenderTexture.active = null;
                var px = tex.GetPixels32(); var bytes = new byte[W * H * 4];
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { var c = px[(H - 1 - y) * W + x]; int o = (y * W + x) * 4; bytes[o] = c.r; bytes[o + 1] = c.g; bytes[o + 2] = c.b; bytes[o + 3] = c.a; }   // top row first
                string tag = p[0] + "-" + p[1];
                File.WriteAllBytes(Path.Combine(outDir, tag + ".unity.rgba"), bytes); File.WriteAllBytes(Path.Combine(outDir, tag + ".unity.png"), tex.EncodeToPNG());
                File.WriteAllText(Path.Combine(outDir, tag + ".scene.txt"), slog.ToString());
                cam.targetTexture = null; UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(tex);
                foreach (var g in made) if (g != null) UnityEngine.Object.Destroy(g);
                log.Append("scene ").Append(tag).Append(" rendered by ").Append(SystemInfo.graphicsDeviceType).Append('\n');
                yield return null;
            }
        }
#endif
    }
}
