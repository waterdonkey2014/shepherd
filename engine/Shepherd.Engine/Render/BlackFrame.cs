using Shepherd.Engine.Native;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>kit #4 (M1, black-frame GC): is the frame on screen covered? A frame counts as covered when one of its draws is an opaque
/// quad over the whole screen -- four vertices whose bounds contain the screen, every vertex alpha >= 0.99, normal or no blending, and
/// an opaque texture (Texture2D.whiteTexture or a solid colour texture: what an untextured UI Image draws with) -- and what is drawn
/// after it covers at most <see cref="KitMaxOnTop"/> of the screen (the bounding boxes of the later draws, summed). That is what the
/// game's scene transitions show at their black hold (MMTransition's full-screen Images at alpha 1, "fade to black" / "white fade",
/// with the loading icon and its text on top); a screen with an opaque backdrop under real content (the splash logos, a book or
/// comic page over a black panel) is not covered. Only the main thread's draws are looked at: with
/// SHEPHERD_MT_RENDER=1 the screen cameras' draws are built on the render thread, the overlay canvases (where the transition's
/// Images live) still here. Nothing here changes a draw: it reads the vertices SceneRenderer hands the backend.</summary>
public static class BlackFrame
{
    /// <summary>The last completed frame -- the one on screen while the next frame's scripts run -- was covered.</summary>
    public static bool LastCovered { get; private set; }
    /// <summary>The frame number of <see cref="LastCovered"/>'s frame, and a short description of its cover draw (for the log).</summary>
    public static int LastFrame { get; private set; } = -1;
    public static string LastNote => !_lastCover ? "" : $"'{_lastTex?.Name}' rgb {_lastRgbA.X:F2},{_lastRgbA.Y:F2},{_lastRgbA.Z:F2} alpha {_lastRgbA.W:F3}, {_lastTop * 100:F0} % drawn on top";
    // (kit #12 walka: _lastCover / _lastTop are internal -- read by the opt-in intro arm in Icalls/BundleIcalls.cs, SHEPHERD_K12_INTRO=1 -- and no member
    // is added: the AOT cold lists key engine methods by token, so a new method or property shifts every later one; vita/notes/kit12-walka.md)
    static bool _covered; internal static bool _lastCover; static NTexture _tex, _lastTex; static System.Numerics.Vector4 _rgba, _lastRgbA; internal static double _lastTop;
    static int _mainThread = -1; static double _onTop; static int _w, _h;
    /// <summary>The share of the screen the draws after the cover may take (bounding boxes, summed): the loading icon and its text.</summary>
    public const double KitMaxOnTop = 0.15;
    /// <summary>Kit #11 walk (the intro's load peak; vita/notes/kit11-walk.md), OPT-IN (SHEPHERD_K11_COVER=1): the game's MMTransition black hold
    /// before the QuoteScreen and before / after the intro's scene load draws its loading icon and text over 16-18 % of the screen (VLX,
    /// SHEPHERD_BLACK_DEBUG=1 on the walk), so kit #4's 15 % counts those UnloadUnusedAssets calls as visible. With the switch a frame whose
    /// cover has up to 20 % on top counts as covered too -- for the growth gate only (<see cref="LastCoveredWide"/>): measured on the walk,
    /// the scene-load override there (a full collection right after the intro's load, which frees nothing) raised the arena peak by 5 MiB
    /// (evacuation copies at the live peak); the gated pre-load collection alone is measured in the note. Off = kit #10 exactly.</summary>
    public static readonly bool WideCover = Environment.GetEnvironmentVariable("SHEPHERD_K11_COVER") == "1" && Environment.GetEnvironmentVariable("SHEPHERD_K11_WALK") != "0";
    const double WideMaxOnTop = 0.20;
    /// <summary>Kit #11 walk (SHEPHERD_K11_COVER=1): the last completed frame was covered with more than kit #4's 15 % but at most 20 % on top.</summary>
    public static bool LastCoveredWide { get; private set; }
    static readonly bool Debug = Environment.GetEnvironmentVariable("SHEPHERD_BLACK_DEBUG") == "1";

    /// <summary>Kit #6 gpulight: a cover was seen earlier in this frame (main thread), so every later screen draw adds its bounding box
    /// (<see cref="Draw"/> reads the vertices): a draw whose vertices are made on the GPU is drawn on the CPU instead.</summary>
    internal static bool CoveredSoFar => _covered;
    /// <summary>SceneRenderer.Frame's start (main thread).</summary>
    internal static void BeginFrame() { _covered = false; _tex = null; _onTop = 0; _mainThread = Environment.CurrentManagedThreadId; }

