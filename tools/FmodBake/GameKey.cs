using System.Text;
using Shepherd.Assets;

namespace Shepherd.FmodBake;

/// <summary>
/// Reads the FMOD bank encryption key from the game's FMODUnity.Settings asset ("FMODStudioSettings", resources.assets),
/// in memory only. The key is returned to the caller, which hands it straight to Studio::System::setAdvancedSettings.
/// It is NEVER printed, logged, written, hashed into a file or embedded anywhere: diagnostics report its length only.
/// </summary>
static class GameKey
{
    public sealed record Result(string Key, string How, int ObjectBytes);

    public static Result Read(string dataDir)
    {
        using var db = new AssetDatabase(dataDir);
        var f = db.GetFile("resources.assets") ?? throw new InvalidOperationException("resources.assets not found");
        foreach (var o in f.ObjectsOfClass(ClassIds.MonoBehaviour))
        {
            var ms = db.GetMonoScript(f, o);
            if (ms == null || ms.Namespace != "FMODUnity" || ms.ClassName != "Settings") continue;
            // 1) a full type tree (only when a bundle carried the same script layout)
            try
            {
                var t = TypeTreeReader.Read(f, o);
                if (t.Has("EncryptionKey") && t.Str("EncryptionKey") is { } k1) return new Result(k1, "typetree", (int)o.ByteSize);
            }
            catch { /* header-only tree: fall through to the layout parse */ }
            // 2) the serialized layout of FMODUnity.Settings (decomp/FMODUnity/FMODUnity/Settings.cs field order), validated
            var d = f.GetObjectBytes(o).ToArray();
            string err = null;
            foreach (bool alignBools in new[] { true, false })
            {
                var k = TryLayout(d, alignBools, out err, out _, out _);
                if (k == "") { err = "EncryptionKey empty"; continue; }
                if (k != null) return new Result(k, alignBools ? "layout(bool-aligned)" : "layout(packed bools)", d.Length);
            }
            throw new InvalidOperationException("FMODUnity.Settings found but its layout did not validate: " + err);
        }
        throw new InvalidOperationException("FMODUnity.Settings (FMODStudioSettings) not found in resources.assets");
    }

    /// <summary>tools/VitaPack (V6, packs carry no key): where the EncryptionKey field sits in a raw (stripped) Settings record:
    /// its int32 length at [lenPos, lenPos + 4), then the string and its padding up to <paramref name="end"/> (4-aligned).
    /// The whole layout must validate (as for <see cref="Read"/>); an empty key is located too. False when it does not validate.</summary>
    public static bool Locate(byte[] d, out int lenPos, out int end, out bool empty)
    {
        foreach (bool alignBools in new[] { true, false })
        {
            var k = TryLayout(d, alignBools, out _, out lenPos, out end);
            if (k != null) { empty = k.Length == 0; return true; }
        }
        lenPos = end = -1; empty = false; return false;
    }

    /// <summary>Walks the Settings fields up to and past EncryptionKey; every field must be plausible (bools 0/1 with zero
    /// padding, printable strings, small enums), else null; an empty key returns "". Error text names fields only, never values.</summary>
    static string TryLayout(byte[] d, bool alignBools, out string err, out int keyPos, out int keyEnd)
    {
        int p = 28; // PPtr m_GameObject (12) + UInt8 m_Enabled (aligned, 4) + PPtr m_Script (12)
        err = null; keyPos = keyEnd = -1;
        try
        {
            Str(d, ref p, "m_Name", 128);
            Bool(d, ref p, alignBools, "HasSourceProject");
            Bool(d, ref p, alignBools, "HasPlatforms");
            Str(d, ref p, "sourceProjectPath", 1024);
            Str(d, ref p, "sourceBankPath", 1024);
            Str(d, ref p, "SourceBankPathUnformatted", 1024);
            Int(d, ref p, -2, 100000, "BankRefreshCooldown");
            Bool(d, ref p, alignBools, "ShowBankRefreshWindow");
            Bool(d, ref p, alignBools, "AutomaticEventLoading");
            Int(d, ref p, 0, 8, "BankLoadType");
            Bool(d, ref p, alignBools, "AutomaticSampleLoading");
            int kp = p;
            string key = Str(d, ref p, "EncryptionKey", 256);
            int ke = p;
            Int(d, ref p, 0, 8, "ImportType");
            string target = Str(d, ref p, "TargetAssetPath", 1024);
            Str(d, ref p, "TargetBankFolder", 1024);
            Int(d, ref p, 0, 8, "EventLinkage");
            Int(d, ref p, 0, 0x7fffffff, "LoggingLevel");
            _ = target;
            keyPos = kp; keyEnd = ke;
            return key;
        }
        catch (FormatException e) { err = e.Message; return null; }
    }

    static string Str(byte[] d, ref int p, string field, int max)
    {
        if (p + 4 > d.Length) throw new FormatException($"{field}: past end");
        int len = BitConverter.ToInt32(d, p); p += 4;
        if (len < 0 || len > max || p + len > d.Length) throw new FormatException($"{field}: bad length");
        for (int i = 0; i < len; i++) if (d[p + i] < 0x20 || d[p + i] > 0x7e) throw new FormatException($"{field}: not printable");
        string s = Encoding.UTF8.GetString(d, p, len);
        p += len; Align(d, ref p, field);
        return s;
    }

    static void Bool(byte[] d, ref int p, bool align, string field)
    {
        if (p >= d.Length || d[p] > 1) throw new FormatException($"{field}: not a bool");
        p++;
        if (align) Align(d, ref p, field);
    }

    static void Int(byte[] d, ref int p, int min, int max, string field)
    {
        // Unity never pre-aligns: alignment only follows nodes that carry the align flag (strings, script bools)
        if (p + 4 > d.Length) throw new FormatException($"{field}: past end");
        int v = BitConverter.ToInt32(d, p); p += 4;
        if (v < min || v > max) throw new FormatException($"{field}: out of range");
    }

    static void Align(byte[] d, ref int p, string field)
    {
        while ((p & 3) != 0) { if (p >= d.Length || d[p] != 0) throw new FormatException($"{field}: nonzero padding"); p++; }
    }
}
