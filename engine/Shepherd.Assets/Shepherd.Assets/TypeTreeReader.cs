using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;

namespace Shepherd.Assets;

/// <summary>Decoded object: ordered field map. Missing fields read as null through the indexer.</summary>
public sealed class TypeTreeObject : IEnumerable<KeyValuePair<string, object>>
{
    private List<string> _keys;
    private readonly List<object> _values;
    private Dictionary<string, int> _index;
    /// <summary>The key list (and its lookup index) belong to a pack shape and are shared by every struct of that shape
    /// (PackReader): read-only here; an Add copies them first.</summary>
    private bool _keysShared;

    public string TypeName { get; }
    public IReadOnlyList<string> Keys => _keys;
    public IReadOnlyList<object> Values => _values;
    public int Count => _keys.Count;
    /// <summary>Bytes consumed when this object was the root of a read (0 for nested objects).</summary>
    public int BytesRead { get; internal set; }

    public TypeTreeObject(string typeName, int capacity = 4)
    {
        TypeName = typeName;
        _keys = new List<string>(capacity);
        _values = new List<object>(capacity);
    }

    /// <summary>A struct whose keys are a pack shape's (shared, never modified through this object); the decoder appends the
    /// values in key order with <see cref="AddValue"/>.</summary>
    internal TypeTreeObject(string typeName, List<string> sharedKeys, Dictionary<string, int> sharedIndex)
    {
        TypeName = typeName; _keys = sharedKeys; _index = sharedIndex; _keysShared = true;
        _values = new List<object>(sharedKeys.Count);
    }
    internal void AddValue(object value) => _values.Add(value);

    internal void Add(string key, object value)
    {
        if (_keysShared) { _keys = new List<string>(_keys); _index = null; _keysShared = false; }
        _keys.Add(key);
        _values.Add(value);
        if (_index != null) _index.TryAdd(key, _keys.Count - 1);
    }

    /// <summary>Replace the value of an existing field (pack baker: inlining streams, dropping image data). False if absent.</summary>
    internal bool Set(string key, object value)
    {
        int i = IndexOf(key);
        if (i < 0) return false;
        _values[i] = value;
        return true;
    }

    public object this[string field] => TryGet(field, out var v) ? v : null;

    public bool TryGet(string field, out object value)
    {
        int i = IndexOf(field);
        if (i < 0) { value = null; return false; }
        value = _values[i];
        return true;
    }

    public bool Has(string field) => IndexOf(field) >= 0;

    private int IndexOf(string field)
    {
        if (_keys.Count <= 8)
        {
            for (int i = 0; i < _keys.Count; i++) if (string.Equals(_keys[i], field, StringComparison.Ordinal)) return i;
            return -1;
        }
        if (_index == null)
        {
            _index = new Dictionary<string, int>(_keys.Count, StringComparer.Ordinal);
            for (int i = 0; i < _keys.Count; i++) _index.TryAdd(_keys[i], i);
        }
        return _index.TryGetValue(field, out var k) ? k : -1;
    }

    // ---- convenience accessors (numeric conversion is lenient) ----
    public T Get<T>(string field) => this[field] is T t ? t : default;
    public TypeTreeObject Obj(string field) => this[field] as TypeTreeObject;
    public List<object> List(string field) => this[field] as List<object>;
    public string Str(string field) => this[field] as string;
    public PPtr PPtr(string field) => this[field] is PPtr p ? p : default;
    public byte[] Bytes(string field) => this[field] as byte[];
    public long Long(string field) => ToLong(this[field]);
    public int Int(string field) => (int)ToLong(this[field]);
    public float Float(string field) => this[field] switch { float f => f, double d => (float)d, var o => ToLong(o) };
    public bool Bool(string field) => this[field] switch { bool b => b, var o => ToLong(o) != 0 };

