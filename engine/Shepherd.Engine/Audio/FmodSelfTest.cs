using System.Runtime.InteropServices;
using FMOD;
using FMOD.Studio;
using Shepherd.Engine;
namespace Shepherd.Audio;

/// <summary>`boot vitatest` part 1: drives FMODUnity's own C# wrapper (FMOD.Studio.*, rewritten P/Invokes -> Fmod.cs) through
/// the patterns CotL uses: RuntimeManager init + bank loads, path/GUID lookups, AudioManager.PlayOneShotWithInstanceCleanup
/// (GCHandle in user data, freed in the STOPPED callback), RuntimeManager.PlayOneShot (start + release), held loops, buses,
/// VCAs, parameters. No game code runs; no audio library is loaded.</summary>
[ProfileOnly]
static class FmodSelfTest
{
    public static int Run(Action<bool, string> check)
    {
        if (ShepAudio.BakeDir != null) return RunBake(check);   // Stage C: the same patterns against shepaudio and the real bake
        int before = 0;
        var r = FMOD.Studio.System.create(out var sys); check(r == RESULT.OK && sys.isValid(), $"Studio::System::create -> {r}, handle 0x{sys.handle.ToInt64():X}");
        r = sys.getCoreSystem(out var core); check(r == RESULT.OK && core.hasHandle(), "getCoreSystem -> non-zero handle");
        r = sys.initialize(512, FMOD.Studio.INITFLAGS.DEFERRED_CALLBACKS, FMOD.INITFLAGS.NORMAL, IntPtr.Zero); check(r == RESULT.OK, "Studio::System::initialize");
        FMOD.SYSTEM_CALLBACK err = (s, t, a, b, u) => RESULT.OK;
        r = core.setCallback(err, FMOD.SYSTEM_CALLBACK_TYPE.ERROR); check(r == RESULT.OK, "System::setCallback(ERROR) accepted");
        r = core.getVersion(out uint ver); check(r == RESULT.OK && ver == VERSION.number, $"System::getVersion = 0x{ver:X}");

        var bankFile = Path.Combine(Program.GameData, "StreamingAssets", "Master.bank");
        r = sys.loadBankFile(bankFile, LOAD_BANK_FLAGS.NORMAL, out var bank); check(r == RESULT.OK && bank.isValid(), "loadBankFile(Master.bank) -> OK (nothing read)");
        bank.getLoadingState(out var ls); bank.getSampleLoadingState(out var ss); check(ls == LOADING_STATE.LOADED && ss == LOADING_STATE.LOADED, $"bank loading state {ls}, sample state {ss}");
        r = sys.loadBankFile(bankFile, LOAD_BANK_FLAGS.NORMAL, out _); check(r == RESULT.ERR_EVENT_ALREADY_LOADED, $"second load of the same bank -> {r}");
        bank.getPath(out var bankPath); check(bankPath == "bank:/Master", $"bank path '{bankPath}'");
        sys.getBankList(out var banks); check(banks.Any(b => b.handle == bank.handle), $"getBankList has it ({banks.Length} banks)");

        const string path = "event:/ui/change_selection";
        r = sys.getEvent(path, out var desc); check(r == RESULT.OK && desc.isValid(), $"getEvent('{path}')");
        r = sys.lookupID(path, out var guid); check(r == RESULT.OK && !guid.IsNull, $"lookupID -> {guid}");
        sys.getEventByID(guid, out var desc2); check(desc2.handle == desc.handle, "getEventByID(lookupID(path)) is the same description");
        desc.getPath(out var p2); desc.getID(out var g2); check(p2 == path && g2 == guid, $"description path '{p2}', id round-trips");
        r = Util.parseID(guid.ToString(), out var g3); check(r == RESULT.OK && g3 == guid, "Util.parseID(\"{guid}\") (FMOD_Studio_ParseID)");
        r = sys.getEvent("nonsense", out var bad); check(r == RESULT.ERR_EVENT_NOTFOUND && !bad.isValid(), $"getEvent('nonsense') -> {r}");

        // AudioManager.PlayOneShotWithInstanceCleanup + OneShotInstanceCleanup
        desc.createInstance(out var inst); var gch = GCHandle.Alloc(new object()); inst.setUserData(GCHandle.ToIntPtr(gch));
        int stopped = 0, others = 0; bool freed = false;
        EVENT_CALLBACK cb = (type, ev, par) =>
        {
            if (type != EVENT_CALLBACK_TYPE.STOPPED) { others++; return RESULT.OK; }
            stopped++; var ei = new EventInstance(ev);
            if (ei.getUserData(out var ud) == RESULT.OK && ud != IntPtr.Zero) { GCHandle.FromIntPtr(ud).Free(); freed = true; ei.setUserData(IntPtr.Zero); }
            ei.setCallback(null); ei.release(); return RESULT.OK;
        };
        inst.setCallback(cb, EVENT_CALLBACK_TYPE.STOPPED); inst.start();
        inst.getPlaybackState(out var st0); check(st0 == PLAYBACK_STATE.STARTING, $"one-shot after start(): {st0}");
        desc.getInstanceCount(out before);
        sys.update();
        check(stopped == 1 && others == 0, $"update -> STOPPED callback fired once (stopped={stopped}, other masked types={others})");
        check(freed, "callback found the GCHandle in the instance's user data and freed it");
        check(!inst.isValid(), "released inside its STOPPED callback -> destroyed (isValid false)");
        desc.getInstanceCount(out var after); check(before == 1 && after == 0, $"instance count {before} -> {after}");

        // RuntimeManager.PlayOneShot: create, start, release
        desc.createInstance(out var os); os.start(); os.release();
        check(os.isValid(), "PlayOneShot instance valid until the next update"); sys.update(); check(!os.isValid(), "... destroyed by it");

        // held loop (AudioManager.CreateLoop): plays until stop()
        desc.createInstance(out var loop); loop.start(); sys.update();
        for (int i = 0; i < 5; i++) sys.update();
        loop.getPlaybackState(out var st1); check(st1 == PLAYBACK_STATE.PLAYING, $"held loop after 6 updates: {st1}");
        loop.setParameterByName("intensity", 0.75f); loop.getParameterByName("intensity", out var pv); check(pv == 0.75f, $"instance parameter read back {pv}");
        loop.setVolume(0.5f); loop.getVolume(out var vol); check(vol == 0.5f, $"instance volume read back {vol}");
        int loopStops = 0; loop.setCallback((t, e, q) => { if (t == EVENT_CALLBACK_TYPE.STOPPED) loopStops++; return RESULT.OK; }, EVENT_CALLBACK_TYPE.STOPPED);
        loop.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT); loop.getPlaybackState(out var st2); check(st2 == PLAYBACK_STATE.STOPPED, $"after stop(): {st2}");
        loop.release(); sys.update(); check(loopStops == 1 && !loop.isValid(), $"stop() -> STOPPED callback at the next update ({loopStops}), released -> destroyed");

