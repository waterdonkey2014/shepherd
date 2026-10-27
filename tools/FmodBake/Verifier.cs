using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shepherd.FmodBake;

/// <summary>
/// verify &lt;set&gt;: re-reads every take of a baked set and checks it against the manifest, objectively:
/// container layout exactly as specified (44 / 60-byte WAV headers, ADTS LC 48 kHz), frame counts, loop bounds, codec
/// rules, bus and parameter references; decodes every take (AAC with Apple's decoder) and compares its RMS with the bake
/// report. Nothing is played.
/// </summary>
static class Verifier
{
    public static int Run(string[] a)
    {
        if (a.Length == 0) { Console.Error.WriteLine("usage: verify <set>"); return 2; }
        string dir = Path.Combine(Program.OutRoot, a[0]);
        var m = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!.AsObject();
        var rep = File.Exists(Path.Combine(dir, "report.json")) ? JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "report.json"))) : null;
        var rms = new Dictionary<string, double>();
        if (rep?["perTake"] is JsonArray pt) foreach (var t in pt) rms[(string)t["File"]] = (double)t["rmsDb"];
        var errors = new List<string>();
        void Err(string s) { if (errors.Count < 200) errors.Add(s); }
        if ((int)m["version"] != 1 || (int)m["sampleRate"] != 48000) Err("header: version/sampleRate");
        var buses = m["buses"].AsArray().Select(b => (string)b["path"]).ToHashSet(StringComparer.Ordinal);
        int events = 0, takes = 0, files = 0; long bytes = 0; var seen = new HashSet<string>();
        var codecCount = new Dictionary<string, int>();
        double maxRmsDiff = 0; int nearSilent = 0;
        int rolled = 0, released = 0, silentDef = 0, stopFrames = 0;
        foreach (var e in m["events"].AsArray())
        {
            events++;
            string path = (string)e["path"];
            if (!buses.Contains((string)e["bus"])) Err($"{path}: unknown bus {e["bus"]}");
            var pnames = e["params"].AsArray().Select(p => (string)p["name"]).ToHashSet(StringComparer.Ordinal);
            bool oneshot = (bool)e["oneshot"];
            // optional fields added for the mixer (STOPPED frame, release, 3D, silent defaults)
            if (e["rolloff"] is JsonArray ro)
            {
                double prev = double.MaxValue, prevD = -1;
                foreach (var p in ro)
                {
                    double d = (double)p["d"], g = (double)p["gain"];
                    if (!(g >= 0 && g < 4) || d <= prevD || g > prev + 1e-9) Err($"{path}: rolloff point d={d} gain={g} (must rise in d, fall in gain)");
                    prev = g; prevD = d;
                }
                rolled++;
            }
            if (e["pan"] is JsonObject pan)
            {
                foreach (var p in pan["az"].AsArray()) { double f = (double)p["far"]; if (!(f >= 0 && f <= 1)) Err($"{path}: pan far {f}"); }
                foreach (var p in pan["near"].AsArray()) { double b = (double)p["blend"]; if (!(b >= 0 && b <= 1)) Err($"{path}: pan blend {b}"); }
            }
            if (e["releaseCurve"] is JsonArray rc)
            {
                double pm = -1;
                foreach (var p in rc)
                {
                    double ms = (double)p["ms"], g = (double)p["gain"];
                    if (ms <= pm || !(g >= 0 && g <= 1)) Err($"{path}: release point {ms} ms gain {g}");
                    pm = ms;
                }
                if ((double)rc[^1]["gain"] != 0) Err($"{path}: release curve does not end at 0");
                released++;
            }
            if (e["silentAtDefault"] is JsonNode sd && (bool)sd)
            {
                silentDef++;
                if (e["takes"].AsArray().Any(t => t["paramValues"] == null)) Err($"{path}: silentAtDefault but has a default take");
            }
            foreach (var t in e["takes"].AsArray())
            {
                if (t["stopFrame"] is JsonNode sf) { int v = (int)sf; if (v < 0 || v > (int)t["frames"] + 300 * 48000) Err($"{t["file"]}: stopFrame {v}"); if (t["loop"] != null) Err($"{t["file"]}: stopFrame on a loop"); stopFrames++; }
            }
            foreach (var t in e["takes"].AsArray())
            {
                takes++;
                string file = (string)t["file"], codec = (string)t["codec"];
                int ch = (int)t["channels"], rate = (int)t["rate"], frames = (int)t["frames"];
                if (rate != 48000) Err($"{file}: rate {rate}");
                if (ch is not (1 or 2)) Err($"{file}: channels {ch}");
                if (t["loop"] is JsonObject lp)
                {
                    int s = (int)lp["start"], en = (int)lp["end"];
                    if (s < 0 || en <= s || en > frames) Err($"{file}: loop {s}..{en} outside 0..{frames}");
                }
                if (t["paramValues"] is JsonObject pv) foreach (var kv in pv) if (!pnames.Contains(kv.Key)) Err($"{file}: paramValues {kv.Key} not in params");
                if (codec is "pcm16" or "ima-adpcm" && frames > Baker.ShortFrames && t["loop"] == null) Err($"{file}: {codec} take longer than 4 s");
                codecCount[codec] = codecCount.GetValueOrDefault(codec) + 1;
                if (!seen.Add(file)) continue;   // a de-duplicated variant: same file, checked once
                string abs = Path.Combine(dir, file);
                if (!File.Exists(abs)) { Err($"{file}: missing"); continue; }
                var b = File.ReadAllBytes(abs);
                files++; bytes += b.Length;
                try
                {
                    float[] pcm; int dch;
                    if (codec == "pcm16")
                    {
                        CheckWavHeader(b, 1, ch, 44, file, Err);
                        if (BitConverter.ToInt32(b, 40) != frames * ch * 2) Err($"{file}: data size != frames*ch*2");
                        (pcm, dch, _, _, _) = Encode.ReadWav(b);
                    }
                    else if (codec == "ima-adpcm")
                    {
                        CheckWavHeader(b, 0x11, ch, 60, file, Err);
                        int align = BitConverter.ToUInt16(b, 32), spb = BitConverter.ToUInt16(b, 38), fact = BitConverter.ToInt32(b, 48);
                        if (align != 1024 * ch || spb != 2041) Err($"{file}: blockAlign {align} spb {spb}");
                        if (fact != frames) Err($"{file}: fact {fact} != frames {frames}");
                        if (BitConverter.ToInt32(b, 56) != (frames + spb - 1) / spb * align) Err($"{file}: data size");
                        (pcm, dch, _, _, _) = Encode.ReadWav(b);
                    }
                    else if (codec == "aac-adts")
                    {
                        var info = Encode.AdtsInfo(b);
                        if (info.profile != 1) Err($"{file}: ADTS profile {info.profile} (want 1 = LC)");
                        if (info.sampleRateIndex != 3) Err($"{file}: ADTS sampling index {info.sampleRateIndex} (want 3 = 48 kHz)");
                        if (info.channels != ch) Err($"{file}: ADTS channels {info.channels} != {ch}");
                        int delay = (int?)t["encoderDelay"] ?? -1;
                        if (delay < 0) Err($"{file}: no encoderDelay");
                        if ((long)info.frames * 1024 < delay + frames) Err($"{file}: {info.frames} ADTS frames < delay+frames");
                        var (dec, dc) = Encode.DecodeAac(b);
                        dch = dc;
                        int n = Math.Min(frames, dec.Length / dc - delay);
                        pcm = new float[n * dc];
                        Array.Copy(dec, delay * dc, pcm, 0, n * dc);
                        if (n < frames) Err($"{file}: decoded {n} < {frames} frames after the delay");
                    }
                    else { Err($"{file}: unknown codec {codec}"); continue; }
                    if (dch != ch) Err($"{file}: decoded channels {dch} != {ch}");
                    if (rms.TryGetValue(file, out var want))
                    {
                        double got = Analysis.Db(Analysis.Rms(pcm));
                        double d = Math.Abs(got - want);
                        // below -70 dBFS RMS a take is near-silent and AAC legitimately drops it toward its noise floor
                        if (want > -70) { maxRmsDiff = Math.Max(maxRmsDiff, d); if (d > 1.0) Err($"{file}: decoded RMS {got:F2} dB vs baked {want:F2} dB"); }
                        else nearSilent++;
                    }
                }
                catch (Exception ex) { Err($"{file}: {ex.Message}"); }
            }
        }
        foreach (var s in m["snapshots"].AsArray())
            if (s["busVolumes"] is JsonObject bv) foreach (var kv in bv) if (!buses.Contains(kv.Key)) Err($"{s["path"]}: unknown bus {kv.Key}");
        Program.Log($"verify '{a[0]}': {events} events, {takes} takes, {files} files, {bytes / 1048576.0:F1} MiB [{string.Join(", ", codecCount.Select(kv => kv.Key + " " + kv.Value))}]; max |decoded RMS - baked RMS| = {maxRmsDiff:F3} dB over takes above -70 dBFS RMS ({nearSilent} near-silent takes not compared); {errors.Count} problems");
        Program.Log($"  optional fields: stopFrame on {stopFrames} takes, rolloff+pan on {rolled} events, release curve on {released}, silentAtDefault {silentDef}");
        foreach (var e in errors.Take(40)) Program.Log("  " + e);
        return errors.Count == 0 ? 0 : 1;
    }

    static void CheckWavHeader(byte[] b, int fmt, int ch, int dataAt, string file, Action<string> err)
    {
        string s(int o) => Encoding.ASCII.GetString(b, o, 4);
        if (s(0) != "RIFF" || s(8) != "WAVE" || s(12) != "fmt ") { err($"{file}: not RIFF/WAVE/fmt "); return; }
        if (BitConverter.ToInt32(b, 4) != b.Length - 8) err($"{file}: RIFF size");
        if (BitConverter.ToUInt16(b, 20) != fmt) err($"{file}: format {BitConverter.ToUInt16(b, 20)} != {fmt}");
        if (BitConverter.ToUInt16(b, 22) != ch) err($"{file}: channels");
        if (BitConverter.ToInt32(b, 24) != 48000) err($"{file}: rate");
        if (s(dataAt - 8) != "data") err($"{file}: 'data' not at {dataAt - 8}");
        if (dataAt == 60 && s(40) != "fact") err($"{file}: 'fact' not at 40");
    }
}
