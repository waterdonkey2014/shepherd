namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #6, track "engine" (vita/notes/kit6-engine.md): engine gaps and fidelity bugs that stopped the game from running
/// further, plus menu fidelity. Every item has its own switch back to kit #5's behaviour, read once at start from the environment (on the
/// Vita: the app's env file); <c>SHEPHERD_K6_ENGINE=0</c> turns every item of the track off at once. (ProfileOnly: reads no property-bag
/// keys, so the diet's literal scan skips it and the menu's retained keys are kit #5's.)</summary>
[ProfileOnly]
public static class K6Engine
{
    static readonly bool Track = Environment.GetEnvironmentVariable("SHEPHERD_K6_ENGINE") != "0";
    static bool On(string name) => Track && Environment.GetEnvironmentVariable(name) != "0";

    /// <summary>(b) Object.DontDestroyOnLoad on a GameObject that has a parent (or on a component of one) does nothing but warn, as the
    /// game's own UnityPlayer does ("DontDestroyOnLoad only works for root GameObjects or components on root GameObjects."; the check on
    /// the transform's parent and the early return read from its x86_64 code, vita/notes/kit6-engine.md). Kit #5 moved the object's whole
    /// root to the DontDestroyOnLoad scene: the intro's ClearRenderTextureManager (a child of 'Game Prefab') took the entire game world
    /// with it, so Pause -> Main Menu never unloaded it. <c>SHEPHERD_K6_DDOL=0</c> = kit #5.</summary>
    public static readonly bool DdolRootOnly = On("SHEPHERD_K6_DDOL");

    /// <summary>(b) A scene file loaded again gets new objects, as every Unity scene load does: the previous load's records of that file
    /// are retired from the loader's (file, path id) table first. Kit #5 handed the destroyed first instance's records back, so the Main
    /// Menu re-loaded with 0 GameObjects. <c>SHEPHERD_K6_SCENE_RELOAD=0</c> = kit #5.</summary>
    public static readonly bool SceneReload = On("SHEPHERD_K6_SCENE_RELOAD");

    /// <summary>(b) A destroyed hierarchy gets Unity's two passes: OnDestroy pre-order over the whole hierarchy (parent first, nothing
    /// deleted yet), then the deletion (Runtime/Lifecycle.cs WillDestroyTree / KillTree). <c>SHEPHERD_K6_DESTROY_ORDER=0</c> = kit #5.</summary>
    public static readonly bool DestroyOrder = On("SHEPHERD_K6_DESTROY_ORDER");

    /// <summary>(b) Cameras keep the command buffers added to them per event (Icalls/CameraIcalls.cs): Camera.GetCommandBuffers answers them
    /// (bound here; kit #5: a missing icall returning null, so StencilBufferRender.OnDestroy threw in every intro unload) and
    /// commandBufferCount counts them. The buffers are still not executed. <c>SHEPHERD_K6_CMDBUF=0</c> = kit #5.</summary>
    public static readonly bool CmdBuf = On("SHEPHERD_K6_CMDBUF");

    /// <summary>(a) Renderer.material on a renderer whose first material slot is empty returns (and assigns) an instance of the default
    /// material, as Unity's GetAndAssignInstantiatedMaterial does (Icalls/RendererIcalls.cs DefaultInstance); kit #5 returned null.
    /// <c>SHEPHERD_K6_DEFAULTMAT=0</c> = kit #5.</summary>
    public static readonly bool DefaultMaterial = On("SHEPHERD_K6_DEFAULTMAT");
    /// <summary>The built-in pipeline's default material: shader "Standard"; Unity names an instantiated material "&lt;name&gt; (Instance)".</summary>
    public static readonly string DefaultShaderName = "Standard", DefaultMaterialInstanceName = "Default-Material (Instance)";   // (not const: the literals stay in this ProfileOnly class, out of the diet's scan)

