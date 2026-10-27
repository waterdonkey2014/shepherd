using System.Runtime.InteropServices;
using FMOD;
using FMOD.Studio;

namespace Shepherd.FmodBake;

/// <summary>
/// One FMOD Studio system on the game's own fmodstudio 2.02.07, non-realtime and SILENT.
/// SILENCE RULE: output type NOSOUND_NRT only. setOutput must succeed before initialize and getOutput must still report
/// NOSOUND_NRT after it, otherwise the process stops before a single mix. No real output device is ever opened.
/// Determinism: fixed core random seed, synchronous Studio update, loading/streaming/mixing driven from update; every
/// Studio::System::update mixes exactly one DSP block (Block frames at Rate), captured by a pass-through DSP placed at the
/// head (post-fader) of the core master channel group.
/// </summary>
sealed class Session : IDisposable
{
    public const int Rate = 48000;
    public const int Block = 1024;           // frames per mix (one Studio::System::update in NRT)
    public const uint Seed = 0x5EEDCA75;
    public const int StreamLookahead = 2 * Rate;

    public FMOD.Studio.System Studio;
    public FMOD.System Core;
    public readonly List<(string file, Bank bank)> Banks = new();
    public long Updates;
    public bool PluginsLoaded;
    readonly Action<string> _log;

    // capture DSP state (called from inside update on this thread: MIX_FROM_UPDATE + NRT)
    static Session _cur;
    static DSP_DESCRIPTION _desc;
    static DSP_READCALLBACK _readCb;
    static DSP_SHOULDIPROCESS_CALLBACK _shouldCb;
    DSP _capDsp;
    public bool Capturing;
    public float[] Cap = new float[Rate * 2 * 60];
    public int CapFrames;                     // stereo frames captured
    public int CbCalls;
    public float LastBlockPeak;
    public int LastBlockChannels;

    Session(Action<string> log) { _log = log; }

    public static Session Open(string appDir, string key, Action<string> log)
    {
        var s = new Session(log);
        s.Init(appDir, key);
        return s;
    }

    static void Check(RESULT r, string what)
    {
        if (r != RESULT.OK) throw new InvalidOperationException($"FMOD {what} -> {r}");
    }