    public static long ToLong(object o) => o switch
    {
        null => 0, bool b => b ? 1 : 0, sbyte v => v, byte v => v, short v => v, ushort v => v, int v => v, uint v => v,
        long v => v, ulong v => (long)v, char c => c, float f => (long)f, double d => (long)d, _ => 0,
    };

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        for (int i = 0; i < _keys.Count; i++) yield return new KeyValuePair<string, object>(_keys[i], _values[i]);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"{TypeName} {{{string.Join(", ", _keys)}}}";

    /// <summary>Pretty multi-line dump; arrays truncated to <paramref name="maxItems"/>.</summary>
    public string ToPrettyString(int maxItems = 8, int maxDepth = 64)
    {
        var sb = new StringBuilder();
        sb.Append(TypeName).Append('\n');
        WriteFields(sb, this, 1, maxItems, maxDepth);
        return sb.ToString();
    }

    private static void WriteFields(StringBuilder sb, TypeTreeObject o, int depth, int maxItems, int maxDepth)
    {
        for (int i = 0; i < o._keys.Count; i++)
        {
            sb.Append(' ', depth * 2).Append(o._keys[i]).Append(": ");
            WriteValue(sb, o._values[i], depth, maxItems, maxDepth);
        }
    }

    private static void WriteValue(StringBuilder sb, object v, int depth, int maxItems, int maxDepth)
    {
        switch (v)
        {
            case null: sb.Append("null\n"); break;
            case string s: sb.Append('"').Append(s.Length > 200 ? s[..200] + "…" : s).Append("\"\n"); break;
            case byte[] b:
                sb.Append($"byte[{b.Length}]");
                if (b.Length > 0) sb.Append(' ').Append(Convert.ToHexString(b, 0, Math.Min(b.Length, 32)).ToLowerInvariant()).Append(b.Length > 32 ? "…" : "");
                sb.Append('\n');
                break;
            case TypeTreeObject t:
                if (depth >= maxDepth) { sb.Append(t.TypeName).Append(" {…}\n"); break; }
                if (t.Count == 0) { sb.Append(t.TypeName).Append(" {}\n"); break; }
                // compact one-liners for small all-scalar structs (Vector3f, ColorRGBA, ...)
                if (t.Count <= 4 && t._values.All(x => x is not (TypeTreeObject or List<object> or byte[] or string)))
                {
                    sb.Append(t.TypeName).Append(" { ");
                    for (int i = 0; i < t.Count; i++) sb.Append(i > 0 ? ", " : "").Append(t._keys[i]).Append('=').Append(Fmt(t._values[i]));
                    sb.Append(" }\n");
                    break;
                }
                sb.Append(t.TypeName).Append('\n');
                WriteFields(sb, t, depth + 1, maxItems, maxDepth);
                break;
            case List<object> l:
                sb.Append($"[{l.Count}]");
                if (l.Count == 0) { sb.Append('\n'); break; }
                if (l.All(x => x is not (TypeTreeObject or List<object> or byte[] or string)))
                {
                    sb.Append(' ').Append(string.Join(", ", l.Take(maxItems * 4).Select(Fmt))).Append(l.Count > maxItems * 4 ? ", …" : "").Append('\n');
                    break;
                }
                sb.Append('\n');
                for (int i = 0; i < Math.Min(l.Count, maxItems); i++)
                {
                    sb.Append(' ', (depth + 1) * 2).Append('[').Append(i).Append("] ");
                    WriteValue(sb, l[i], depth + 1, maxItems, maxDepth);
                }
                if (l.Count > maxItems) sb.Append(' ', (depth + 1) * 2).Append($"… {l.Count - maxItems} more\n");
                break;
            default: sb.Append(Fmt(v)).Append('\n'); break;
        }
    }