    /// <summary>(a) The VideoPlayer stand-in keeps Unity's timing (Icalls/VideoMiscIcalls.cs, "kit #6 (a)"): events from the video update
    /// after LateUpdate, never inside Play; the clip's own length when the owner-local length table lists it. <c>SHEPHERD_K6_VIDEO=0</c> =
    /// kit #5 (events inside Play and before LateUpdate, the profile's stand-in length for every clip).</summary>
    public static readonly bool Video = On("SHEPHERD_K6_VIDEO");
    /// <summary>(a) SHEPHERD_VIDEO_LENGTHS=&lt;file&gt;: the game's videos' lengths (owner-local, made by vita/tools/video_lengths.py from the
    /// owner's install; never in git): one line per clip "name &lt;TAB&gt; frames &lt;TAB&gt; rate" (rate a number or num/den), '#' comments.
    /// Unset or unreadable: no clip is listed (every clip keeps the profile's stand-in length).</summary>
    public sealed class ClipLength { public long Frames; public double Rate; }
    static readonly Dictionary<string, ClipLength> Lengths = LoadLengths(LengthsPath());
    /// <summary>Kit #6 integration, owner decision 2026-10-19 ("Devolver splash: match the original"): under the Vita profile, with
    /// SHEPHERD_VIDEO_LENGTHS unset, the owner folder's data/video-lengths.tsv (ux0:data/shepherd/data/, staged by vita/shepherd/stage-owner.sh
    /// from the owner's install with every clip listed) -- so the boot's Devolver logo plays its 6.27 s as the PC game's does (a black picture
    /// with its music; boot work goes on behind it), and the intro's clip its 13 s. <c>SHEPHERD_VIDEO_LENGTHS=0</c> = no table (kit #5's
    /// 0-s clips; the engine track's default); <c>=&lt;file&gt;</c> = that table. No file there = no table.</summary>
    static string LengthsPath()
    {
        var e = Environment.GetEnvironmentVariable("SHEPHERD_VIDEO_LENGTHS");
        if (e == "0") return null;
        if (!string.IsNullOrEmpty(e)) return e;
        if (!Profile.Vita || !Video) return null;
        try { var p = Path.Combine(Program.Root, "data", "video-lengths.tsv"); return File.Exists(p) ? p : null; }
        catch (Exception) { return null; }
    }
    static Dictionary<string, ClipLength> LoadLengths(string path)
    {
        var d = new Dictionary<string, ClipLength>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(path)) return d;
        try
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture; var listed = new System.Text.StringBuilder();
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Split('#')[0].Split('\t'); if (t.Length < 3) continue;
                var r = t[2].Trim().Split('/');
                if (!long.TryParse(t[1].Trim(), out var fr) || fr <= 0 || !double.TryParse(r[0], System.Globalization.NumberStyles.Float, ic, out var num)) continue;
                double den = r.Length > 1 && double.TryParse(r[1], System.Globalization.NumberStyles.Float, ic, out var dd) && dd > 0 ? dd : 1;
                if (!(num / den > 0)) continue;
                d[t[0].Trim()] = new ClipLength { Frames = fr, Rate = num / den };
                listed.Append(' ').Append(t[0].Trim()).Append(' ').Append(fr).Append('/').Append((num / den).ToString(ic));
            }
            Log.Info("[k6] video lengths from " + path + ":" + listed);
        }
        catch (Exception e) { Log.Warn("[k6] video lengths: " + path + " unreadable (" + e.Message + "): no clip listed"); }
        return d;
    }
    /// <summary>The clip's length from the table, or null when it is not listed.</summary>
    public static ClipLength VideoLength(string clip) => clip != null && Lengths.TryGetValue(clip, out var l) ? l : null;

    /// <summary>(d) Where Input.mousePosition rests while no mouse (and no finger, kit #5 touch) has moved it: the screen centre,
    /// (Screen.width / 2, Screen.height / 2) as the game itself computes the centre (integer halves) -- the game's neutral point:
    /// CameraSubtleMovementOnInput's input is (mouse - centre) / half-size and CanvasParallax's is mouse / size * 2 - 1, both exactly 0 there,
    /// so the main menu camera stays at its authored position and the parallax layers at their authored offset. Kits #1-#5 rested it at
    /// (0,0), the bottom-left corner: both lean to their bottom-left extreme, a picture a PC shows only with the cursor parked in that corner.
    /// <c>SHEPHERD_K6_MOUSE_REST=0</c> = kit #5 (0,0); <c>=x,y</c> rests it there (a test, Mac / tools builds: e.g. a reference capture's cursor).</summary>
    public static readonly string MouseRestSetting = Track ? Environment.GetEnvironmentVariable("SHEPHERD_K6_MOUSE_REST") : "0";
    public static bool MouseRest => MouseRestSetting != "0";

    /// <summary>(d) A Screen Space - Camera root canvas ('Main Menu', 'StartingLogo') is placed in front of its camera at planeDistance and
    /// scaled to fill the view, as Unity drives it, from its first layout on: the scene's saved camera reference is resolved (kit #5 resolved
    /// it only while a finger was on the glass and otherwise laid the canvas out like an overlay canvas, so its UI world positions were not
    /// Unity's). The placement is kept while it still matches the picture within 0.5 px (kit #5 touch's rule).
    /// Review fix: OFF by default, opt-in with <c>SHEPHERD_K6_CANVAS_CAMERA=1</c> (unset / anything else = kit #5). While a menu camera
    /// moves (the 3.5 s after "press any button", the ~200-frame glide after every touch) the canvas is re-placed each frame, which voids the
    /// UI geometry cache (it keys on the bit-exact canvas-to-screen x local-to-world matrices): VLX 00098 +8.6 % frame time after the press,
    /// +18.8 % in the first 100 frames of a glide, for a picture that differs from kit #5's only in edge pixels. The cheaper re-placement (the
    /// UI pass projecting Camera-mode roots through the root-relative matrix, so moving the root never touches the UI geometry) is a UI-render
    /// item (vita/notes/kit6-engine.md, Review fixes).</summary>
    public static readonly bool CanvasCamera = Track && Environment.GetEnvironmentVariable("SHEPHERD_K6_CANVAS_CAMERA") == "1";

    /// <summary>(c) Pack textures are uploaded from native memory, not from managed arrays (Assets/PackAssets.cs UploadDxtNative /
    /// UploadAlpha8Native): a DXT image is read into malloc'd memory and uploaded as it is; an SDF atlas's distance channel is decoded strip
    /// by strip (the pack read in 256-KiB strips) into malloc'd memory and uploaded -- the same GL textures, the same calls and bytes.
    /// Kit #5 allocated a managed array per upload: at the Main Menu's first draw ~11 MiB of DXT blocks (one 4-MiB 2048 x 2048, six 1-MiB)
    /// plus 4 + 4 MiB for the 'IconTexture' SDF atlas, all large objects in the GC heap, in the frame where the menu load's arena peak is set.
    /// <c>SHEPHERD_K6_TEX_NATIVE=0</c> = kit #5; <c>=check</c> also decodes the SDF atlas the kit #5 way and compares (Mac / tools builds).</summary>
    public static readonly bool TexNative = On("SHEPHERD_K6_TEX_NATIVE");
    public static readonly bool TexNativeCheck = TexNative && Environment.GetEnvironmentVariable("SHEPHERD_K6_TEX_NATIVE") == "check";

    /// <summary>Boot, after the icall registry: binds the track's late hooks (the VitaTouch pattern: only when the item is on).</summary>
    public static void Install()
    {
        if (MouseRest)
        {   // before any game code reads the pointer (UINavigatorNew takes its first reading as its "previous" one: no jump is seen)
            Impl.InputState.Mouse = new UnityEngine.Vector3(Impl.AppIcalls.ScreenW / 2, Impl.AppIcalls.ScreenH / 2, 0f);
#if !SHEPHERD_EBOOT
            var p = (MouseRestSetting ?? "").Split(',');   // =x,y: a test position (Mac / tools builds)
            if (p.Length == 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)) Impl.InputState.Mouse = new UnityEngine.Vector3(x, y, 0f);
#endif
        }
        if (CmdBuf) Bind("UnityEngine.Camera::GetCommandBuffers", Impl.CameraIcalls.GetCbs);
        if (Video)
        {
            const string vp = "UnityEngine.Video.VideoPlayer::";
            Bind(vp + "set_url", Impl.VideoIcalls.SetUrl); Bind(vp + "get_url", Impl.VideoIcalls.GetUrl);
            Bind(vp + "set_clip", Impl.VideoIcalls.SetClip); Bind(vp + "get_clip", Impl.VideoIcalls.GetClip);
            Bind(vp + "set_source", Impl.VideoIcalls.SetSource); Bind(vp + "get_source", Impl.VideoIcalls.GetSource);
            Bind(vp + "set_playbackSpeed", Impl.VideoIcalls.SetSpeed); Bind(vp + "get_playbackSpeed", Impl.VideoIcalls.GetSpeed);
            Bind(vp + "set_isLooping", Impl.VideoIcalls.SetLooping); Bind(vp + "get_isLooping", Impl.VideoIcalls.GetLooping);
        }
    }
    /// <summary>An icall the registry left unimplemented, bound to <paramref name="h"/>.</summary>
    static void Bind(string name, Shepherd.Hooks.IcallImpl h)
    {
        for (int i = 0; i < IcallRegistry.Names.Length; i++)
            if (IcallRegistry.Names[i] == name && Shepherd.Hooks.Icalls.Table[i] == null) Shepherd.Hooks.Icalls.Table[i] = h;
    }

    /// <summary>The warning Unity prints for a non-root DontDestroyOnLoad (its own words), with the object's name.</summary>
    public static void DdolRefused(string goName, string parentName) =>
        Log.Warn("DontDestroyOnLoad only works for root GameObjects or components on root GameObjects. ('" + goName + "', child of '" + parentName + "': left in its scene)");

    /// <summary>(b) The log line of a scene file instantiated again (Assets/Loader.cs RetireSceneRecords).</summary>
    public static void SceneRetired(string name, int records, int alive) =>
        Log.Info("[k6] scene '" + name + "' loaded again: " + records.ToString() + " records of its previous load retired (" + alive.ToString() + " of them not destroyed); this load creates new objects");

    /// <summary>The track's end-of-run line (engine.log): which items are on (each item's switch name without SHEPHERD_K6_).</summary>
    public static string Summary()
    {
        var b = new System.Text.StringBuilder(Track ? "[k6] engine track on:" : "[k6] engine track OFF (SHEPHERD_K6_ENGINE=0)");
        if (DdolRootOnly) b.Append(" DDOL"); if (SceneReload) b.Append(" SCENE_RELOAD"); if (DestroyOrder) b.Append(" DESTROY_ORDER"); if (CmdBuf) b.Append(" CMDBUF");
        if (DefaultMaterial) b.Append(" DEFAULTMAT"); if (Video) b.Append(" VIDEO(").Append(Lengths.Count).Append(" clip lengths)");
        if (MouseRest) b.Append(" MOUSE_REST(").Append(Impl.InputState.Mouse.x).Append(',').Append(Impl.InputState.Mouse.y).Append(')');
        if (CanvasCamera) b.Append(" CANVAS_CAMERA"); if (TexNative) b.Append(TexNativeCheck ? " TEX_NATIVE(check)" : " TEX_NATIVE");
        return b.ToString();
    }
}
