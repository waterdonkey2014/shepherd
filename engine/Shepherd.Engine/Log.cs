namespace Shepherd.Engine;

public static class Log
{
    static readonly object Gate = new();
    static StreamWriter _file;
    public static int Errors, Warnings, Exceptions;
    public static bool Echo = true;
    static readonly Dictionary<string, int> Seen = new();
    /// <summary>Kit #8 speed track (Runtime/K8Speed.cs LogDiet): default AutoFlush per line (kit #7; review fix: the test kits keep every line
    /// on the card up to a hard crash). SHEPHERD_K8_LOG_DIET=batch / ring (opt-in): the file is not flushed per line; the Boot loop flushes it
    /// once per frame (LogDiet.EndFrame), the writer when its buffers fill, and Flush at the end / a managed unhandled exception / process
    /// exit -- the same lines in the same order, in fewer card writes; a hard crash loses the buffered lines (LogDiet's doc: what survives where).</summary>
    public static void Open(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)); _file = new StreamWriter(path, false) { AutoFlush = !Runtime.LogDiet.Batch }; Runtime.LogDiet.Opened(); }
    public static void Write(string tag, string msg) => Write(tag, msg, true);
    /// <summary>toFile false (kit #8 SHEPHERD_K8_LOG_DIET=ring, the game's own Debug.Log / LogWarning): the console echo only -- on the Vita the
    /// host's capture puts it in the black box's engine-line ring; engine.log does not get it.</summary>
    public static void Write(string tag, string msg, bool toFile)
    {
        var line = $"[{Runtime.Time.FrameCount,5}] {tag} {msg}";
        lock (Gate) { if (toFile) { _file?.WriteLine(line); Runtime.LogDiet.Dirty = true; } if (Echo) Console.WriteLine(line.Length > 400 ? line[..400] + "…" : line); }
    }
    /// <summary>Kit #8: the buffered lines to the file now (no-op when nothing was written since the last flush, and with the default
    /// AutoFlush per line: everything is on the card already).</summary>
    public static void Flush()
    {
        if (!Runtime.LogDiet.Batch) return;
        lock (Gate) { if (_file == null || !Runtime.LogDiet.Dirty) return; Runtime.LogDiet.Dirty = false; Runtime.LogDiet.Flushes++; try { _file.Flush(); } catch (Exception) { } }
    }
    public static void Info(string m) => Write("   ", m);
    public static void Warn(string m) { Warnings++; Write("WRN", m); }
    public static void Error(string m) { Errors++; Write("ERR", m); }
    public static void Exception(Exception e, string where)
    {
        Exceptions++;
        var inner = e; while ((inner is System.Reflection.TargetInvocationException || inner is TypeInitializationException) && inner.InnerException != null) inner = inner.InnerException;
        var st = inner.StackTrace ?? "";
        var lines = st.Split('\n').Where(l => !l.Contains("System.Reflection") && !l.Contains("System.RuntimeMethodHandle") && !Render.HotCfg.IsCallFrame(l)).Take(6);   // (menu-speed: the frame a phase message's delegate is caught in, see Lifecycle.CallD)
        var key = where + "|" + inner.GetType().Name + "|" + inner.Message;
        lock (Gate) { Seen.TryGetValue(key, out var n); Seen[key] = n + 1; if (n > 0) { if (n == 1 || n == 10 || n == 100 || n == 1000) Write("EXC", $"(repeat x{n + 1}) {where}: {inner.GetType().Name}: {inner.Message}"); return; } }
        Write("EXC", $"{where}: {inner.GetType().Name}: {inner.Message}\n      " + string.Join("\n      ", lines.Select(l => l.Trim())));
    }
}
