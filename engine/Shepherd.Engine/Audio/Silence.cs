using FMOD;
using FMODUnity;
namespace Shepherd.Audio;

/// <summary>Engine-level silence for the Mac default engine (the owner's rule: nothing may make sound on this Mac).
/// The default engine runs the game's real FMOD (fmodstudio.bundle). In a headless run the engine mutes it as soon as
/// FMODUnity's RuntimeManager is up and before its first Update (the only place that calls Studio::System::update(), so no
/// queued event has been mixed yet): FMOD's core master channel group setMute(true) -- everything Studio plays ends there --
/// and the Studio master bus ("bus:/") setMute(true) once the master banks are loaded. Both are re-checked every frame (the game
/// may unmute its bus from its own audio settings) and their state is read back from FMOD at the end of the run.
///   SHEPHERD_AUDIO=on   never mute (sound allowed)          SHEPHERD_AUDIO=off   mute in window mode too
///   unset               mute headless runs; window mode (play.sh) keeps its sound
/// The Vita profile has no FMOD library (Shepherd.Audio.Fmod stubs): nothing to do there.
/// [ProfileOnly]: no property-bag keys are read here, and its FMOD-typed locals must stay out of the diet's IL scan.</summary>
[Shepherd.Engine.ProfileOnly]
public static class Silence
{
    static readonly string Mode = (Environment.GetEnvironmentVariable("SHEPHERD_AUDIO") ?? "").Trim().ToLowerInvariant();
    /// <summary>True until the run is known not to need muting, or while it still has to (re-)assert the mute.</summary>
    public static bool Active { get; private set; }
    static int _mutedAt = -1, _busAt = -1, _remutes, _checks;
    static string _why;

    /// <summary>Boot, before the frame loop.</summary>
    public static void Configure(bool headless)
    {
        if (Shepherd.Engine.Profile.Vita) { Active = false; return; }
        Active = Mode switch { "on" or "1" or "yes" => false, "off" or "0" or "no" or "mute" => true, _ => headless };
        _why = Mode.Length > 0 ? $"SHEPHERD_AUDIO={Mode}" : headless ? "headless run" : "window run";
        Shepherd.Engine.Log.Info(Active ? $"[silence] {_why}: FMOD is muted at the engine level once RuntimeManager is up (master channel group + master bus); SHEPHERD_AUDIO=on allows sound"
                                        : $"[silence] {_why}: FMOD sound is allowed");
    }

    /// <summary>Lifecycle.Frame, right before the Update phase (RuntimeManager.Update -> Studio::System::update()).</summary>
    public static void BeforeUpdates()
    {
        if (!Active) return;
        try
        {
            if (!RuntimeManager.IsInitialized) return;   // reads the instance only: never creates the RuntimeManager
            _checks++;
            if (RuntimeManager.CoreSystem.getMasterChannelGroup(out ChannelGroup mcg) == RESULT.OK && mcg.getMute(out bool m) == RESULT.OK && !m)
            {
                var r = mcg.setMute(true);
                if (_mutedAt < 0) { _mutedAt = Shepherd.Engine.Runtime.Time.FrameCount; Shepherd.Engine.Log.Info($"[silence] FMOD master channel group muted ({r}) at frame {_mutedAt}, before RuntimeManager's first update"); }
                else _remutes++;
            }
            if (RuntimeManager.HaveMasterBanksLoaded && RuntimeManager.StudioSystem.getBus("bus:/", out FMOD.Studio.Bus bus) == RESULT.OK && bus.getMute(out bool bm) == RESULT.OK && !bm)
            {
                var r = bus.setMute(true);
                if (_busAt < 0) { _busAt = Shepherd.Engine.Runtime.Time.FrameCount; Shepherd.Engine.Log.Info($"[silence] FMOD master bus (bus:/) muted ({r}) at frame {_busAt}"); }
                else _remutes++;
            }
        }
        catch (Exception e) { Shepherd.Engine.Log.Warn($"[silence] {e.GetBaseException().Message}"); }
    }

    /// <summary>End of the run: FMOD's own answer for both mutes (no listening involved).</summary>
    public static string Summary()
    {
        if (Shepherd.Engine.Profile.Vita || _why == null) return null;   // the Vita profile / a mode without a game boot
        if (!Active) return $"[silence] {_why}: not muted by the engine";
        try
        {
            if (!RuntimeManager.IsInitialized) return $"[silence] {_why}: FMOD RuntimeManager never initialized (nothing could play)";
            string cg = RuntimeManager.CoreSystem.getMasterChannelGroup(out ChannelGroup mcg) == RESULT.OK && mcg.getMute(out bool m) == RESULT.OK ? m.ToString() : "?";
            string bs = RuntimeManager.StudioSystem.getBus("bus:/", out FMOD.Studio.Bus bus) == RESULT.OK && bus.getMute(out bool bm) == RESULT.OK ? bm.ToString() : "? (bus:/ not found)";
            return $"[silence] {_why}: FMOD state at exit: master channel group mute={cg}, master bus mute={bs}; muted at frame {_mutedAt} (bus {_busAt}), re-muted {_remutes} times over {_checks} checks";
        }
        catch (Exception e) { return $"[silence] state query failed: {e.GetBaseException().Message}"; }
    }
}
