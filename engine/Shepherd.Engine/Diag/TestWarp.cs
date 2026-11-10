#if !SHEPHERD_EBOOT
using System.Reflection;

namespace Shepherd.Engine.Diag;

/// <summary>Kit #13 basec (Mac test hooks; compiled into Mac / tools builds -- a SHEPHERD_ENGINE_TOOLS=1 eboot too --, left out of the release
/// eboots): test hooks that take a Mac run to the base, the first dungeon and a base with N followers through the game's own code (reflection
/// on the game's types; no game code is copied). Every hook is off unless its variable is set (unset: no call reaches the game, no frame
/// changes). REFUSED (every hook, one [testwarp] REFUSED line) under the Vita host (SHEPHERD_VITA_HOST=1: a tools eboot on the card, whose
/// persistent folder holds the owner's real slots) and when Application.persistentDataPath looks like a real save location (the follower
/// sweep's real_save patterns): Quick Start resets save slot 0 and the game's next autosave writes it.
/// <list type="bullet">
/// <item>SHEPHERD_TEST_QUICKSTART=&lt;frame&gt;: the Quick Start branch of LoadMenu.BeginNewGame in its order (slot 0 reset, QuickStartActive,
/// OnboardingFinished, the tutorial variables, Medium difficulty), then MMTransition.Play to SHEPHERD_TEST_QS_SCENE (default "Base Biome 1").
/// The UI / audio lines of that branch are left out (the sandbox runs silent). The save is the run's sandbox copy.</item>
/// <item>SHEPHERD_TEST_DOOR=&lt;frame&gt;:&lt;scene&gt;: the base dungeon door's trigger lines (Interaction_BaseDungeonDoor.GetFloor of the door whose
/// SceneName is &lt;scene&gt;, else the location &lt;scene&gt;_1 by name) and MMTransition.Play to that scene.</item>
/// <item>SHEPHERD_TEST_FOLLOWERS=&lt;frame&gt;:&lt;n&gt;[,base|copy]: n followers on a 10-wide grid around the player. base (default) =
/// FollowerManager.CreateNewFollower(Base, pos, false), the base's own spawn; copy = FollowerManager.SpawnCopyFollower (the cutscene
/// follower; works in a scene without a location manager).</item>
/// <item>SHEPHERD_TEST_DUMP=&lt;frame&gt;:&lt;path filter or *&gt;: every camera, then every active renderer whose hierarchy path contains the filter
/// (placement, sort keys, material, and where the main camera puts its pivot / bounds on screen).</item>
/// <item>SHEPHERD_TEST_SETACTIVE0=&lt;frame&gt;:&lt;hierarchy path&gt;: SetActive(false) on that object (an A/B aid).</item>
/// <item>SHEPHERD_TEST_WATCH=&lt;from&gt;:&lt;every&gt;[:&lt;to&gt;]: one line per &lt;every&gt; frames with the dungeon generator's state (BiomeGenerator.Instance:
/// the current room, its GenerateRoom's generated / GeneratedDecorations / GeneratedPathing), MMTransition (IsPlaying, CanResume, the loading
/// text), Time.timeScale and the object pool's pending spawns: where a load waits.</item>
/// <item>Kit #14 room (the "lighter room first" measurement; Mac / tools only like the rest):
/// SHEPHERD_TEST_UNCOVER=&lt;frame&gt;: a transition still waiting for ResumePlay (Quick Start into a scene that never calls it, e.g.
/// BufferScene) gets MMTransition.ResumePlay(); if its cover has not faded 60 frames later it is ended the way MMTransition.FadeOutRoutine ends
/// one (StopCurrentTransition, IsPlaying false, timeScale 1, SimulationManager.UnPause, the transition object off) -- MMTransition.Show
/// ignores a new Play while IsPlaying, so the door / scene hooks after such a Quick Start need it.
/// SHEPHERD_TEST_ROOM=&lt;frame&gt;[,&lt;frame&gt;...]: at each frame the dungeon moves one room on, the way a room door does
/// (BiomeGenerator.ChangeRoom(direction)), to the first connected neighbour of the current room not visited yet (N, E, S, W order; else the
/// first connected one): the floor's next rooms.
/// SHEPHERD_TEST_SCENE=&lt;frame&gt;:&lt;scene&gt;: a plain MMTransition.Play(ChangeRoomWaitToResume, BlackFade) to that scene (what the game's
/// own scene changes call: e.g. BufferScene -> Dungeon Ratau Home / Hub-Shore).
/// SHEPHERD_TEST_SAVE=&lt;frame&gt;: the game's own SaveAndLoad.Save() into the current slot of the run's sandbox persistent folder (a
/// fresh-built test save, e.g. for a Continue route; it saves only when the game's AllowSaving / DisableSaving allow it).</item>
/// </list></summary>
[ProfileOnly]   // reads no property-bag key; log text only
public static class TestWarp
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly int QsFrame = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_TEST_QUICKSTART"), out var f) ? f : -1;
    static readonly string QsScene = Environment.GetEnvironmentVariable("SHEPHERD_TEST_QS_SCENE") is { Length: > 0 } qsn ? qsn : "Base Biome 1";
    static readonly (int frame, string arg) Door = FrameArg(Environment.GetEnvironmentVariable("SHEPHERD_TEST_DOOR"));
    static readonly (int frame, string arg) DumpAt = FrameArg(Environment.GetEnvironmentVariable("SHEPHERD_TEST_DUMP"));
    static readonly (int frame, string arg) Fol = FrameArg(Environment.GetEnvironmentVariable("SHEPHERD_TEST_FOLLOWERS"));
    static readonly (int frame, string arg) Watch = FrameArg(Environment.GetEnvironmentVariable("SHEPHERD_TEST_WATCH"));
    static readonly (int frame, string arg) Off = FrameArg(Environment.GetEnvironmentVariable("SHEPHERD_TEST_SETACTIVE0"));
    static readonly int WatchEvery = Watch.frame >= 0 && int.TryParse(Watch.arg.Split(':')[0], out var we) && we > 0 ? we : 0;
    static readonly int WatchTo = Watch.frame >= 0 && Watch.arg.Split(':') is { Length: > 1 } wt && int.TryParse(wt[1], out var wto) ? wto : int.MaxValue;
    static readonly int UncoverAt = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_TEST_UNCOVER"), out var unc) ? unc : -1;
    static readonly HashSet<int> RoomAt = new((Environment.GetEnvironmentVariable("SHEPHERD_TEST_ROOM") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var r) ? r : -1).Where(r => r >= 0));
    static readonly (int frame, string arg) SceneGo = FrameArg(Environment.GetEnvironmentVariable("SHEPHERD_TEST_SCENE"));
    static readonly int SaveAt = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_TEST_SAVE"), out var sv) ? sv : -1;
    static readonly bool Asked = QsFrame >= 0 || Door.frame >= 0 || DumpAt.frame >= 0 || Fol.frame >= 0 || WatchEvery > 0 || Off.frame >= 0
        || UncoverAt >= 0 || RoomAt.Count > 0 || SceneGo.frame >= 0 || SaveAt >= 0;
    static int _coverDeadline = -1;
    /// <summary>Why the hooks are refused (null: they may run): the Vita host, or a save root that looks real.</summary>
    static readonly string Refusal = !Asked ? null
        : VitaHost.On ? "under the Vita host (SHEPHERD_VITA_HOST=1): the card's persistent folder holds the owner's real saves"
        : RealSave(Impl.AppIcalls.Persistent) ? $"the save root '{Impl.AppIcalls.Persistent}' looks like a real save location (sandbox saves only)"
        : null;
    /// <summary>Any hook set and allowed (Boot's frame loop calls Before only then).</summary>
    public static readonly bool On = Asked && Refusal == null;
    static TestWarp() { if (Refusal != null) Log.Info($"[testwarp] REFUSED, no hook runs: {Refusal}"); }

    /// <summary>The follower sweep's real_save patterns (vita/tools/k12_follower_sweep.sh), on the resolved, lower-cased path.</summary>
    internal static bool RealSave(string path)
    {
        string p;
        try { p = Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant(); } catch { return true; }   // (unreadable: refuse)
        if (p.StartsWith("ux0:", StringComparison.Ordinal) || p.Contains("/ux0:", StringComparison.Ordinal) || p.Contains("/volumes/ux0", StringComparison.Ordinal)) return true;
        foreach (var k in new[] { "application support", "steamapps", "steamlibrary", "library/containers", "/saved games", "appdata", "massive monster" })
            if (p.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    static (int, string) FrameArg(string v)
    {
        if (string.IsNullOrEmpty(v)) return (-1, "");
        int c = v.IndexOf(':');
        if (c < 1 || !int.TryParse(v[..c], out var fr)) return (-1, "");
        return (fr, v[(c + 1)..]);
    }

    public static void Before(int frame)
    {
        if (frame == QsFrame) Run("quickstart", QuickStart);
        if (frame == Door.frame) Run("door", DoorGo);
        if (frame == DumpAt.frame) Run("dump", Dump);
        if (frame == Fol.frame) Run("followers", Followers);
        if (frame == Off.frame) Run("setactive0", SetActive0);
        if (WatchEvery > 0 && frame >= Watch.frame && frame <= WatchTo && (frame - Watch.frame) % WatchEvery == 0) Run("watch", WatchLine);
        if (frame == UncoverAt) Run("uncover", Uncover);
        if (_coverDeadline >= 0) Run("cover", () => CoverPoll(frame));
        if (RoomAt.Contains(frame)) Run("room", NextRoom);
        if (frame == SaveAt) Run("save", () =>
        {
            var sal = T("SaveAndLoad"); var dm = GetStatic(T("DataManager"), "Instance");
            string why = $"slot {GetStatic(sal, "SAVE_SLOT")}, AllowSaving {TryGet(dm, "AllowSaving")}, DisableSaving {TryGet(dm, "DisableSaving")}";
            sal.GetMethod("Save", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)!.Invoke(null, null);
            Log.Info($"[testwarp] save at frame {Runtime.Time.FrameCount}: SaveAndLoad.Save() ({why}) into the sandbox persistent folder");
        });
        if (frame == SceneGo.frame) Run("scene", () =>
        {
            Transition(SceneGo.arg, 1f);
            Log.Info($"[testwarp] scene at frame {Runtime.Time.FrameCount}: MMTransition.Play -> '{SceneGo.arg}'");
        });
    }

    static object GetStatic(Type t, string name) =>
        t.GetField(name, All) is { } fi ? fi.GetValue(null) : t.GetProperty(name, All)?.GetValue(null);

    /// <summary>SHEPHERD_TEST_UNCOVER: ResumePlay on a transition that waits for it; CoverPoll ends the cover 60 frames later if it is still up.</summary>
    static void Uncover()
    {
        var mm = T("MMTools.MMTransition");
        bool playing = (bool)GetStatic(mm, "IsPlaying");
        if (playing) mm.GetMethod("ResumePlay", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)!.Invoke(null, null);
        Log.Info($"[testwarp] uncover at frame {Runtime.Time.FrameCount}: transition was {(playing ? "playing -> ResumePlay()" : "not playing")}");
        if (playing) _coverDeadline = Runtime.Time.FrameCount + 60;
    }

    static void CoverPoll(int frame)
    {
        var mm = T("MMTools.MMTransition");
        if ((bool)GetStatic(mm, "IsPlaying") && frame < _coverDeadline) return;
        _coverDeadline = -1;
        if (!(bool)GetStatic(mm, "IsPlaying")) { Log.Info($"[testwarp] cover: faded out by itself by f{frame}"); return; }
        mm.GetMethod("StopCurrentTransition", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        var fi = mm.GetField("IsPlaying", All); fi?.SetValue(null, false);
        UnityEngine.Time.timeScale = 1f;
        T("SimulationManager").GetMethod("UnPause", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null);
        foreach (var n in new[] { "mmTransition", "Instance" })
            if (GetStatic(mm, n) is UnityEngine.Component c && c != null) c.gameObject.SetActive(false);
        Log.Info($"[testwarp] cover: never faded out by f{frame}; ended like FadeOutRoutine ends it (StopCurrentTransition, timeScale 1, UnPause, transition object off)");
    }

    /// <summary>SHEPHERD_TEST_ROOM: BiomeGenerator.ChangeRoom(direction) to the first connected, not yet visited neighbour (N, E, S, W).</summary>
    static void NextRoom()
    {
        var bgT = T("MMBiomeGeneration.BiomeGenerator");
        var bg = bgT.GetField("Instance", All)?.GetValue(null);
        if (bg is not UnityEngine.Object bu || bu == null) { Log.Info($"[testwarp] room at frame {Runtime.Time.FrameCount}: no BiomeGenerator in this scene"); return; }
        var cur = Get(bg, "CurrentRoom");
        if (cur == null) { Log.Info($"[testwarp] room at frame {Runtime.Time.FrameCount}: no current room yet"); return; }
        (string name, string field, int dx, int dy)[] dirs = { ("N", "N_Room", 0, 1), ("E", "E_Room", 1, 0), ("S", "S_Room", 0, -1), ("W", "W_Room", -1, 0) };
        (string name, int dx, int dy, object room)? pick = null, first = null;
        foreach (var d in dirs)
        {
            var c = Get(cur, d.field);
            if (c == null || !(bool)Get(c, "Connected") || Get(c, "Room") is not { } r) continue;
            first ??= (d.name, d.dx, d.dy, r);
            if (!(bool)Get(r, "Visited")) { pick = (d.name, d.dx, d.dy, r); break; }
        }
        pick ??= first;
        if (pick is not { } p) { Log.Info($"[testwarp] room at frame {Runtime.Time.FrameCount}: the current room ({Get(cur, "x")},{Get(cur, "y")}) has no connected neighbour"); return; }
        bool visitedBefore = (bool)Get(p.room, "Visited");
        bgT.GetMethod("ChangeRoom", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(UnityEngine.Vector2Int) }, null)!
            .Invoke(null, new object[] { new UnityEngine.Vector2Int(p.dx, p.dy) });
        Log.Info($"[testwarp] room at frame {Runtime.Time.FrameCount}: ({Get(cur, "x")},{Get(cur, "y")}) -> {p.name} ({Get(p.room, "x")},{Get(p.room, "y")}) " +
                 $"'{(p.room as UnityEngine.Object == null ? TryGet(Get(p.room, "GameObject"), "name") : "")}' visited before {visitedBefore}; rooms visited {TryGet(bg, "RoomsVisited")}");
    }

    /// <summary>SHEPHERD_TEST_SETACTIVE0=&lt;frame&gt;:&lt;hierarchy path&gt;: SetActive(false) on the active GameObjects at that path (an A/B aid: does
    /// this object make the difference on screen).</summary>
    static void SetActive0()
    {
        int n = 0;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<UnityEngine.Transform>())
            if (PathOf(t) == Off.arg) { t.gameObject.SetActive(false); n++; }
        Log.Info($"[testwarp] setactive0 at frame {Runtime.Time.FrameCount}: '{Off.arg}' x{n}");
    }

    static object TryGet(object o, string name)
    {
        if (o == null) return null;
        try { return Get(o, name); } catch { return "?"; }
    }

    static void WatchLine()
    {
        var sb = new System.Text.StringBuilder($"[testwarp] watch f{Runtime.Time.FrameCount}: timeScale {UnityEngine.Time.timeScale}");
        var bg = T("MMBiomeGeneration.BiomeGenerator");
        var inst = bg.GetField("Instance", All)?.GetValue(null);
        if (inst is UnityEngine.Object uo && uo != null)
        {
            var room = TryGet(inst, "CurrentRoom");
            sb.Append($" | biome: room {(room == null ? "none" : $"({TryGet(room, "x")},{TryGet(room, "y")})")}");
            var gr = room == null ? null : TryGet(room, "generateRoom");
            if (gr is UnityEngine.Object gu && gu != null)
                sb.Append($" generated {TryGet(gr, "generated")} decorations {TryGet(gr, "GeneratedDecorations")} pathing {TryGet(gr, "GeneratedPathing")} active {((UnityEngine.Component)gr).gameObject.activeInHierarchy}");
            else sb.Append(" generateRoom none");
        }
        else sb.Append(" | biome: no instance");
        var mm = T("MMTools.MMTransition");
        sb.Append($" | transition: playing {mm.GetField("IsPlaying", All)?.GetValue(null)} canResume {mm.GetField("CanResume", All)?.GetValue(null)}");
        var mmi = mm.GetField("mmTransition", All)?.GetValue(null);
        if (mmi is UnityEngine.Object mu && mu != null && TryGet(mmi, "LoadingText") is UnityEngine.Component lt && lt != null)
            sb.Append($" text '{TryGet(lt, "text")}' shown {lt.gameObject.activeInHierarchy}");
        var sim = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SimulationManager")).FirstOrDefault(t => t != null);
        if (sim != null) sb.Append($" | simulation paused {sim.GetProperty("IsPaused", All)?.GetValue(null) ?? sim.GetField("_isPaused", All)?.GetValue(null) ?? "?"}");
        Log.Info(sb.ToString());
    }

    static void Run(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { Log.Info($"[testwarp] {what} FAILED: {(e as TargetInvocationException)?.InnerException ?? e}"); }
    }

    static Type T(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null)
        ?? throw new Exception($"type {name} not found");

    static void SetStatic(Type t, string name, object v)
    {
        var fi = t.GetField(name, All);
        if (fi != null) fi.SetValue(null, v); else t.GetProperty(name, All)!.SetValue(null, v);
    }

    static void Set(object o, string name, object v)
    {
        var fi = o.GetType().GetField(name, All);
        if (fi != null) fi.SetValue(o, v); else o.GetType().GetProperty(name, All)!.SetValue(o, v);
    }

    static object Get(object o, string name)
    {
        var fi = o.GetType().GetField(name, All);
        return fi != null ? fi.GetValue(o) : o.GetType().GetProperty(name, All)!.GetValue(o);
    }

    static string PathOf(UnityEngine.Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;

    static void Transition(string scene, float duration)
    {
        var mm = T("MMTools.MMTransition");
        var tt = Enum.Parse(T("MMTools.MMTransition+TransitionType"), "ChangeRoomWaitToResume");
        var fx = Enum.Parse(T("MMTools.MMTransition+Effect"), "BlackFade");
        mm.GetMethod("Play", All)!.Invoke(null, new[] { tt, fx, scene, duration, "", null, null });
    }

    static void QuickStart()
    {
        var gm = T("GameManager");
        SetStatic(gm, "CurrentDungeonLayer", 1); SetStatic(gm, "CurrentDungeonFloor", 1); SetStatic(gm, "DungeonUseAllLayers", false);
        var sal = T("SaveAndLoad");
        SetStatic(sal, "SAVE_SLOT", 0);
        sal.GetMethod("ResetSave", All)!.Invoke(null, new object[] { 0, true });
        var dmT = T("DataManager");
        var dm = dmT.GetProperty("Instance", All)!.GetValue(null)!;
        var dif = T("DifficultyManager");
        var all = (Array)dif.GetMethod("AllAvailableDifficulties", All)!.Invoke(null, null)!;
        int medium = Array.IndexOf(all, Enum.Parse(T("DifficultyManager+Difficulty"), "Medium"));
        Set(dm, "PermadeDeathActive", false);
        var meta = Get(dm, "MetaData"); Set(meta, "Difficulty", medium); Set(dm, "MetaData", meta);   // a struct: copy, change, store back
        Set(dm, "QuickStartActive", true);
        Set(dm, "OnboardingFinished", true);
        dmT.GetMethod("SetTutorialVariables", All)!.Invoke(dm, null);
        dif.GetMethod("ForceDifficulty", new[] { typeof(int) })!.Invoke(null, new object[] { medium });
        Transition(QsScene, 5f);
        Log.Info($"[testwarp] quickstart at frame {Runtime.Time.FrameCount}: slot 0 reset, QuickStartActive, difficulty index {medium}, -> '{QsScene}'");
    }

    static void DoorGo()
    {
        var doorT = T("Interaction_BaseDungeonDoor");
        var doors = UnityEngine.Object.FindObjectsOfType(doorT);
        object pick = null;
        foreach (var d in doors)
        {
            Log.Info($"[testwarp] door '{Get(d, "SceneName")}' location {Get(d, "Location")} unlocked {Get(d, "Unlocked")}");
            if ((string)Get(d, "SceneName") == Door.arg) pick = d;
        }
        object loc = pick != null ? Get(pick, "Location") : Enum.Parse(T("FollowerLocation"), Door.arg + "_1");
        Log.Info($"[testwarp] door at frame {Runtime.Time.FrameCount}: {(pick != null ? "the base's own door" : "no door object found, location by name")} {loc} -> '{Door.arg}'");
        if (pick != null) Set(pick, "Used", true);
        doorT.GetMethod("GetFloor", All)!.Invoke(null, new[] { loc });
        Transition(Door.arg, 1f);
    }

    static void Followers()
    {
        var parts = Fol.arg.Split(','); int n = int.Parse(parts[0]); string mode = parts.Length > 1 ? parts[1].Trim() : "base";
        var fm = T("FollowerManager"); var fi = T("FollowerInfo"); var locT = T("FollowerLocation");
        var baseLoc = Enum.Parse(locT, "Base");
        var o = UnityEngine.Vector3.zero;
        var pf = T("PlayerFarming");
        var inst = pf.GetProperty("Instance", All)?.GetValue(null) ?? pf.GetField("Instance", All)?.GetValue(null);
        if (inst is UnityEngine.Component pc && pc != null) o = pc.transform.position;
        var newChar = fi.GetMethod("NewCharacter", All, null, new[] { locT, typeof(string) }, null)!;
        var spawnCopy = fm.GetMethod("SpawnCopyFollower", All, null, new[] { fi, typeof(UnityEngine.Vector3), typeof(UnityEngine.Transform), locT }, null)!;
        var create = fm.GetMethod("CreateNewFollower", All, null, new[] { locT, typeof(UnityEngine.Vector3), typeof(bool) }, null)!;
        int ok = 0, fail = 0; string firstErr = null;
        for (int i = 0; i < n; i++)
        {
            var pos = o + new UnityEngine.Vector3((i % 10 - 4.5f) * 1.2f, (i / 10 - 4.5f) * 1.2f, 0f);
            try
            {
                if (mode == "copy") spawnCopy.Invoke(null, new object[] { newChar.Invoke(null, new object[] { baseLoc, "" }), pos, null, baseLoc });
                else create.Invoke(null, new object[] { baseLoc, pos, false });
                ok++;
            }
            catch (Exception e) { fail++; firstErr ??= ((e as TargetInvocationException)?.InnerException ?? e).ToString(); }
        }
        int gos = UnityEngine.Object.FindObjectsOfType<UnityEngine.GameObject>().Length;
        Log.Info($"[testwarp] followers at frame {Runtime.Time.FrameCount}: mode {mode}, {ok} spawned, {fail} failed, origin {o}, active GameObjects now {gos}" +
                 (firstErr != null ? " -- first error: " + firstErr.Split('\n')[0] : ""));
    }

    static void Dump()
    {
        foreach (var c in UnityEngine.Object.FindObjectsOfType<UnityEngine.Camera>())
            Log.Info($"[testwarp] CAM '{PathOf(c.transform)}' enabled={c.enabled} depth={c.depth} ortho={c.orthographic} fov={c.fieldOfView} size={c.orthographicSize} " +
                     $"pos={c.transform.position:F3} rot={c.transform.eulerAngles:F3} scale={c.transform.lossyScale:F3} near={c.nearClipPlane} far={c.farClipPlane} " +
                     $"aspect={c.aspect:F4} rect={c.rect} px={c.pixelWidth}x{c.pixelHeight} main={(c == UnityEngine.Camera.main)}");
        foreach (var c in UnityEngine.Object.FindObjectsOfType<UnityEngine.Camera>())
            if (c.targetTexture is { } rt) Log.Info($"[testwarp] CAM '{PathOf(c.transform)}' targets '{rt.name}' {rt.width}x{rt.height} id {rt.GetInstanceID()}");
        foreach (var ri in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.RawImage>())
        {
            var tx = ri.texture;
            Log.Info($"[testwarp] RAWIMAGE '{PathOf(ri.transform)}' active={ri.gameObject.activeInHierarchy} enabled={ri.enabled} texture={(tx == null ? "null" : $"'{tx.name}' {tx.GetType().Name} {tx.width}x{tx.height} id {tx.GetInstanceID()}")} " +
                     $"color={ri.color} material='{ri.material?.name}' shader='{ri.material?.shader?.name}' comps=[{string.Join(",", ri.GetComponents<UnityEngine.Component>().Select(k => k == null ? "null" : k.GetType().Name))}]");
        }
        var cam = UnityEngine.Camera.main;
        string filter = DumpAt.arg == "*" ? "" : DumpAt.arg;
        foreach (var gr in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Graphic>())
        {
            if (!gr.gameObject.activeInHierarchy || !gr.enabled) continue;
            var path = PathOf(gr.transform);
            if (filter.Length > 0 && path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var cv = gr.canvas; var root = cv == null ? null : cv.rootCanvas;
            var corners = new UnityEngine.Vector3[4]; gr.rectTransform.GetWorldCorners(corners);
            Log.Info($"[testwarp] UI '{path}' {gr.GetType().Name} color={gr.color} canvas='{(root == null ? "" : PathOf(root.transform))}' mode={root?.renderMode} order={root?.sortingOrder} " +
                     $"worldCam='{root?.worldCamera?.name}' planeDist={root?.planeDistance} rect={gr.rectTransform.rect} corners={corners[0]:F2}..{corners[2]:F2} " +
                     (gr is UnityEngine.UI.Image im ? $"sprite='{im.sprite?.name}' type={im.type}" : ""));
        }
        foreach (var r in UnityEngine.Object.FindObjectsOfType<UnityEngine.Renderer>())
        {
            if (!r.gameObject.activeInHierarchy || !r.enabled) continue;
            var t = r.transform; var path = PathOf(t);
            if (filter.Length > 0 && path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            string kind = r is UnityEngine.SpriteRenderer sr ? $"sprite={sr.sprite?.name} drawMode={sr.drawMode} size={sr.size:F3} color={sr.color}" : r.GetType().Name;
            var b = r.bounds;
            string scr = cam == null ? "" : $" pivot@{cam.WorldToScreenPoint(t.position):F1} bmin@{cam.WorldToScreenPoint(b.min):F1} bmax@{cam.WorldToScreenPoint(b.max):F1}";
            Log.Info($"[testwarp] R '{path}' {kind} pos={t.position:F3} lpos={t.localPosition:F3} rot={t.eulerAngles:F3} lrot={t.localEulerAngles:F3} lossy={t.lossyScale:F3} " +
                     $"sort={r.sortingLayerName}/{r.sortingOrder} bounds={b.center:F3}/{b.size:F3}{scr} shader='{r.sharedMaterial?.shader?.name}' mat='{r.sharedMaterial?.name}'");
        }
    }
}
#endif
