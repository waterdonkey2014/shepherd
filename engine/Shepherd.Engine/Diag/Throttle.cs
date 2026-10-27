using System.Diagnostics;
namespace Shepherd.Engine.Diag;

/// <summary>kit3 play mode (vita/notes/play-mode.md), measurement only: SHEPHERD_FRAME_MS=&lt;ms&gt; pads every boot frame to at least that
/// much wall time (sleep at the end of the frame), so a Mac run sees the device's frame rate (e.g. 166 = the ~6 fps of the interpreted
/// GL menu on the Vita). With the wall clock (SHEPHERD_TIME=wall) the game then gets ~0.166 s of game time per frame, exactly what the
/// Vita gives it; with the frame clock it still gets 1/60 s. Unset or 0: nothing (no clock read, no sleep).</summary>
[ProfileOnly]
public static class Throttle
{
    static readonly double Ms = double.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_FRAME_MS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;
    static long _last; static long _frames, _padded; static double _sleptMs;

    /// <summary>End of every boot frame.</summary>
    public static void After()
    {
        if (Ms <= 0) return;
        long now = Stopwatch.GetTimestamp();
        if (_last == 0) { _last = now; Log.Info($"[throttle] SHEPHERD_FRAME_MS={Ms}: every frame padded to >= {Ms} ms of wall time ({1000 / Ms:F1} fps at most)"); return; }
        double el = (now - _last) * 1000.0 / Stopwatch.Frequency, want = Ms - el;
        _frames++;
        if (want > 0.05)
        {
            _padded++; _sleptMs += want;
            long until = now + (long)(want * Stopwatch.Frequency / 1000.0);
            if (want > 2) Thread.Sleep(TimeSpan.FromMilliseconds(want - 1));
            while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(64);   // the last ms spun: frame-time accuracy, not sleep granularity
        }
        _last = Stopwatch.GetTimestamp();
    }

    public static string Summary() => Ms <= 0 ? null : $"[throttle] {_frames} frames, {_padded} padded to {Ms} ms ({_sleptMs / 1000:F1} s slept)";
}
