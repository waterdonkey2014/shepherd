using Shepherd.Assets;
using Shepherd.Engine.Native;
using Shepherd.Engine.Render;
using System.Runtime.InteropServices;
namespace Shepherd.Engine.Assets;

/// <summary>V3 part 2 (vita/notes/v3b-pack.md): <c>SHEPHERD_PACK=&lt;dir&gt;</c> serves the asset layer from a Vita pack baked by
/// tools/VitaPack instead of the bundles and loose serialized files. Same file names, PathIds and externals, so LazyRefs and
/// PPtrs resolve to the same objects; payloads are value trees (no TypeTree work, no file buffers, no type-tree fallbacks)
/// and texture images are read from the pack when they are uploaded (DXT blocks go up as they are; nothing is kept).
/// Anything the pack lacks is logged loudly as an error naming the file / PathId, then loaded from the game data — only on
/// the Mac (bundles present) and not with <c>SHEPHERD_PACK_STRICT=1</c>, which behaves like the Vita (a miss resolves to null).</summary>
[ProfileOnly]
public static class PackAssets
{
    public static readonly string Dir = Environment.GetEnvironmentVariable("SHEPHERD_PACK");
    public static readonly bool Strict = Environment.GetEnvironmentVariable("SHEPHERD_PACK_STRICT") is "1" or "true" or "yes";
    public static PackReader Reader;
    public static bool On => Reader != null;
    public static int Misses, GpuUploads, GpuRgbaUploads, GpuAlphaUploads, SdfOfTranscoded;
    public static long GpuUploadBytes;
    static readonly HashSet<string> Seen = new(StringComparer.Ordinal);

