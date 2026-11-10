using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>Kit #12 menusa, review fixes: <c>k12uitest</c> -- the UI replay (SHEPHERD_K12_UIREPLAY's backend half, Render/GlBackend.K12.cs) against
/// the plain draw path on a headless GL backend, frame by frame, on synthetic UI passes that reach what the game's reachable menus do not:
/// - static: every block unchanged (full replays);
/// - prefix: one block rewritten every frame, a second one every third frame, an unkeyed draw (incremental prefix takes), a frame with one
///   draw more and one with one draw fewer;
/// - grab-mid: a BlendModes (grab) draw after two blocks, every frame (its flush inside the pass);
/// - grab-first: a world grab draw with one mode, then a UI grab draw with another mode as the pass's first draw (the backdrop state);
/// - target-mid: two blocks, a draw into a render target and back, a draw sampling that target (a flush inside the pass, no grab).
/// Each scenario runs twice -- the UI draws through Draw (kit #11), then through K12UiBegin / K12Draw / K12UiEnd -- and every frame's main
/// target is read back and compared byte for byte. PASS = every frame identical, every scenario exercised what it is for (replays,
/// prefix takes, grab copies), and with SHEPHERD_K12_UICHECK=1 no replay / prefix check difference (and prefix takes compared). Negative
/// controls: SHEPHERD_K12_NEGCTL=replay / prefix must FAIL (frames differ; with UICHECK the checks differ too). Mac only (compiled out of
/// the eboots). Exit code 0 = PASS.</summary>
[ProfileOnly]
public static class K12UiTest
{
#if !SHEPHERD_EBOOT
    public static bool Handles(string mode) => mode == "k12uitest";
    const int W = 320, H = 240;
    static readonly int[] Q2 = { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };

    /// <summary>A retained UI block: its vertex array is its key (rewritten in place when its version changes, as the UI cache does).</summary>
    sealed class Block { public Vertex[] V = new Vertex[8]; public int Ver = -1; }
    enum Op { Draw, DrawUnkeyed, Target, Main }
    readonly struct Step
    {
        public readonly Op Op; public readonly int Block, Ver, Tex; public readonly uint Blend; public readonly bool Clip;
        public Step(Op op, int block = 0, int ver = 0, int tex = 0, uint blend = BLENDMODE_BLEND, bool clip = false) { Op = op; Block = block; Ver = ver; Tex = tex; Blend = blend; Clip = clip; }
    }
    sealed class Scenario { public string Name; public int Frames; public Func<int, List<Step>> Ui; public Func<int, uint> WorldGrab; public string Wants; }

    static void Fill(Block b, int id, int ver)
    {
        if (b.Ver == ver) return;
        b.Ver = ver;
        float x = id == 6 ? 6 : 12 + id % 6 * 50, y = id == 6 ? 5 : 14 + id / 6 * 60, w = 44 + ver % 3 * 3, h = 40 + ver % 2 * 4;   // (6: inside the 64 x 64 target)
        float r = 0.2f + 0.13f * (id % 5), gg = 0.25f + 0.07f * ((id + ver) % 7), bb = 0.9f - 0.11f * (ver % 6), a = id % 3 == 0 ? 1f : 0.7f;
        for (int q = 0; q < 2; q++)
        {
            float ox = x + q * 6, oy = y + q * 9, s = q == 0 ? 1f : 0.55f;
            for (int k = 0; k < 4; k++)
            {
                ref var v = ref b.V[q * 4 + k];
                float px = k == 1 || k == 2 ? ox + w * s : ox, py = k >= 2 ? oy + h * s : oy;
                v.position.x = px; v.position.y = py;
                v.color.r = r; v.color.g = q == 0 ? gg : 1f - gg; v.color.b = bb; v.color.a = a;
                v.tex_coord.x = k == 1 || k == 2 ? 1f : 0f; v.tex_coord.y = k >= 2 ? 1f : 0f;
            }
        }
    }

