namespace Shepherd.Engine.Runtime;

/// <summary>kit #4 New Game scope (vita/notes/kit4-intro-plan.md section 5): the intro does not fit in the Vita's memory yet (its
/// Follower skeleton data alone runs the 124 MiB GC heap out while it is read), so a kit #4 app ends the session cleanly -- green, the
/// way SHEPHERD_STOP_AT ends a run -- at the moment the game asks for the intro scene, instead of a load that cannot finish. Without
/// the stop the game would go on into the intro's load: with the kit #4 `newgame` pack that is MISSING-from-pack errors, failed
/// Addressables preloads and an endless loading screen; with the `intro` pack, the memory guard's red END.
/// The trigger is the game's own announcement, the earliest moment the engine learns the destination: its transition logs
/// "Loading scene &lt;name&gt;" when the fade starts (f2916 of the New Game route) and "SceneToLoad&lt;name&gt;" after the buffer scene,
/// both before anything of the scene is requested; a LoadSceneAsync of the scene itself is the fallback (a game that logs neither).
/// SHEPHERD_STOP_SCENE (env file): unset = "Game Biome Intro" in the Vita profile, nothing on the Mac default; empty or 0 = no stop
/// (the intro's load runs, for measurement); any other text = the scene name to stop at. Only ends the session: no game state changes.
/// [ProfileOnly]: reads no property-bag keys (its literals stay out of the V3 diet's scan).</summary>
[ProfileOnly]
public static class SceneStop
{
    static readonly string Scene = Environment.GetEnvironmentVariable("SHEPHERD_STOP_SCENE") is { } v
        ? (v.Trim() is "0" or "off" ? "" : v.Trim()) : (Profile.Vita ? "Game Biome Intro" : "");
    public static bool Hit { get; private set; }

    /// <summary>A Debug.Log line of the game (Impl/AppIcalls Internal_Log).</summary>
    public static void OnGameLog(string msg)
    {
        if (Scene.Length == 0 || Hit || msg == null || !msg.Contains(Scene, StringComparison.Ordinal)) return;
        if (msg.Contains("Loading scene", StringComparison.Ordinal) || msg.Contains("SceneToLoad", StringComparison.Ordinal)) Stop($"the game announced the scene ('{msg}')");
    }

    /// <summary>SceneManager.LoadSceneAsync / LoadScene of <paramref name="name"/> (Impl/BundleIcalls), before anything of it is read.</summary>
    public static void OnLoadScene(string name)
    {
        if (Scene.Length == 0 || Hit || name == null || !name.Contains(Scene, StringComparison.Ordinal)) return;
        Stop($"LoadSceneAsync('{name}')");
    }

    static void Stop(string why)
    {
        Hit = true;
        Log.Info($"[scenestop] END intro at frame {Time.FrameCount}: New Game reached '{Scene}' ({why}). The intro needs the Spine-data and engine " +
                 "memory passes (kit #5+): the session ends here, cleanly, after this frame. SHEPHERD_STOP_SCENE= (empty) in the env file goes on into the load.");
        Boot.QuitRequested = true;
    }
}