    /// <summary>Called by Loader.Init right after the AssetDatabase is created. No-op when SHEPHERD_PACK is unset.</summary>
    public static void Init(AssetDatabase db)
    {
        if (string.IsNullOrEmpty(Dir)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // SHEPHERD_PACK_WINDOW=<KiB>:<count>: the reader's read-ahead windows (default 128:8; device tuning)
        if (Environment.GetEnvironmentVariable("SHEPHERD_PACK_WINDOW") is { } wv && wv.Split(':') is [var ws, var wc] && int.TryParse(ws, out var kib) && int.TryParse(wc, out var cnt) && kib > 0 && cnt > 0)
        { PackReader.WindowSize = kib << 10; PackReader.WindowCount = cnt; }
        // k4-display: the read-ahead worker (Shepherd.Assets PackReader.ReadAhead): SHEPHERD_PACK_READAHEAD=1 turns it on (an A/B for the
        // device's boot time); OFF by default (k4 review: with it on, vlx runs of one build kept the same GC counts but their heap and arena
        // peaks still differed by 1-4 MiB from run to run, and the gain is under a second of boot); under the Vita host its thread is pinned
        // to the 4th core when the core-unlock plugin allows it, else USER_2 (Diag/VitaHost.cs)
        PackReader.ReadAhead = Environment.GetEnvironmentVariable("SHEPHERD_PACK_READAHEAD") is { Length: > 0 } ra && ra.Trim() == "1";
        PackReader.OnReadAheadThread = Diag.VitaHost.PinWorker;
        Runtime.K10Io.ConfigurePack();   // kit #10 io: the chunked pack, decode-ahead, lanes, write discipline (SHEPHERD_K10_IO=0 = kit #9)
        Reader = Runtime.K10Io.OpenPack(Dir);
        db.Pack = Reader;
        db.PackFallback = !Strict && Directory.Exists(db.BundleDir);
        db.PackMiss = Miss; Reader.Miss = Miss;
        TextureDecoder.ReducedImageDecoded = s =>
        {
            if (Seen.Add("cpu " + s.Image.Offset))
                Log.Warn($"[pack] CPU read of a transcoded texture ({(Shepherd.Assets.TextureFormat)s.Format} {s.Width}x{s.Height}, pack image {s.Image}): served upscaled from the reduced image; re-record and re-bake so it is kept exact");
        };
        Log.Info($"[pack] {Reader.Path}: {Reader.ObjectCount} objects in {Reader.FileCount} files, TOC {Reader.TocBytes / 1048576.0:F2} MiB, opened in {sw.Elapsed.TotalMilliseconds:F1} ms; " +
                 $"fallback to the game data on a miss: {(db.PackFallback ? "yes (Mac)" : "NO (strict / Vita)")}; bake properties: {(Reader.Properties.Count == 0 ? "none" : string.Join(", ", Reader.Properties.Select(kv => kv.Key + "=" + kv.Value)))}");
        Log.Info(Runtime.K10Io.Describe(Reader));
    }

    /// <summary>A pack TextAsset's bytes, left in the pack: TextAsset.bytes / .text read them on each call (a fresh array, as
    /// Unity's own getter returns a copy); GetDataPtr materializes them once (it must stay pinned).</summary>
    public sealed class PackBytes
    {
        readonly SerializedFile _f; readonly ObjectInfo _o; readonly int _skip, _take; int _len = -1;
        public PackBytes(SerializedFile f, ObjectInfo o, int skip, int take) { _f = f; _o = o; _skip = skip; _take = take; }
        public int Length => _len >= 0 ? _len : _len = Math.Clamp(Math.Min(_take, _f.Pack.RawLength(_o) - _skip), 0, int.MaxValue);
        public byte[] Read() { Reads++; ReadBytes += Length; return _f.Pack.ReadRawRange(_f, _o, _skip, _take); }
        public static int Reads; public static long ReadBytes;
        /// <summary>kit6 mem (L1b, Runtime/SpineReadShare.cs): the same bytes as <see cref="Read"/>, as a read-only stream that fills a small buffer
        /// from the pack as it is read (no array of the whole record).</summary>
        public Stream OpenStream() { Streams++; StreamBytes += Length; return new RangeStream(_f, _o, _skip, Length); }
        /// <summary>kit #7 spine L2 (Runtime/SpineLazySkins.cs): the same bytes from <paramref name="offset"/> on (a lazy Spine skin's slot section), with
        /// an 8 KiB buffer (a skin's section is a few KiB).</summary>
        public Stream OpenStreamAt(int offset) { LazyStreams++; return new RangeStream(_f, _o, _skip + offset, Length - offset, 8 << 10); }
        public static int LazyStreams;
        public static int Streams; public static long StreamBytes;
    }

    /// <summary>kit6 mem (L1b): a forward reader over [skip, skip+length) of a pack object's raw bytes with MemoryStream's results for every call a
    /// reader makes (Read returns min(count, remaining), ReadByte -1 at the end); 32 KiB buffer, refilled from the pack (its read-ahead window).</summary>
    public sealed class RangeStream : Stream
    {
        readonly SerializedFile _f; readonly ObjectInfo _o; readonly int _skip, _len; int _pos, _bufAt, _bufN; byte[] _buf;
        public RangeStream(SerializedFile f, ObjectInfo o, int skip, int length) : this(f, o, skip, length, 32 << 10) { }
        public RangeStream(SerializedFile f, ObjectInfo o, int skip, int length, int buffer) { _f = f; _o = o; _skip = skip; _len = length; _buf = new byte[buffer]; }
        public override bool CanRead => _buf != null; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => _len;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        bool Fill()
        {
            if (_pos >= _len) return false;
            _bufAt = _pos; _bufN = _f.Pack.ReadRawInto(_f, _o, _skip + _pos, _buf.AsSpan(0, Math.Min(_buf.Length, _len - _pos)));
            if (_bufN <= 0) throw new EndOfStreamException($"{_f.Name} #{_o.PathId}: the pack record ends at {_pos} of {_len} bytes");
            return true;
        }
        public override int ReadByte()
        {
            if (_pos - _bufAt >= _bufN || _pos < _bufAt) { if (!Fill()) return -1; }
            return _buf[_pos++ - _bufAt];
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> dst)
        {
            int done = 0;
            while (done < dst.Length && _pos < _len)
            {
                if (_pos - _bufAt >= _bufN || _pos < _bufAt) Fill();
                int k = Math.Min(dst.Length - done, _bufN - (_pos - _bufAt));
                _buf.AsSpan(_pos - _bufAt, k).CopyTo(dst[done..]); done += k; _pos += k;
            }
            return done;
        }
        protected override void Dispose(bool disposing) { _buf = null; base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A bake parameter of the pack (tools/VitaPack), or null (no pack, or a pack baked without it).</summary>
    public static string Property(string key) => Reader != null && Reader.Properties.TryGetValue(key, out var v) ? v : null;

    static void Miss(string what)
    {
        lock (Seen) { if (!Seen.Add(what)) return; Misses++; }
        Log.Error($"[pack] MISSING from pack: {what}");
    }

    /// <summary>Scene instantiation enumerates a file's GameObjects instead of resolving PathIds, so a pack copy that lacks some
    /// would give a silently partial scene: report it (loudly) and, on the Mac, instantiate the game's own file instead.</summary>
    public static SerializedFile SceneFile(AssetDatabase db, SerializedFile f)
    {
        int have = f.ObjectsOfClass(1).Count();   // reports a gap through PackReader.Miss
        return have >= f.PackSourceCount(1) ? f : db.PackOriginal(f.Name) ?? f;
    }

    /// <summary>Upload a texture whose pack image is a GPU image (reduced size, top-down rows): DXT blocks as they are when the
    /// backend takes them, else RGBA8 decoded at the image's own size (uvs are normalized). Nothing is kept on the CPU.</summary>
    public static IntPtr Upload(NTexture t, bool sdf, IRenderBackend g)
    {
        var img = t.Src.Image;
        if (sdf && Seen.Add("sdf " + t.Name)) { SdfOfTranscoded++; Log.Info($"[pack] '{t.Name}' ({(Shepherd.Assets.TextureFormat)t.Format} {t.Width}x{t.Height}) is drawn through the SDF path from a transcoded {img}: expected for TextMeshPro/Sprite icon atlases; a real font atlas here would be a bake classification gap"); }
        string key;
        IntPtr h = IntPtr.Zero;
        if (!sdf && g is ICompressedUpload { Dxt: true } cu && DxtBlocks.BlockBytes(img.Format) > 0)
        {
            if (Runtime.K6Engine.TexNative && g is ICompressedUploadPtr cp && UploadDxtNative(img, cp, t.FilterMode != 0) is var hd && hd != IntPtr.Zero)
            { GpuUploads++; GpuUploadBytes += img.Length; key = $"pack DXT{(img.Format == DxtBlocks.UnityDXT1 ? 1 : 5)} {(img.Width == t.Width ? "full" : "reduced")} (from {(Shepherd.Assets.TextureFormat)t.Format})"; Count(key, t); return hd; }
            var blocks = img.Read();
            h = cu.CreateCompressedTexture(img.Width, img.Height, img.Format, blocks, t.FilterMode != 0, () => Rgba(img));
            if (h != IntPtr.Zero) { GpuUploads++; GpuUploadBytes += blocks.Length; key = $"pack DXT{(img.Format == DxtBlocks.UnityDXT1 ? 1 : 5)} {(img.Width == t.Width ? "full" : "reduced")} (from {(Shepherd.Assets.TextureFormat)t.Format})"; Count(key, t); return h; }
        }
        if (sdf && Runtime.K6Engine.TexNative && g is IAlphaUploadPtr ap && DxtBlocks.BlockBytes(img.Format) > 0 && Runtime.VitaMemory.Alpha8
            && UploadAlpha8Native(t, img, ap) is var hn && hn != IntPtr.Zero) { GpuAlphaUploads++; GpuUploadBytes += (long)img.Width * img.Height; Count($"pack SDF alpha8 decoded from {(Shepherd.Assets.TextureFormat)img.Format}", t); return hn; }
        if (sdf && g is IAlphaUpload au && DxtBlocks.BlockBytes(img.Format) > 0 && Runtime.VitaMemory.Alpha8)
        {   // an SDF draw keeps only the distance channel: decode just the alpha, 1 B/px (no 4 B/px RGBA8 transient)
            var a8 = TextureDecoder.DecodeAlpha8(img.Read(), img.Width, img.Height, (Shepherd.Assets.TextureFormat)img.Format);
            if (img.TopDown) TextureDecoder.FlipRowsAlpha8(a8, img.Width, img.Height);   // the rows Rgba() would give
            if (Environment.GetEnvironmentVariable("SHEPHERD_CHECK_A8") == "1")
            {   // diagnostic: the RGBA8 path's alpha, texel for texel
                var full = Rgba(img); int bad = 0; for (int i = 0; i < a8.Length; i++) if (a8[i] != full[i * 4 + 3]) bad++;
                Log.Info($"[pack] alpha8 check '{t.Name}' {img}: {bad} of {a8.Length} texels differ from the RGBA8 decode");
            }
            h = au.CreateSdfAlpha8Texture(img.Width, img.Height, a8, t.FilterMode != 0);
            if (h != IntPtr.Zero) { GpuAlphaUploads++; GpuUploadBytes += a8.Length; Count($"pack SDF alpha8 decoded from {(Shepherd.Assets.TextureFormat)img.Format}", t); return h; }
        }
        var px = Rgba(img);
        h = g.CreateTexture(img.Width, img.Height, px, t.FilterMode != 0, sdf, owned: true);
        GpuRgbaUploads++; GpuUploadBytes += px.Length;
        Count($"pack RGBA8 decoded from {(Shepherd.Assets.TextureFormat)img.Format} (backend {(g is ICompressedUpload { Dxt: true } ? "refused DXT" : "without DXT")})", t);
        return h;
    }

    /// <summary>Kit #6 (c): the SDF atlas's distance channel decoded strip by strip -- the pack read into a 256-KiB native buffer, decoded
    /// into a w x h native buffer -- and uploaded from there (the same GL call and bytes as the array path: SHEPHERD_K6_TEX_NATIVE=check
    /// decodes both and compares). Kit #5 read the whole compressed image into a managed array and decoded into another (4 + 4 MiB for a
    /// 2048 x 2048 atlas, both large objects in the GC heap, at the Main Menu's first draw: where its load's arena peak is set). The native
    /// buffers come from malloc (newlib on the Vita) and are freed before this returns. Zero = not done (the caller takes the array path).</summary>
    static unsafe IntPtr UploadAlpha8Native(NTexture t, PackImage img, IAlphaUploadPtr ap)
    {
        int w = img.Width, h = img.Height, bb = DxtBlocks.BlockBytes(img.Format), bw = (w + 3) / 4, bh = (h + 3) / 4, rowBytes = bw * bb;
        var fmt = (Shepherd.Assets.TextureFormat)img.Format;
        if (w <= 0 || h <= 0 || (long)bh * rowBytes != img.Length) return IntPtr.Zero;
        int strip = Math.Max(1, Math.Min(bh, (256 * 1024) / rowBytes));
        IntPtr a8 = IntPtr.Zero, sb = IntPtr.Zero;
        try
        {
            a8 = Marshal.AllocHGlobal(w * h); sb = Marshal.AllocHGlobal(strip * rowBytes);
            var dst = new Span<byte>((void*)a8, w * h);
            for (int by = 0; by < bh; by += strip)
            {
                int n = Math.Min(strip, bh - by); var src = new Span<byte>((void*)sb, n * rowBytes);
                img.ReadRange(by * rowBytes, src);
                if (!TextureDecoder.DecodeAlpha8Rows(src, w, h, fmt, by, n, dst, flip: !img.TopDown)) return IntPtr.Zero;   // (DecodeAlpha8's flip, cancelled by a top-down image's own)
            }
#if !SHEPHERD_EBOOT
            if (Runtime.K6Engine.TexNativeCheck)
            {   // the array path's bytes, texel for texel (check runs only, Mac / tools builds: it allocates them)
                var old = TextureDecoder.DecodeAlpha8(img.Read(), w, h, fmt); if (img.TopDown) TextureDecoder.FlipRowsAlpha8(old, w, h);
                int bad = 0; for (int i = 0; i < old.Length; i++) if (old[i] != dst[i]) bad++;
                Log.Info($"[k6] SDF native decode check '{t.Name}' {img}: {bad} of {old.Length} texels differ from the array path");
            }
#endif
            return ap.CreateSdfAlpha8Texture(w, h, a8, t.FilterMode != 0);
        }
        finally { if (a8 != IntPtr.Zero) Marshal.FreeHGlobal(a8); if (sb != IntPtr.Zero) Marshal.FreeHGlobal(sb); }
    }

    /// <summary>Kit #6 (c): a pack DXT image read into native memory (malloc: newlib on the Vita) and uploaded from there (the same GL call
    /// and bytes as the array path), freed before this returns. Kit #5 read it into a managed array: a 2048 x 2048 DXT5 is a 4-MiB large
    /// object, and the Main Menu's first draw uploads ~11 MiB of them in one frame. Zero = not done (the caller takes the array path).</summary>
    static IntPtr UploadDxtNative(PackImage img, ICompressedUploadPtr cp, bool linear)
    {
        IntPtr b = IntPtr.Zero;
        try
        {
            b = Marshal.AllocHGlobal(img.Length);
            unsafe { img.ReadRange(0, new Span<byte>((void*)b, img.Length)); }
            return cp.CreateCompressedTexture(img.Width, img.Height, img.Format, b, img.Length, linear);
        }
        finally { if (b != IntPtr.Zero) Marshal.FreeHGlobal(b); }
    }

    static void Count(string key, NTexture t)
    {
        var c = GlTextures.DxtCensus.TryGetValue(key, out var v) ? v : default; GlTextures.DxtCensus[key] = (c.n + 1, c.px + (long)t.Src.Image.Width * t.Src.Image.Height);
    }

    static byte[] Rgba(PackImage img)
    {
        var px = TextureDecoder.Decode(img.Read(), img.Width, img.Height, (Shepherd.Assets.TextureFormat)img.Format);
        if (img.TopDown) TextureDecoder.FlipRows(px, img.Width, img.Height);   // Decode assumes Unity's bottom-up rows
        return px;
    }

    public static void Report()
    {
        if (!On) return;
        Runtime.K10Io.End();   // kit #10 io: pending cache writes, the read log, the lines
        Log.Info($"[pack] {Reader.Stats()}");
        Log.Info($"[pack] texture uploads from the pack: {GpuUploads} DXT + {GpuRgbaUploads} RGBA8 + {GpuAlphaUploads} SDF alpha8, {GpuUploadBytes / 1048576.0:F1} MiB; SDF draws of transcoded textures: {SdfOfTranscoded}; MISSING lines: {Misses}; TextAsset reads from the pack: {PackBytes.Reads} ({PackBytes.ReadBytes / 1048576.0:F1} MiB)");
    }
}