    static byte[] Pattern(int w, int h, int seed)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4; bool c = ((x >> 2) + (y >> 2) + seed) % 2 == 0;
            px[i] = (byte)(c ? 230 : 40 + seed * 30); px[i + 1] = (byte)(c ? 90 + seed * 40 : 200); px[i + 2] = (byte)(60 + x * 8); px[i + 3] = (byte)(seed == 2 ? 160 + y * 4 : 255);
        }
        return px;
    }

    static uint Grab(int mode) => ShaderPolicy.GrabFlag | ((uint)mode << 8);

    static Scenario[] Scenarios() => new[]
    {
        new Scenario { Name = "static", Frames = 8, Wants = "replays", Ui = f =>
        {
            var s = new List<Step>();
            for (int b = 0; b < 6; b++) s.Add(new Step(Op.Draw, b, 0, b % 3, b == 4 ? BLENDMODE_ADD : BLENDMODE_BLEND, clip: b == 2));
            return s;
        } },
        new Scenario { Name = "prefix", Frames = 14, Wants = "prefix takes", Ui = f =>
        {
            var s = new List<Step>();
            int n = f == 9 ? 8 : f == 11 ? 6 : 7;   // (one frame a draw more, one a draw fewer)
            for (int b = 0; b < n; b++)
            {
                int ver = b == 3 ? f : b == 1 ? f / 3 : 0;   // (block 3 rewritten every frame, block 1 every third)
                s.Add(b == 5 ? new Step(Op.DrawUnkeyed, b, f % 2, 1) : new Step(Op.Draw, b, ver, b % 2, clip: b == 6));
            }
            return s;
        } },
        new Scenario { Name = "grab-mid", Frames = 8, Wants = "grabs", Ui = f => new List<Step>
        {
            new(Op.Draw, 0, 0, 0), new(Op.Draw, 1, 0, 1), new(Op.Draw, 2, 0, 3, Grab(7)), new(Op.Draw, 3, 0, 2),
        } },
        new Scenario { Name = "grab-first", Frames = 8, Wants = "grabs", WorldGrab = f => Grab(1), Ui = f => new List<Step>
        {
            new(Op.Draw, 2, 0, 3, Grab(10)), new(Op.Draw, 0, 0, 0), new(Op.Draw, 1, 0, 1),
        } },
        new Scenario { Name = "target-mid", Frames = 8, Wants = "nothing", Ui = f => new List<Step>
        {
            new(Op.Draw, 0, 0, 0), new(Op.Draw, 1, 0, 1), new(Op.Target), new(Op.Draw, 6, 0, 2), new(Op.Main), new(Op.Draw, 3, 0, 4), new(Op.Draw, 7, 0, 5),
        } },
    };

    /// <summary>One scenario on a fresh backend; the UI draws plain (replay false) or through the recording. Returns every frame's pixels.</summary>
    static List<byte[]> RunOnce(Scenario sc, bool replay, out int grabs)
    {
        var frames = new List<byte[]>(); grabs = 0;
        using var g = new GlBackend(W, H, headless: true);
        var tex = new IntPtr[6];
        tex[0] = g.CreateTexture(16, 16, Pattern(16, 16, 0), linear: false);
        tex[1] = g.CreateTexture(16, 16, Pattern(16, 16, 1), linear: true);
        tex[2] = g.CreateTexture(16, 16, Pattern(16, 16, 2), linear: false);
        tex[3] = g.CreateTexture(1, 1, new byte[] { 255, 255, 255, 255 }, linear: false);
        tex[4] = g.CreateTarget(64, 64);
        tex[5] = tex[4];
        var blocks = new Block[8]; for (int i = 0; i < blocks.Length; i++) blocks[i] = new Block();
        Block wq = new(), wg = new(); Fill(wq, 11, 0); Fill(wg, 10, 0);   // (the world's own quads)
        var unkeyed = new Vertex[8];
        var bg = new Vertex[4];
        for (int f = 0; f < sc.Frames; f++)
        {
            g.Begin(0.08f, 0.1f, 0.12f, 1f);
            for (int k = 0; k < 4; k++)
            {   // the world: a gradient and a textured quad (the grab draws' backdrop)
                ref var v = ref bg[k]; float x = k == 1 || k == 2 ? W : 0, y = k >= 2 ? H : 0;
                v.position.x = x; v.position.y = y; v.color.r = x / W; v.color.g = 0.3f + 0.4f * y / H; v.color.b = 0.6f - 0.3f * x / W; v.color.a = 1f;
                v.tex_coord.x = v.tex_coord.y = 0f;
            }
            g.Draw(IntPtr.Zero, BLENDMODE_NONE, false, bg, Q2.AsSpan(0, 6));
            g.Draw(tex[1], BLENDMODE_BLEND, false, wq.V, Q2);
            if (sc.WorldGrab != null) g.Draw(tex[3], sc.WorldGrab(f), false, wg.V, Q2);
            var rp = replay ? (IK12UiReplay)g : null;
            rp?.K12UiBegin();
            foreach (var st in sc.Ui(f))
            {
                switch (st.Op)
                {
                    case Op.Target: g.SetTarget(tex[4]); g.NoClip(); continue;
                    case Op.Main: g.SetTarget(IntPtr.Zero); continue;
                }
                if (st.Clip) g.Clip(40, 30, 200, 150); else g.NoClip();
                Vertex[] v; object key; bool unchanged;
                if (st.Op == Op.DrawUnkeyed)
                {   // a draw built in shared buffers (no key: never matches)
                    var b = new Block(); Fill(b, st.Block, st.Ver); Array.Copy(b.V, unkeyed, 8); v = unkeyed; key = null; unchanged = false;
                }
                else { var b = blocks[st.Block]; unchanged = b.Ver == st.Ver; Fill(b, st.Block, st.Ver); v = b.V; key = v; }
                if (rp != null) rp.K12Draw(key, unchanged, tex[st.Tex], st.Blend, false, v, Q2);
                else g.Draw(tex[st.Tex], st.Blend, false, v, Q2);
            }
            g.NoClip();
            rp?.K12UiEnd();
            g.EndFrame();
            grabs += g.Stats.Grabs;
            frames.Add(g.ReadPixels(out _, out _));
        }
        return frames;
    }

    public static int Run()
    {
        bool check = K12Ui.Check; int fails = 0;
        Log.Info($"[k12uitest] UI replay vs the plain draw path, headless GL {W}x{H}; check={(check ? 1 : 0)} NEGCTL={(K12Ui.NegCtl.Length > 0 ? K12Ui.NegCtl : "-")}");
        foreach (var sc in Scenarios())
        {
            var a = RunOnce(sc, false, out int ga);
            long rf = K12Ui.ReplayFrames, rk = K12Ui.ReplayRecorded, pt = K12Ui.ReplayDiffer, rc = K12Ui.ReplayChecked, rd = K12Ui.ReplayCheckDiff, pc = K12Ui.PrefixChecked, pd = K12Ui.PrefixCheckDiff;
            var b = RunOnce(sc, true, out int gb);
            rf = K12Ui.ReplayFrames - rf; rk = K12Ui.ReplayRecorded - rk; pt = K12Ui.ReplayDiffer - pt; rc = K12Ui.ReplayChecked - rc; rd = K12Ui.ReplayCheckDiff - rd; pc = K12Ui.PrefixChecked - pc; pd = K12Ui.PrefixCheckDiff - pd;
            int differ = 0, first = -1; long px = 0;
            for (int f = 0; f < a.Count; f++)
            {
                int n = 0; var x = a[f]; var y = b[f];
                for (int i = 0; i < x.Length; i += 4) if (x[i] != y[i] || x[i + 1] != y[i + 1] || x[i + 2] != y[i + 2] || x[i + 3] != y[i + 3]) n++;
                if (n > 0) { differ++; px += n; if (first < 0) first = f; }
            }
            string notRun = sc.Wants switch
            {
                "replays" when rf == 0 => "no frame replayed",
                "prefix takes" when pt == 0 => "no prefix take",
                "prefix takes" when check && pc == 0 => "no prefix take compared",
                "grabs" when ga == 0 || gb == 0 => "no grab draw ran (no blend-mode program?)",
                _ => null,
            };
            bool ok = differ == 0 && notRun == null && (!check || (rd == 0 && pd == 0));
            if (!ok) fails++;
            Log.Info($"[k12uitest] {sc.Name,-10} {(ok ? "PASS" : "FAIL")}: {a.Count} frames, {differ} differ ({px} pixels{(first >= 0 ? $", first frame {first}" : "")}){(notRun != null ? $"; NOT EXERCISED: {notRun}" : "")}; " +
                $"replayed {rf}, kept {rk}, prefix takes {pt}, grabs {ga} / {gb}; checks: replay {rd} differ of {rc}, prefix {pd} differ of {pc}");
        }
        Log.Info($"[k12uitest] RESULT: {(fails == 0 ? "PASS" : $"FAIL ({fails} scenarios)")}");
        return fails == 0 ? 0 : 1;
    }
#else
    public static bool Handles(string mode) => false;
    public static int Run() => 1;
#endif
}