    private static string Fmt(object v) => v switch
    {
        float f => f.ToString("G6", System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("G9", System.Globalization.CultureInfo.InvariantCulture),
        PPtr p => $"PPtr({p.FileId},{p.PathId})",
        char c => c == 0 ? "'\\0'" : $"'{c}'",
        bool b => b ? "true" : "false",
        null => "null",
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
    };
}

public sealed class TypeTreeReadException : Exception
{
    public string FieldPath { get; }
    public TypeTreeReadException(string path, Exception inner) : base($"type tree read failed at {path}: {inner.Message}", inner) { FieldPath = path; }
}

public static class TypeTreeReader
{
    internal enum K : byte
    {
        Bool, I8, U8, Char, I16, U16, I32, U32, I64, U64, F32, F64,
        String, PPtr, Vector, TypelessData, Struct, RefObjData,
    }

    internal sealed class CNode
    {
        public TypeTreeNode N;
        public CNode[] Children = Array.Empty<CNode>();
        public K Kind;
        public bool Align;
        public CNode Elem;       // vectors: element node
        public bool ArrayAlign;  // vectors/strings: the Array child's align flag
        public bool PPtr32;      // PPtr with 32-bit path id (old)
        public string Name => N.Name;
    }

    private static readonly ConditionalWeakTable<List<TypeTreeNode>, CNode> s_compiled = new();

    internal static CNode Compile(List<TypeTreeNode> nodes)
    {
        if (s_compiled.TryGetValue(nodes, out var c)) return c;
        lock (s_compiled)
        {
            if (s_compiled.TryGetValue(nodes, out c)) return c;
            int i = 0;
            c = Build(nodes, ref i);
            s_compiled.AddOrUpdate(nodes, c);
            return c;
        }
    }

    private static CNode Build(List<TypeTreeNode> nodes, ref int i)
    {
        var n = nodes[i];
        var c = new CNode { N = n, Align = n.AlignAfter };
        i++;
        var kids = new List<CNode>();
        while (i < nodes.Count && nodes[i].Level > n.Level)
        {
            if (nodes[i].Level != n.Level + 1) throw new InvalidDataException($"type tree level jump at node {i} ({nodes[i].Name})");
            kids.Add(Build(nodes, ref i));
        }
        c.Children = kids.ToArray();
        Classify(c);
        return c;
    }

    private static void Classify(CNode c)
    {
        string t = c.N.Type;
        if (c.Children.Length == 0)
        {
            c.Kind = t switch
            {
                "bool" => K.Bool,
                "SInt8" => K.I8,
                "UInt8" => K.U8,
                "char" => K.Char,
                "SInt16" or "short" => K.I16,
                "UInt16" or "unsigned short" => K.U16,
                "SInt32" or "int" => K.I32,
                "UInt32" or "unsigned int" or "Type*" => K.U32,
                "SInt64" or "long long" => K.I64,
                "UInt64" or "unsigned long long" or "FileSize" => K.U64,
                "float" => K.F32,
                "double" => K.F64,
                "ReferencedObjectData" => K.RefObjData,
                _ => K.Struct,
            };
            return;
        }
        if (t == "string" && c.Children.Length == 1 && c.Children[0].N.Type == "Array")
        {
            c.Kind = K.String;
            c.ArrayAlign = c.Children[0].Align;
            return;
        }
        if (t == "TypelessData")
        {
            c.Kind = K.TypelessData;
            return;
        }
        if (t.StartsWith("PPtr<", StringComparison.Ordinal) && c.Children.Length == 2)
        {
            c.Kind = K.PPtr;
            c.PPtr32 = c.Children[1].N.ByteSize == 4;
            return;
        }
        CNode arr = t == "Array" ? c : (c.Children.Length == 1 && c.Children[0].N.Type == "Array" ? c.Children[0] : null);
        if (arr != null)
        {
            if (arr.Children.Length != 2) throw new InvalidDataException($"Array node {c.Name} has {arr.Children.Length} children");
            c.Kind = K.Vector;
            c.Elem = arr.Children[1];
            c.ArrayAlign = arr.Align;
            return;
        }
        c.Kind = K.Struct;
    }