    /// <summary>One draw of the frame (SceneRenderer.SubmitIdx: every draw to the screen), before or after the backend got it. Cheap for
    /// the common case: nothing covered yet and not a 4-vertex opaque candidate -> return at once; once a cover is seen, every later draw
    /// (any vertex count: TMP text, Spine and other meshes) adds its bounding box to what is drawn on top.</summary>
    internal static void Draw(Vertex[] v, int nv, int ni, NTexture tex, uint blend, int screenW, int screenH)
    {
        if (!_covered && (nv != 4 || v[0].color.a < 0.99f)) return;   // (the common case, cheaply: not a cover candidate, nothing covered yet)
        if (Environment.CurrentManagedThreadId != _mainThread) return;
        if (_covered && nv > 0)
        {   // a draw on top of the cover: its bounding box, clipped to the screen
            float bx0 = v[0].position.x, bx1 = bx0, by0 = v[0].position.y, by1 = by0;
            for (int i = 1; i < nv; i++) { var p = v[i].position; if (p.x < bx0) bx0 = p.x; if (p.x > bx1) bx1 = p.x; if (p.y < by0) by0 = p.y; if (p.y > by1) by1 = p.y; }
            bx0 = Math.Max(bx0, 0); by0 = Math.Max(by0, 0); bx1 = Math.Min(bx1, screenW); by1 = Math.Min(by1, screenH);
            if (bx1 > bx0 && by1 > by0) _onTop += (double)(bx1 - bx0) * (by1 - by0);
        }
        if (nv != 4 || ni < 6) return;
        if (blend != BLENDMODE_BLEND && blend != BLENDMODE_NONE && blend != BLENDMODE_BLEND_PREMULTIPLIED) return;
        float x0 = v[0].position.x, x1 = x0, y0 = v[0].position.y, y1 = y0, amin = v[0].color.a;
        for (int i = 1; i < 4; i++)
        {
            var p = v[i].position; if (p.x < x0) x0 = p.x; if (p.x > x1) x1 = p.x; if (p.y < y0) y0 = p.y; if (p.y > y1) y1 = p.y;
            if (v[i].color.a < amin) amin = v[i].color.a;
        }
        bool full = x0 <= 0.5f && y0 <= 0.5f && x1 >= screenW - 0.5f && y1 >= screenH - 0.5f;
        if (Debug && full) Log.Info($"[blackframe] f{Runtime.Time.FrameCount} full-screen quad: tex '{tex?.Name}' {tex?.Width}x{tex?.Height} solid={IsSolid(tex)} blend {blend} alpha min {amin:F3} rgb {v[0].color.r:F2},{v[0].color.g:F2},{v[0].color.b:F2}");
        if (!full || amin < 0.99f || !IsSolid(tex)) return;
        _covered = true; _onTop = 0; _w = screenW; _h = screenH;   // (a later cover hides what was drawn before it)
        _tex = tex; _rgba = new System.Numerics.Vector4(v[0].color.r, v[0].color.g, v[0].color.b, amin);
    }

    /// <summary>An opaque texture: none (a colour-only draw) or one of the engine's solid colour textures (Texture2D.whiteTexture & co.:
    /// what untextured UI draws with) with alpha 255.</summary>
    static bool IsSolid(NTexture t) => t == null || t.Solid && t.SolidAlpha == 255;

    /// <summary>SceneRenderer.Frame's end (main thread): this frame is now the one on screen.</summary>
    internal static void EndFrame()
    {
        double top = _covered && _w > 0 && _h > 0 ? _onTop / ((double)_w * _h) : 0;
        LastCovered = _covered && top <= KitMaxOnTop; LastFrame = Runtime.Time.FrameCount;
        LastCoveredWide = WideCover && _covered && !LastCovered && top <= WideMaxOnTop;
        _lastCover = _covered; _lastTex = _tex; _lastRgbA = _rgba; _lastTop = top;   // (the note is formatted only when asked for: no allocation per frame)
        Runtime.BlackFrameGc.Track();   // M1's growth gate: the heap after each major
        if (Debug && _covered) Log.Info($"[blackframe] f{LastFrame} {(LastCovered ? "covered" : "NOT covered")} by {LastNote}");
    }
}
