namespace VitaBudget;

// Plain field records (serialized with IncludeFields) — survey cache lives in out/survey.json (local only, never shared).

public sealed class TexRec
{
    public string B, Cab, Name; public long Pid; public int Cls, W, H, Fmt, Mips, Dim, Images;
    public long Inline, Stream, Obj; public bool Readable;
    public long Stored => Inline + Stream;
    public long Rgba8 => (long)W * H * 4 * Math.Max(1, Images);
}

public sealed class MeshRec { public string B, Cab, Name; public long Pid; public int Verts; public long Obj, Stream; }

public sealed class AudioRec { public string B, Cab, Name; public long Pid; public int Fmt, Ch, Freq, LoadType; public float Len; public long Obj, Res; }

public sealed class ShaderRec
{
    public string B, Cab, Name; public long Pid; public long Obj; public int Sub, Passes, Props, Kw;
    public List<string> PropNames; public List<string> PassNames; public string Err;
}

public sealed class MatRec { public string B, Cab, Name; public long Pid; public string ShCab; public long ShPid; }

public sealed class ScriptRec { public string Cab; public long Pid; public string Cls, Ns, Asm; }

public sealed class BundleRec
{
    public string Name; public long Disk, Uncomp, ResS, Resource, OtherNodes, SerBytes, StreamAttrib;
    public Dictionary<string, int> Comp = new();
    public List<string> SceneFiles = new(); public List<string> Cabs = new();
    /// <summary>category -> bytes. MonoBehaviour payloads outside scene files are NOT here (see Mb, categorized at report time).</summary>
    public Dictionary<string, long> Cat = new();
    /// <summary>class name -> [count, bytes]</summary>
    public Dictionary<string, long[]> Cls = new();
    /// <summary>"scriptCab:scriptPid" -> [count, bytes] for MonoBehaviours in non-scene files.</summary>
    public Dictionary<string, long[]> Mb = new();
    public List<TexRec> Tex = new(); public List<MeshRec> Mesh = new(); public List<AudioRec> Audio = new();
    public List<ShaderRec> Shaders = new(); public List<MatRec> Mats = new(); public List<ScriptRec> Scripts = new();
    public int DetailErrors; public string Err;
    public bool IsScene => SceneFiles.Count > 0;
}

public sealed class Survey
{
    public string DataDir; public DateTime When;
    public List<BundleRec> Bundles = new();
}