    /// <summary>Decode an object of a serialized file (uses its own type tree, or the registered fallback when stripped).</summary>
    public static TypeTreeObject Read(SerializedFile file, ObjectInfo obj)
    {
        if (file.Pack != null) { AssetTrace.ObjectRead?.Invoke(file, obj, AssetTrace.Access.Tree); return file.Pack.ReadTree(file, obj); }   // V3 pack: stored value tree, no type tree work
        var nodes = file.GetTypeTree(obj) ?? throw new InvalidOperationException(
            $"{file.Name}: no type tree for {ClassIds.Name(obj.ClassId)} (class {obj.ClassId}); register a fallback");
        var prev = AssetTrace.Current; AssetTrace.Current = AssetTrace.Access.Tree;
        try { return ReadCore(file.GetObjectBytes(obj), nodes, file); }
        finally { AssetTrace.Current = prev; }
    }

    public static TypeTreeObject Read(ReadOnlySpan<byte> data, List<TypeTreeNode> nodes) => ReadCore(data, nodes, null);

    /// <summary>Generic read with a file context (needed for [SerializeReference] payloads).</summary>
    public static TypeTreeObject Read(ReadOnlySpan<byte> data, List<TypeTreeNode> nodes, SerializedFile context) => ReadCore(data, nodes, context);

    private static TypeTreeObject ReadCore(ReadOnlySpan<byte> data, List<TypeTreeNode> nodes, SerializedFile file)
    {
        if (nodes == null || nodes.Count == 0) throw new ArgumentException("empty type tree");
        var root = Compile(nodes);
        var r = new SpanReader(data, file?.BigEndianData ?? false);
        var o = ReadStruct(ref r, root, file);
        if (root.Align) r.Align(4);
        o.BytesRead = r.Position;
        return o;
    }

    private static TypeTreeObject ReadStruct(ref SpanReader r, CNode c, SerializedFile file)
    {
        var o = new TypeTreeObject(c.N.Type, c.Children.Length);
        foreach (var ch in c.Children)
        {
            object v;
            try
            {
                if (ch.Kind == K.RefObjData) v = ReadRefData(ref r, o, file);
                else v = ReadValue(ref r, ch, file);
            }
            catch (TypeTreeReadException e)
            {
                throw new TypeTreeReadException(ch.Name + "." + e.FieldPath, e.InnerException);
            }
            catch (Exception e)
            {
                throw new TypeTreeReadException($"{ch.Name} ({ch.N.Type}) @{r.Position}", e);
            }
            o.Add(ch.Name, v);
        }
        return o;
    }

    private static object ReadValue(ref SpanReader r, CNode c, SerializedFile file)
    {
        object v;
        switch (c.Kind)
        {
            case K.Bool: v = r.U8() != 0; break;
            case K.I8: v = r.I8(); break;
            case K.U8: v = r.U8(); break;
            case K.Char: v = (char)r.U8(); break;
            case K.I16: v = r.I16(); break;
            case K.U16: v = r.U16(); break;
            case K.I32: v = r.I32(); break;
            case K.U32: v = r.U32(); break;
            case K.I64: v = r.I64(); break;
            case K.U64: v = r.U64(); break;
            case K.F32: v = r.F32(); break;
            case K.F64: v = r.F64(); break;
            case K.String:
                v = r.LenString();
                if (c.ArrayAlign) r.Align(4);
                break;
            case K.PPtr:
            {
                int fid = r.I32();
                long pid = c.PPtr32 ? r.I32() : r.I64();
                v = new PPtr(fid, pid);
                break;
            }
            case K.TypelessData:
            {
                int n = r.I32();
                if (n < 0 || n > r.Remaining) throw new InvalidDataException($"bad TypelessData size {n}");
                v = r.Bytes(n).ToArray();
                break;
            }
            case K.Vector:
            {
                int n = r.I32();
                if (n < 0 || n > r.Remaining + 16) throw new InvalidDataException($"bad array size {n} (remaining {r.Remaining})");
                var e = c.Elem;
                if (e.Kind is K.U8 or K.I8)
                {
                    v = r.Bytes(n).ToArray();
                }
                else
                {
                    var list = new List<object>(n);
                    for (int i = 0; i < n; i++) list.Add(ReadValue(ref r, e, file));
                    v = list;
                }
                if (c.ArrayAlign) r.Align(4);
                break;
            }
            case K.RefObjData:
                throw new InvalidDataException("ReferencedObjectData outside a ReferencedObject");
            default:
                v = ReadStruct(ref r, c, file);
                break;
        }
        if (c.Align) r.Align(4);
        return v;
    }