    void Init(string appDir, string key)
    {
        Check(FMOD.Studio.System.create(out Studio), "Studio::System::create");
        Check(Studio.getCoreSystem(out Core), "getCoreSystem");
        var r = Core.setOutput(OUTPUTTYPE.NOSOUND_NRT);
        if (r != RESULT.OK) { Studio.release(); Fatal($"silence rule: setOutput(NOSOUND_NRT) -> {r}; refusing to continue"); }
        Check(Core.setSoftwareFormat(Rate, SPEAKERMODE.STEREO, 0), "setSoftwareFormat");
        Check(Core.setDSPBufferSize((uint)Block, 2), "setDSPBufferSize");
        var adv = new FMOD.ADVANCEDSETTINGS { randomSeed = Seed };
        Check(Core.setAdvancedSettings(ref adv), "System::setAdvancedSettings");
        // the bank key goes straight from memory into FMOD; it is not kept by this class
        // streamingscheduledelay: how far ahead (mixed samples) Studio opens a streamed instrument. NRT mixes ~180x faster
        // than real time while stream opens run on FMOD's non-blocking thread in real time, so the default 8192 samples
        // (~1 ms of wall time here) lets late opens shift sounds. 2 s of lookahead gives opens ~10 ms of wall time; the
        // matching hold at an event's start is removed by the renderer (timeline-based lead trim).
        var sadv = new FMOD.Studio.ADVANCEDSETTINGS { streamingscheduledelay = StreamLookahead };
        Check(Studio.setAdvancedSettings(sadv, key), "Studio::setAdvancedSettings");
        var sflags = FMOD.Studio.INITFLAGS.SYNCHRONOUS_UPDATE | FMOD.Studio.INITFLAGS.LOAD_FROM_UPDATE;   // never LIVEUPDATE (network)
        var cflags = FMOD.INITFLAGS.STREAM_FROM_UPDATE | FMOD.INITFLAGS.MIX_FROM_UPDATE;
        Check(Studio.initialize(1024, sflags, cflags, IntPtr.Zero), "Studio::System::initialize");
        Check(Core.getOutput(out var ot), "getOutput");
        if (ot != OUTPUTTYPE.NOSOUND_NRT) { Studio.release(); Fatal($"silence rule: output is {ot} after initialize; refusing to continue"); }
        Check(Core.getSoftwareFormat(out int rate, out var sm, out _), "getSoftwareFormat");
        Check(Core.getDSPBufferSize(out uint bl, out _), "getDSPBufferSize");
        if (rate != Rate || sm != SPEAKERMODE.STEREO || bl != Block) Fatal($"mixer format {rate} Hz {sm} block {bl}, expected {Rate} STEREO {Block}");
        _log($"FMOD up: output={ot} {rate} Hz {sm} block={bl} seed=0x{Seed:X8}");

        // plugins the game loads (FMODUnity Platform.LoadPlugins): resonanceaudio. A DSP plugin, it makes no sound by itself.
        // FMODUnity passes the .bundle directory on macOS (Platform.GetPluginPath)
        string res = Path.Combine(appDir, "Contents/PlugIns/resonanceaudio.bundle");
        if (Directory.Exists(res))
        {
            var pr = Core.loadPlugin(res, out uint ph);
            Core.getNumNestedPlugins(ph, out int nested);
            _log($"plugin resonanceaudio -> {pr} ({nested} nested DSPs)");
            PluginsLoaded = pr == RESULT.OK;
        }

        // capture DSP at the head (post-fader) of the master channel group
        _readCb = ReadCallback;
        _shouldCb = ShouldCallback;
        _desc = new DSP_DESCRIPTION
        {
            pluginsdkversion = 110,
            name = new byte[32],
            version = 1,
            numinputbuffers = 1,
            numoutputbuffers = 1,
            read = _readCb,
            shouldiprocess = _shouldCb,
        };
        System.Text.Encoding.ASCII.GetBytes("shepherd capture").CopyTo(_desc.name, 0);
        Check(Core.createDSP(ref _desc, out _capDsp), "createDSP(capture)");
        Check(Core.getMasterChannelGroup(out var master), "getMasterChannelGroup");
        Check(master.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, _capDsp), "master.addDSP(HEAD)");
        _cur = this;
    }

    static void Fatal(string msg)
    {
        Console.Error.WriteLine("FATAL: " + msg);
        Environment.Exit(3);
    }

    static RESULT ShouldCallback(ref DSP_STATE st, bool inputsidle, uint length, CHANNELMASK mask, int ch, SPEAKERMODE sm) => RESULT.OK;

    static unsafe RESULT ReadCallback(ref DSP_STATE st, IntPtr inbuf, IntPtr outbuf, uint length, int inch, ref int outch)
    {
        var s = _cur;
        float* src = (float*)inbuf, dst = (float*)outbuf;
        int n = (int)length * inch;
        Buffer.MemoryCopy(src, dst, (long)n * 4, (long)n * 4);
        outch = inch;
        if (s == null) return RESULT.OK;
        s.CbCalls++;
        s.LastBlockChannels = inch;
        float pk = 0;
        for (int i = 0; i < n; i++) { float a = Math.Abs(src[i]); if (a > pk) pk = a; }
        s.LastBlockPeak = pk;
        if (s.Capturing)
        {
            int need = (s.CapFrames + (int)length) * 2;
            if (need > s.Cap.Length) Array.Resize(ref s.Cap, Math.Max(need, s.Cap.Length * 2));
            int o = s.CapFrames * 2;
            if (inch == 2) for (int i = 0; i < n; i++) s.Cap[o + i] = src[i];
            else for (int f = 0; f < length; f++) { float v = src[f * inch]; s.Cap[o + f * 2] = v; s.Cap[o + f * 2 + 1] = inch > 1 ? src[f * inch + 1] : v; }
            s.CapFrames += (int)length;
        }
        return RESULT.OK;
    }

    /// <summary>One Studio update = one mixed block in NRT. Returns the peak of that block (0 if the read callback did not run).</summary>
    public float Step()
    {
        int before = CbCalls;
        LastBlockPeak = 0;
        Check(Studio.update(), "Studio::System::update");
        Updates++;
        if (CbCalls == before && Capturing)
        {
            // no read callback for this mix (idle graph): keep the timeline exact with a silent block
            int need = (CapFrames + Block) * 2;
            if (need > Cap.Length) Array.Resize(ref Cap, Math.Max(need, Cap.Length * 2));
            Array.Clear(Cap, CapFrames * 2, Block * 2);
            CapFrames += Block;
            MissedCallbacks++;
        }
        return LastBlockPeak;
    }
    public long MissedCallbacks;

    public void BeginCapture() { CapFrames = 0; Capturing = true; }
    public float[] EndCapture()
    {
        Capturing = false;
        var a = new float[CapFrames * 2];
        Array.Copy(Cap, a, a.Length);
        return a;
    }

    /// <summary>Run mixes until the master output has been silent (peak below thr) for quietBlocks in a row, or maxBlocks.</summary>
    public int Drain(int quietBlocks = 8, int maxBlocks = Rate * 20 / Block, float thr = 1e-6f)
    {
        int q = 0, n = 0;
        while (n < maxBlocks)
        {
            float pk = Step(); n++;
            if (pk < thr) { if (++q >= quietBlocks) break; } else q = 0;
        }
        return n;
    }

    // ------------------------------------------------------------------ banks

    public void LoadBanks(string streamingAssets)
    {
        var files = Directory.GetFiles(streamingAssets, "*.bank").Where(f => !Path.GetFileName(f).StartsWith("._")).ToList();
        // strings + Master first, then the rest in name order (deterministic)
        files.Sort((a, b) => Rank(a).CompareTo(Rank(b)) is var c && c != 0 ? c : string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        foreach (var f in files)
        {
            var r = Studio.loadBankFile(f, LOAD_BANK_FLAGS.NONBLOCKING, out var b);
            if (r != RESULT.OK) throw new InvalidOperationException($"loadBankFile {Path.GetFileName(f)} -> {r}");
            Banks.Add((Path.GetFileName(f), b));
        }
        WaitBanks(LOADING_STATE.LOADED, sample: false);
        foreach (var (_, b) in Banks) b.loadSampleData();
        Check(Studio.flushCommands(), "flushCommands");
        Step();
        WaitBanks(LOADING_STATE.LOADED, sample: true);
    }

    static int Rank(string f)
    {
        string n = Path.GetFileName(f);
        if (n.Equals("Master.strings.bank", StringComparison.OrdinalIgnoreCase)) return 0;
        if (n.StartsWith("Master.", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    void WaitBanks(LOADING_STATE want, bool sample)
    {
        for (int iter = 0; ; iter++)
        {
            bool all = true;
            foreach (var (name, b) in Banks)
            {
                var r = sample ? b.getSampleLoadingState(out var st) : b.getLoadingState(out st);
                if (st == LOADING_STATE.ERROR) throw new InvalidOperationException($"bank {name}: {(sample ? "sample " : "")}loading ERROR ({r})");
                if (st != want && !(sample && st == LOADING_STATE.UNLOADED)) all = false;
            }
            if (all) return;
            Step();
            if (iter > 200000) throw new TimeoutException("bank loading did not finish");
        }
    }

    public void Dispose()
    {
        _cur = null;
        if (Studio.isValid()) { Studio.unloadAll(); Studio.release(); }
    }
}