        // buses, VCAs, global parameters
        r = sys.getBus("bus:/", out var bus); check(r == RESULT.OK && bus.isValid(), "getBus('bus:/')");
        bus.setPaused(true); bus.getPaused(out var paused); check(paused, "bus paused state read back");
        r = sys.getBus("not a bus", out _); check(r == RESULT.ERR_EVENT_NOTFOUND, $"getBus('not a bus') -> {r}");
        sys.getVCA("vca:/Music", out var vca); vca.setVolume(0.3f); vca.getVolume(out var vv); check(vca.isValid() && vv == 0.3f, $"VCA volume read back {vv}");
        sys.setParameterByName("Underwater", 1f); sys.getParameterByName("Underwater", out var gv); check(gv == 1f, $"global parameter read back {gv}");

        r = bank.unload(); check(r == RESULT.OK && !bank.isValid(), "bank unload -> handle invalid");
        r = sys.release(); check(r == RESULT.OK && !sys.isValid(), "Studio::System::release");
        GC.KeepAlive(err); GC.KeepAlive(cb);
        return 0;
    }

    /// <summary>Stage C (`SHEPHERD_AUDIO_BAKE=runtime/audiobake/menu boot vitatest`): FMODUnity's wrapper through the game's patterns with
    /// the stubs backed by shepaudio and the menu bake. Time advances one 60 fps frame per Studio update (the output -- file or null --
    /// mixes each frame's audio), so STOPPED comes when the take reaches the bake's stopFrame, not at the next update as with the silent
    /// stubs. Uses events of the menu bake: ui/change_selection, relics/glitch, player/Curses/vortex_loop, snapshot:/music_bus, a 3D vo.</summary>
    static int RunBake(Action<bool, string> check)
    {
        Shepherd.Engine.Runtime.Time.CaptureDeltaTime = 1f / 60f; Shepherd.Engine.Runtime.Time.VirtualRealtime = true;
        var r = FMOD.Studio.System.create(out var sys); check(r == RESULT.OK && ShepAudio.On, $"Studio::System::create with SHEPHERD_AUDIO_BAKE -> shepaudio on ({ShepAudio.Status})");
        if (!ShepAudio.On) return 1;
        sys.initialize(512, FMOD.Studio.INITFLAGS.DEFERRED_CALLBACKS, FMOD.INITFLAGS.NORMAL, IntPtr.Zero);
        int frames = 0;
        void Update(int n = 1) { for (int k = 0; k < n; k++) { Shepherd.Engine.Runtime.Time.BeginFrame(); sys.update(); frames++; } }

        // descriptions: the bake's GUIDs and data
        const string ui = "event:/ui/change_selection";
        r = sys.getEvent(ui, out var desc); check(r == RESULT.OK && desc.isValid(), $"getEvent('{ui}')");
        sys.lookupID(ui, out var guid); desc.getID(out var g2);
        int ev = ShepAudio.FindEvent(ui); var x = ev >= 0 ? ShepAudio.Events[ev] : null;
        check(x != null && guid.Data1 == x.G0 && guid.Data2 == x.G1 && guid.Data3 == x.G2 && guid.Data4 == x.G3 && g2 == guid, $"lookupID / getID = the bake's (FMOD's) GUID {guid}");
        sys.getEventByID(guid, out var desc2); check(desc2.handle == desc.handle, "getEventByID(FMOD GUID) = the same description (EventReference path)");
        desc.isOneshot(out bool os); desc.is3D(out bool d3); check(os && !d3, $"isOneshot {os}, is3D {d3} (from the bake)");
        sys.getEvent("event:/dlc/dungeon06/enemy/miniboss_executioner/game_opening_attack_vo", out var vo);
        vo.is3D(out bool vo3d); vo.getMinMaxDistance(out float mn, out float mx); check(vo3d && mn == 1f && mx == 40f, $"3D event: is3D {vo3d}, min/max distance {mn}/{mx} (bake: 1/40)");

        // AudioManager.PlayOneShotWithInstanceCleanup: GCHandle in user data, freed by the STOPPED callback, released there
        const string glitch = "event:/relics/glitch";   // one pcm16 take, FMOD STOPPED at take frame 18432 (384 ms)
        sys.getEvent(glitch, out var gd);
        gd.createInstance(out var inst); var gch = GCHandle.Alloc(new object()); inst.setUserData(GCHandle.ToIntPtr(gch));
        int stoppedAt = -1, others = 0; bool freed = false;
        EVENT_CALLBACK cb = (type, evh, par) =>
        {
            if (type != EVENT_CALLBACK_TYPE.STOPPED) { others++; return RESULT.OK; }
            stoppedAt = frames + 1; var ei = new EventInstance(evh);   // inside update number frames + 1
            if (ei.getUserData(out var ud) == RESULT.OK && ud != IntPtr.Zero) { GCHandle.FromIntPtr(ud).Free(); freed = true; ei.setUserData(IntPtr.Zero); }
            ei.setCallback(null); ei.release(); return RESULT.OK;
        };
        inst.setCallback(cb, EVENT_CALLBACK_TYPE.STOPPED); inst.start();
        inst.getPlaybackState(out var st0); check(st0 == PLAYBACK_STATE.STARTING, $"one-shot after start(): {st0}");
        int startFrame = frames + 1;
        Update(); inst.getPlaybackState(out var st1); check(st1 == PLAYBACK_STATE.PLAYING && stoppedAt < 0, $"next update: {st1}, no STOPPED yet");
        for (int k = 0; k < 120 && stoppedAt < 0; k++) Update();
        double expect = 18432 / 800.0;   // output frames per 60 fps frame
        check(stoppedAt > 0 && stoppedAt - startFrame >= expect - 1 && stoppedAt - startFrame <= expect + 2,
              $"STOPPED callback in update {stoppedAt}, {stoppedAt - startFrame} after the starting one (the bake's stopFrame = {expect:F1} frames at 60 fps)");
        check(freed && others == 0, "the callback found the GCHandle in the user data and freed it; no unmasked callback types");
        Update(); check(!inst.isValid(), "released inside its STOPPED callback -> destroyed at the next update");
        gd.getInstanceCount(out int cnt); check(cnt == 0, $"instance count {cnt}");

        // RuntimeManager.PlayOneShot: create, set3DAttributes, start, setTimelinePosition(startTime), release
        gd.createInstance(out var po); po.set3DAttributes(default); po.start(); po.setTimelinePosition(200); po.release();
        int t0 = frames; for (int k = 0; k < 120 && po.isValid(); k++) Update();
        double expect2 = (18432 - 200 * 48) / 800.0;
        check(!po.isValid() && frames - t0 >= expect2 && frames - t0 <= expect2 + 3, $"PlayOneShot + setTimelinePosition(200): destroyed after {frames - t0} updates (stopFrame - 200 ms = {expect2:F1} frames)");

        // held loop (AudioManager.CreateLoop) + stop(ALLOWFADEOUT) with the authored release; bus pause
        const string loopPath = "event:/player/Curses/vortex_loop";   // ADPCM loop, measured release: STOPPED 64 ms after stop()
        sys.getEvent(loopPath, out var ld); ld.createInstance(out var loop); loop.start(); Update(30);
        loop.getPlaybackState(out var ls1); loop.getTimelinePosition(out int tl1); check(ls1 == PLAYBACK_STATE.PLAYING && tl1 > 400, $"held loop after 30 updates: {ls1}, timeline {tl1} ms");
        sys.getBus("bus:/SFXBus/paused_by_pause_menu", out var pbus); pbus.setPaused(true); Update(10);
        loop.getTimelinePosition(out int tl2); Update(10); loop.getTimelinePosition(out int tl3);
        check(tl3 == tl2, $"bus paused (paused_by_pause_menu): the loop's timeline holds ({tl2} -> {tl3} ms)");
        pbus.setPaused(false); Update(10); loop.getTimelinePosition(out int tl4); check(tl4 > tl3, $"bus resumed: timeline moves again ({tl4} ms)");
        int loopStops = 0, stopFrame = -1; loop.setCallback((t, e, q) => { if (t == EVENT_CALLBACK_TYPE.STOPPED) { loopStops++; stopFrame = frames + 1; } return RESULT.OK; }, EVENT_CALLBACK_TYPE.STOPPED);
        int callAt = frames; loop.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT); loop.getPlaybackState(out var ls2);
        check(ls2 == PLAYBACK_STATE.STOPPING, $"stop(ALLOWFADEOUT) -> {ls2} (the authored release)");
        for (int k = 0; k < 60 && loopStops == 0; k++) Update();
        check(loopStops == 1 && stopFrame - callAt >= 4 && stopFrame - callAt <= 6, $"STOPPED in the {stopFrame - callAt}th update after stop() (releaseStopMs 64 ms = 3.8 frames after the call's audio clock)");
        loop.release(); Update(); check(!loop.isValid(), "released after STOPPED -> destroyed");

        // snapshots: the game's volume snapshot driven by its parameter; a second copy (a second AudioManager) does not stack
        sys.getEvent("snapshot:/music_bus", out var sd); sd.isSnapshot(out bool isSnap); check(isSnap, "snapshot:/music_bus is a snapshot");
        sd.createInstance(out var snap); snap.start(); snap.setParameterByName("music_bus_volume", 0.5f); Update(15);
        snap.getParameterByName("music_bus_volume", out float pv); snap.getPlaybackState(out var ss); check(pv == 0.5f && ss == PLAYBACK_STATE.PLAYING, $"snapshot playing, parameter read back {pv}");
        snap.stop(FMOD.Studio.STOP_MODE.IMMEDIATE); snap.release(); Update(2); check(!snap.isValid(), "snapshot stopped + released -> destroyed");

        // 3D one-shot with the listener; globals; bus stopAllEvents
        sys.setListenerAttributes(0, new ATTRIBUTES_3D { forward = new VECTOR { z = 1 }, up = new VECTOR { y = 1 } });
        vo.createInstance(out var v3); v3.set3DAttributes(new ATTRIBUTES_3D { position = new VECTOR { x = 6 }, forward = new VECTOR { z = 1 }, up = new VECTOR { y = 1 } });
        v3.start(); v3.release(); Update(2); v3.getPlaybackState(out var vs3); check(vs3 == PLAYBACK_STATE.PLAYING, $"3D one-shot 6 m to the right: {vs3}");
        r = sys.setParameterByName("inside", 1f); check(r == RESULT.OK, "global parameter set (bake global 'inside')");
        sys.getBus("bus:/", out var master); master.stopAllEvents(FMOD.Studio.STOP_MODE.IMMEDIATE); Update(3);
        check(!v3.isValid(), "bus:/ stopAllEvents(IMMEDIATE) stopped the 3D one-shot -> destroyed (it was released)");

        // nothing leaked: every shepaudio instance the test made is gone
        Update(5);
        int live = 0; foreach (var d in new[] { desc, gd, ld, sd, vo }) { d.getInstanceCount(out int c); live += c; }
        check(live == 0, $"no instance left alive ({live}) after {frames} updates");
        GC.KeepAlive(cb);
        return 0;
    }
}