    /// <summary>[SerializeReference] payload: the layout comes from the file's RefTypes entry named by the sibling "type".</summary>
    private static object ReadRefData(ref SpanReader r, TypeTreeObject owner, SerializedFile file)
    {
        var t = owner.Obj("type");
        string cls = t?.Str("class"), ns = t?.Str("ns"), asm = t?.Str("asm");
        if (string.IsNullOrEmpty(cls)) return null; // null reference / terminus: no payload
        if (file == null) throw new InvalidDataException("[SerializeReference] payload needs a SerializedFile context");
        foreach (var rt in file.RefTypes)
        {
            if (rt.Nodes is { Count: > 0 } && rt.KlassName == cls && rt.NameSpace == ns && rt.AsmName == asm)
            {
                var root = Compile(rt.Nodes);
                var o = ReadStruct(ref r, root, file);
                if (root.Align) r.Align(4);
                return o;
            }
        }
        throw new InvalidDataException($"[SerializeReference] type {ns}.{cls} ({asm}) not in the file's ref types");
    }

    /// <summary>The keys a decoded root object would have, in order, each followed by "key.sub" for the fields of a
    /// struct-valued key (exactly the names <c>foreach (kv in Read(...))</c> + the keys of TypeTreeObject values would give),
    /// computed from the type tree alone — no object bytes are read.</summary>
    public static IEnumerable<string> FieldPaths(List<TypeTreeNode> nodes)
    {
        if (nodes == null || nodes.Count == 0) yield break;
        var root = Compile(nodes);
        foreach (var ch in root.Children)
        {
            yield return ch.Name;
            if (ch.Kind == K.Struct) foreach (var g in ch.Children) yield return ch.Name + "." + g.Name;
        }
    }

    /// <summary><see cref="FieldPaths(List{TypeTreeNode})"/> for an object of a file; for a pack file (value trees, no type trees)
    /// the same names come from the decoded tree: each key, then "key.sub" for the keys of a struct value.</summary>
    public static IEnumerable<string> FieldPaths(SerializedFile file, ObjectInfo obj)
    {
        if (file.Pack == null) return FieldPaths(file.GetTypeTree(obj));
        if (!file.HasTree(obj)) return Array.Empty<string>();
        var t = file.Pack.ReadTree(file, obj); var res = new List<string>();
        foreach (var kv in t)
        {
            if (kv.Key == PackReader.ImageKey) continue;
            res.Add(kv.Key);
            if (kv.Value is TypeTreeObject s) foreach (var k in s.Keys) res.Add(kv.Key + "." + k);
        }
        return res;
    }

    /// <summary>Collect every PPtr in a decoded value (depth-first).</summary>
    public static void CollectPPtrs(object value, List<PPtr> into)
    {
        switch (value)
        {
            case PPtr p: if (!p.IsNull) into.Add(p); break;
            case TypeTreeObject o: foreach (var v in o.Values) CollectPPtrs(v, into); break;
            case List<object> l: foreach (var v in l) CollectPPtrs(v, into); break;
        }
    }
}
