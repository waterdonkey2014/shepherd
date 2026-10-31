using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Shepherd.Assets;
using C = Shepherd.Assets.VitaPack2Format;

namespace Shepherd.VitaPack;

/// <summary>
/// Device kit #10, track io: <c>--repack</c> turns a version-1 pack (shepherd.pack) into the chunked version 2 (shepherd.pk2,
/// Shepherd.Assets/VitaPack2.cs) WITHOUT reading the game data: the logical bytes are the version-1 file's, so the result is exact by
/// construction and checked byte for byte (every chunk decoded again with the builder's decoders, the managed LZ4 decoder and -- in
/// --verify -- the engine's own PackReader over the new file against the old one). What it adds:
///   * per-chunk codec choice (stored / LZ4 HC / zstd): the estimated device time of a chunk = max(card read, decode) (the decode-ahead
///     worker decodes while the card reads the next chunk), card rate and decode rates from --card-mibs / --rate-lz4 / --rate-zstd
///     (MiB/s of decoded output; defaults: kit #7's measured card 11.6 MiB/s, ESTIMATED Cortex-A9 decoders at 444 MHz) -- so a stronger codec
///     is taken only where the decoder keeps ahead of the card (decode rate > card rate x ratio) or still wins overall;
///   * visit-order layout: --order &lt;read log&gt; (the engine's SHEPHERD_K10_READLOG: every logical pack read of a recorded run, in order):
///     chunks stored in the order the run first touched them, the rest after in pack order;
///   * block-aligned chunks: every stored chunk starts at a multiple of --align (default 512 B: kit #7's device test found 512 B shifts
///     already at full speed; --align 32768 = the card's cluster);
///   * deduplication: chunks with identical bytes (byte-identical texture images split into the same tiles) stored once;
///   * integrity hashes (XXH32 of the stored and of the decoded bytes) for 'Verify files' (Shepherd.Assets PackVerify).
///   dotnet bin/VitaPack.dll --repack &lt;v1 pack dir or file&gt; --out &lt;runtime/vitapack/name&gt; [--order &lt;log&gt; ...] [--chunk-kib 64]
///          [--align 512] [--codec lz4|auto|zstd|store (default lz4: the device builds without SHEPHERD_K10_ZSTD)] [--zstd-level 19] [--lz4-level 12] [--card-mibs 11.6] [--rate-lz4 180]
///          [--rate-zstd 45] [--no-dedup] [-j N] [--analyze]
/// </summary>
static class Repack
{
    /// <summary>--stress &lt;v1 pack&gt; &lt;v2 pack&gt; [seconds]: the version-2 reader under every page / worker / prefetch configuration against random
    /// access patterns (jumps, re-reads, straddles, big reads, images, bundle prefetches) -- every byte compared with the version-1 reader's.</summary>
    public static int Stress(string v1Path, string v2Path, int seconds)
    {
        NativeCodecs.Init(); PackReader.ChunkDecoder = new MacDecoder();
        var cfgs = new (int pages, int ahead, bool worker, bool prefetch, int waitMs)[] { (16, 11, true, true, 25), (4, 1, true, true, 1), (5, 2, true, false, 0), (16, 11, false, false, 25), (8, 5, true, true, 1), (64, 61, true, true, 25) };
        using var a = PackReader.Open(v1Path);
        var names = a.FileNames.ToList(); int bad = 0; long ops = 0;
        var sw0 = Stopwatch.StartNew();
        foreach (var cfg in cfgs)
        {
            PackReader.V2Pages = cfg.pages; PackReader.V2AheadPages = cfg.ahead; PackReader.V2DecodeAhead = cfg.worker; PackReader.V2Prefetch = cfg.prefetch; PackReader.AheadWaitBoundMs = cfg.waitMs;
            using var b = PackReader.Open(v2Path);
            var rng = new Random(cfg.pages * 31 + cfg.waitMs); var sw = Stopwatch.StartNew(); long n = 0;
            var ba = new byte[3 << 20]; var bb = new byte[3 << 20];
            long end = a.PayloadOffset + a.PayloadSize;
            // the K8 preload's reads (PreloadRead: its own thread, its own buffers and decoder context) side by side with the reading thread,
            // every piece compared with the version-1 reader's PreloadRead (kit #10 io final: vlx's preload stays off without VLX_VRAM_FREE_KIB)
            long preOps = 0; int preBad = 0, stopPre = 0;
            var pre = new Thread(() =>
            {
                var rp = new Random(cfg.pages * 7 + 1); var pa = new byte[1 << 20]; var pb = new byte[1 << 20];
                while (Volatile.Read(ref stopPre) == 0)
                {
                    int len = rp.Next(3) switch { 0 => rp.Next(1, 4096), 1 => rp.Next(4096, 140000), _ => rp.Next(140000, 1 << 20) };
                    long off = a.PayloadOffset + (long)(rp.NextDouble() * (a.PayloadSize - len));
                    if (rp.Next(4) == 0) off = a.PayloadOffset + a.PayloadSize - len;   // the payload's last bytes (the last chunk is short)
                    bool oka = a.PreloadRead(off, pa.AsSpan(0, len)), okb = b.PreloadRead(off, pb.AsSpan(0, len));
                    Interlocked.Increment(ref preOps);
                    if (oka != okb || (oka && !pa.AsSpan(0, len).SequenceEqual(pb.AsSpan(0, len))))
                        if (Interlocked.Increment(ref preBad) <= 5) Console.WriteLine($"stress {cfg}: preload [{off}, +{len}) differs (ok {oka} / {okb})");
                }
                // past the payload's end the chunked reader refuses (false: the preload stops), never a partial read (the version-1 file has
                // its TOC there, so its reader returns TOC bytes -- the preload's maps only name record ranges inside the payload)
                var pz = new byte[16]; if (b.PreloadRead(end - 8, pz)) { Interlocked.Increment(ref preBad); Console.WriteLine($"stress {cfg}: preload past the payload's end not refused"); }
            }) { IsBackground = true, Name = "stress-preload" };
            pre.Start();
            while (sw.Elapsed.TotalSeconds < seconds / (double)cfgs.Length)
            {
                int kind = rng.Next(10);
                if (kind < 5)
                {   // a logical piece somewhere (or right after the last one: sequential runs)
                    int len = rng.Next(3) switch { 0 => rng.Next(1, 300), 1 => rng.Next(300, 70000), _ => rng.Next(70000, 3 << 20) };
                    long off = a.PayloadOffset + (long)(rng.NextDouble() * (a.PayloadSize - len));
                    for (int rep = 0; rep < 1 + rng.Next(4); rep++)
                    {
                        if (off + len > end) break;
                        a.ReadLogicalForTest(off, ba.AsSpan(0, len)); b.ReadLogicalForTest(off, bb.AsSpan(0, len)); n++;
                        if (!ba.AsSpan(0, len).SequenceEqual(bb.AsSpan(0, len))) { if (bad++ < 5) Console.WriteLine($"stress {cfg}: [{off}, +{len}) differs"); }
                        off += len;
                    }
                }
                else
                {   // a file's objects (a bundle-ish walk), with the prefetch of that file first
                    var name = names[rng.Next(names.Count)]; var fa = a.GetFile(name); var fb = b.GetFile(name);
                    if (rng.Next(2) == 0) b.Prefetch(new[] { fb });
                    int start = rng.Next(Math.Max(1, fa.Objects.Count)), cnt = Math.Min(fa.Objects.Count - start, 1 + rng.Next(200));
                    for (int i = start; i < start + cnt; i++)
                    {
                        var oa = fa.Objects[i]; var ob = fb.Objects[i]; n++;
                        if (PackReader.HasTree(oa))
                        {
                            var ta = a.ReadTree(fa, oa); var tb = b.ReadTree(fb, ob);
                            if (!SameTree(ta, tb)) { if (bad++ < 5) Console.WriteLine($"stress {cfg}: tree {name} #{oa.PathId} differs"); }
                            if (ta.TryGet(PackReader.ImageKey, out var ia) && rng.Next(3) == 0)
                            {
                                var xa = ((PackImage)ia).Read(); var xb = ((PackImage)tb[PackReader.ImageKey]).Read();
                                if (!xa.AsSpan().SequenceEqual(xb)) { if (bad++ < 5) Console.WriteLine($"stress {cfg}: image {name} #{oa.PathId} differs"); }
                            }
                        }
                        if (a.RawLength(oa) > 0 && !a.ReadRaw(fa, oa).AsSpan().SequenceEqual(b.ReadRaw(fb, ob))) { if (bad++ < 5) Console.WriteLine($"stress {cfg}: raw {name} #{oa.PathId} differs"); }
                    }
                }
            }
            Volatile.Write(ref stopPre, 1); pre.Join(); bad += preBad;
            ops += n + preOps;
            Console.WriteLine($"stress pages {cfg.pages} ahead {cfg.ahead} worker {cfg.worker} prefetch {cfg.prefetch} wait {cfg.waitMs} ms: {n} operations + {preOps} preload reads on their own thread, {(bad == 0 ? "identical" : bad + " differences so far")}; " + b.Stats().Split("; chunked")[1].Split("; lanes")[0].Trim());
        }
        Console.WriteLine($"stress: {ops} operations in {sw0.Elapsed.TotalSeconds:F0} s over {cfgs.Length} configurations -- {(bad == 0 ? "ALL IDENTICAL" : bad + " DIFFERENCES")}");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>--verify &lt;pack file&gt; [deep]: Shepherd.Assets PackVerify (the Verify-files core) on the Mac; exit 0 = OK.</summary>
    public static int VerifyCli(string path, bool deep)
    {
        NativeCodecs.Init();
        var r = PackVerify.Run(path, new MacDecoder(), deep, null);
        Console.WriteLine($"verify {path}{(deep ? " (deep)" : "")}: {r.Summary()}");
        return r.Ok ? 0 : 1;
    }

    /// <summary>Kit #10 io review: <see cref="VitaPack2Format.PayloadHashProperty"/>'s value for a version-1 payload: "xxh32-1m:" + XXH32 with
    /// seeds 0 and 1 (16 hex) over the list of the payload's per-1-MiB XXH32s (little-endian u32 each).</summary>
    public static string PayloadHash(ReadOnlySpan<byte> pay)
    {
        int nb = (int)((pay.Length + (1L << 20) - 1) >> 20); var list = new byte[nb * 4];
        for (int i = 0; i < nb; i++) { int o = i << 20; BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(i * 4), C.Xxh32(pay.Slice(o, Math.Min(1 << 20, pay.Length - o)))); }
        return FoldHash(list);
    }
    /// <summary><see cref="PayloadHash(ReadOnlySpan{byte})"/> of [off, off + size) of a file (PackWriter: its .tmp file, read back).</summary>
    public static string PayloadHashOfFile(string path, long off, long size)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int nb = (int)((size + (1L << 20) - 1) >> 20); var list = new byte[nb * 4]; var b = new byte[1 << 20];
        for (int i = 0; i < nb; i++)
        {
            long o = (long)i << 20; int m = (int)Math.Min(1 << 20, size - o), done = 0;
            while (done < m) { int k = RandomAccess.Read(h, b.AsSpan(done, m - done), off + o + done); if (k <= 0) throw new EndOfStreamException($"{path}: payload hash read past the end"); done += k; }
            BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(i * 4), C.Xxh32(b.AsSpan(0, m)));
        }
        return FoldHash(list);
    }
    static string FoldHash(byte[] list) => "xxh32-1m:" + C.Xxh32(list, 0).ToString("x8") + C.Xxh32(list, 1).ToString("x8");

    /// <summary>--made-from &lt;shepherd.pk2 or dir&gt; &lt;shepherd.pack or dir&gt;: exit 0 only when the chunked pack was made from exactly that
    /// version-1 file -- the header check the engine runs at open, the version-1 TOC's checksum from its bytes, and EVERY chunk's decoded hash
    /// against the version-1 file's bytes at its logical range (stage-owner.sh STAGE_PK2; the engine at open can only sample a pack baked before
    /// kit #10). Reads only.</summary>
    public static int MadeFromCli(string v2, string v1)
    {
        if (Directory.Exists(v2)) v2 = Path.Combine(v2, C.FileName);
        if (Directory.Exists(v1)) v1 = Path.Combine(v1, VitaPackFormat.PackFileName);
        try
        {
            C.CheckMadeFrom(v2, v1);
            var h = new byte[C.HeaderSize]; byte[] t;
            using (var f2 = File.OpenRead(v2))
            {
                f2.ReadExactly(h);
                if (VitaPackFormat.Fnv1a(h.AsSpan(0, 120)) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(120))) throw new InvalidDataException($"{v2}: header checksum mismatch");
                t = new byte[BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(80))]; f2.Position = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(72)); f2.ReadExactly(t);
                if (VitaPackFormat.Fnv1a(t) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(84))) throw new InvalidDataException($"{v2}: chunk table checksum mismatch");
            }
            int n = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(64)); uint tocHash = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12));
            long payOff = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(32)), paySize = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(40));
            long tocOff = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(16)), tocSize = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(24));
            using var f1 = File.OpenRead(v1);
            var toc = new byte[tocSize]; f1.Position = tocOff; f1.ReadExactly(toc);
            if (VitaPackFormat.Fnv1a(toc) != tocHash) throw new InvalidDataException($"{v1}: its TOC's bytes do not have the checksum its header (and the pk2) records");
            var b = new byte[1 << 20]; long expect = 0;
            for (int i = 0; i < n; i++)
            {
                var c = C.Chunk.Read(t.AsSpan(i * C.EntrySize));
                if (c.Logical != expect || c.LogicalLen <= 0 || c.LogicalLen > b.Length) throw new InvalidDataException($"{v2}: chunk {i} does not continue the payload");
                f1.Position = payOff + c.Logical; f1.ReadExactly(b, 0, c.LogicalLen);
                if (C.Xxh32(b.AsSpan(0, c.LogicalLen)) != c.LogicalHash) throw new InvalidDataException($"chunk {i} (logical {c.Logical}+{c.LogicalLen}) differs from {v1}'s bytes");
                expect += c.LogicalLen;
            }
            if (expect != paySize) throw new InvalidDataException($"{v2}: the chunks cover {expect} of {paySize} payload bytes");
            Console.WriteLine($"made-from: YES -- {v2} was made from {v1}: header, TOC (FNV {tocHash:x8}) and all {n} chunks ({paySize:N0} B of payload) agree");
            return 0;
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException)
        {
            Console.WriteLine($"made-from: NO -- {e.Message}");
            return 1;
        }
    }

    /// <summary>--open &lt;pack dir&gt;: the folder opened as the engine opens it with kit #10 on (shepherd.pk2 preferred, the made-from checks, the
    /// truncation check): exit 0 = the chunked pack, 3 = the version-1 file (no pk2), 1 = refused (the engine would read the version-1 file).</summary>
    public static int OpenCli(string dir)
    {
        NativeCodecs.Init(); PackReader.ChunkDecoder = new MacDecoder(); PackReader.PreferV2 = true;
        try
        {
            using var r = PackReader.Open(dir);
            Console.WriteLine($"open: {Path.GetFileName(r.Path)}" + (r.IsChunked ? $" (chunked, {r.ChunkCount} chunks; {r.MadeFrom ?? "no version-1 file beside it"})" : ""));
            return r.IsChunked ? 0 : 3;
        }
        catch (Exception e) { Console.WriteLine($"open: REFUSED -- {e.GetBaseException().Message}"); return 1; }
    }

    /// <summary>Test (kit #10 io review): --read-all &lt;pack dir&gt; [--hash 0|1|2] [--runs 0|1] [--jump] [--piece N] [--compare]: the folder opened
    /// as the engine opens it, then the whole payload read in order through the reader in N-byte pieces (default 4096: the records' sizes) --
    /// --jump first reads 16 bytes at 90 % of the payload (a chunk stored near the tail, as a read outside the recorded visit order); --compare checks
    /// every piece against the version-1 file. Prints the reader's decode-ahead numbers; exit 1 on an exception (named) or a difference.</summary>
    public static int ReadAllCli(string[] args)
    {
        string dir = args[1]; int piece = 4096; bool jump = false, compare = false;
        NativeCodecs.Init(); PackReader.ChunkDecoder = new MacDecoder(); PackReader.PreferV2 = true;
        for (int i = 2; i < args.Length; i++)
            switch (args[i])
            {
                case "--hash": PackReader.V2HashCheck = int.Parse(args[++i]); break;
                case "--runs": PackReader.V2AheadRuns = args[++i] != "0"; break;
                case "--need": PackReader.V2AheadRunNeed = int.Parse(args[++i]); break;
                case "--jump": jump = true; break;
                case "--compare": compare = true; break;
                case "--piece": piece = int.Parse(args[++i]); break;
                default: Console.Error.WriteLine($"read-all: unknown argument {args[i]}"); return 2;
            }
        try
        {
            using var r = PackReader.Open(dir);
            using var v1 = compare ? File.OpenHandle(Path.Combine(dir, VitaPackFormat.PackFileName)) : null;
            long end = r.PayloadOffset + r.PayloadSize; var b = new byte[piece]; var c = new byte[piece]; long n = 0, bad = 0;
            var sw = Stopwatch.StartNew();
            if (jump) r.ReadLogicalForTest(r.PayloadOffset + r.PayloadSize * 9 / 10, b.AsSpan(0, 16));   // (a chunk near the tail, not the last one)
            for (long off = r.PayloadOffset; off < end; off += piece)
            {
                int len = (int)Math.Min(piece, end - off);
                r.ReadLogicalForTest(off, b.AsSpan(0, len)); n++;
                if (v1 != null) { RandomAccess.Read(v1, c.AsSpan(0, len), off); if (!b.AsSpan(0, len).SequenceEqual(c.AsSpan(0, len))) bad++; }
            }
            Console.WriteLine($"read-all: {n} pieces of {piece} B in {sw.Elapsed.TotalSeconds:F1} s{(compare ? $", {bad} differ from the version-1 file" : "")}; hash mode {PackReader.V2HashCheck}, runs {PackReader.V2AheadRuns}, jump {jump}: " +
                $"ahead decoded {r.V2AheadDecoded}, used {r.V2AheadUsed}, reader card reads {r.V2CardReads}, new runs followed {r.V2AheadReanchors}, hash checks {r.V2HashChecked} ok / {r.V2HashBad} bad");
            return bad == 0 ? 0 : 1;
        }
        catch (Exception e) { Console.WriteLine($"read-all: EXCEPTION -- {e.GetBaseException().Message}"); return 1; }
    }

    /// <summary>Test (kit #10 io review): --replay &lt;pack dir&gt; &lt;read log&gt; [--runs 0|1] [--hash 0|1|2]: a recorded run's logical reads
    /// (SHEPHERD_K10_READLOG) replayed through the reader in order (no card latency: the Mac's page cache) -- the decode-ahead numbers on a real
    /// route, e.g. the visit order's own route with the worker following new runs or not.</summary>
    public static int ReplayCli(string[] args)
    {
        string dir = args[1], log = args[2];
        NativeCodecs.Init(); PackReader.ChunkDecoder = new MacDecoder(); PackReader.PreferV2 = true;
        for (int i = 3; i < args.Length; i++)
            switch (args[i])
            {
                case "--runs": PackReader.V2AheadRuns = args[++i] != "0"; break;
                case "--need": PackReader.V2AheadRunNeed = int.Parse(args[++i]); break;
                case "--hash": PackReader.V2HashCheck = int.Parse(args[++i]); break;
                default: Console.Error.WriteLine($"replay: unknown argument {args[i]}"); return 2;
            }
        try
        {
            using var r = PackReader.Open(dir);
            long end = r.PayloadOffset + r.PayloadSize; var b = new byte[4 << 20]; long n = 0;
            foreach (var line in File.ReadLines(log))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('\t'); if (f.Length < 2 || !long.TryParse(f[0], out var off) || !int.TryParse(f[1], out var len) || len <= 0) continue;
                if (off < r.PayloadOffset || off + len > end) continue;
                if (len > b.Length) b = new byte[len];
                r.ReadLogicalForTest(off, b.AsSpan(0, len)); n++;
            }
            Console.WriteLine($"replay: {n} reads; runs {PackReader.V2AheadRuns}: ahead decoded {r.V2AheadDecoded}, used {r.V2AheadUsed}, reader card reads {r.V2CardReads}, worker card reads {r.V2WorkerReads}, new runs followed {r.V2AheadReanchors}");
            return 0;
        }
        catch (Exception e) { Console.WriteLine($"replay: EXCEPTION -- {e.GetBaseException().Message}"); return 1; }
    }

    public static int Run(string[] args)
    {
        string src = null, outDir = null; var orders = new List<string>(); int chunkKiB = 64, align = 512, zlevel = 19, llevel = 12, jobs = Environment.ProcessorCount;
        string codec = "lz4"; double card = 11.6, rLz4 = 180, rZstd = 45; bool dedup = true, analyze = false, verify = true; int dedupMin = 16384;
        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--repack": src = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--order": orders.Add(args[++i]); break;
                case "--chunk-kib": chunkKiB = int.Parse(args[++i]); break;
                case "--align": align = int.Parse(args[++i]); break;
                case "--codec": codec = args[++i]; break;
                case "--zstd-level": zlevel = int.Parse(args[++i]); break;
                case "--lz4-level": llevel = int.Parse(args[++i]); break;
                case "--card-mibs": card = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--rate-lz4": rLz4 = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--rate-zstd": rZstd = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--no-dedup": dedup = false; break;
                case "--dedup-min": dedupMin = int.Parse(args[++i]); break;
                case "--no-verify": verify = false; break;
                case "-j": jobs = int.Parse(args[++i]); break;
                case "--analyze": analyze = true; break;
                default: Console.Error.WriteLine($"repack: unknown argument {args[i]}"); return 2;
            }
        if (src == null || (outDir == null && !analyze)) { Console.Error.WriteLine("usage: VitaPack --repack <v1 pack dir|file> --out <runtime/vitapack/name> [--order <readlog>] [--chunk-kib 64] [--align 512] [--codec auto|lz4|zstd|store] [--analyze]"); return 2; }
        if (align < 1 || (align & (align - 1)) != 0) { Console.Error.WriteLine("--align must be a power of two"); return 2; }
        if (Directory.Exists(src)) src = Path.Combine(src, VitaPackFormat.PackFileName);
        NativeCodecs.Init();
        var sw = Stopwatch.StartNew();
        var report = new StringBuilder();
        void Say(string s) { Console.WriteLine(s); report.AppendLine(s); }
        Say($"repack {src} ({new FileInfo(src).Length:N0} B) -- {NativeCodecs.Versions()}; tile {chunkKiB} KiB, align {align} B, codec {codec}, zstd -{zlevel}, lz4hc -{llevel}, dedup {(dedup ? "on" : "off")}; device model: card {card} MiB/s, decoders lz4 {rLz4} / zstd {rZstd} MiB/s (ESTIMATE)");
        var v1 = V1.Open(src);
        Say($"version-1 pack: TOC {v1.TocSize:N0} B (FNV {v1.TocHash:x8}), payload {v1.PaySize:N0} B at {v1.PayOff}: records {v1.RecordsEnd:N0} B, {v1.Images.Count} images {v1.PaySize - v1.RecordsEnd:N0} B");
        var plan = Plan(v1, chunkKiB << 10);
        Say($"chunks: {plan.Count} ({plan.Count(c => (c.Flags & C.ChunkImage) == 0)} record tiles, {plan.Count(c => (c.Flags & C.ChunkImage) != 0)} image tiles)");

        // the logical payload, once (the v1 file is read whole: ~180 MiB on the Mac)
        var pay = new byte[v1.PaySize];
        using (var fs = File.OpenRead(src)) { fs.Position = v1.PayOff; fs.ReadExactly(pay); }
        // review: a pack baked from kit #10 on carries its payload's hash in its TOC (VitaPack2Format.PayloadHashProperty): checked here, so a
        // damaged copy is never repacked; the engine's made-from check then binds the pk2 to this payload through the TOC's checksum
        if (v1.Props.TryGetValue(C.PayloadHashProperty, out var ph))
        {
            var got = PayloadHash(pay);
            if (got != ph) { Say($"version-1 pack: its payload does not match its own {C.PayloadHashProperty} ({got}, the TOC says {ph}): a damaged copy -- not repacked"); return 1; }
            Say($"version-1 pack: {C.PayloadHashProperty} {ph} (its TOC property) matches its payload: a shepherd.pk2 of it is bound to these bytes");
        }
        else Say($"version-1 pack: no {C.PayloadHashProperty} in its TOC (baked before kit #10): the engine compares {PackReader.V2MadeFromSamples} sampled chunks with it at open; stage with tools/VitaPack --made-from (every chunk)");

        // dedup by content
        int copies = 0; long copyBytes = 0;
        var firstOf = new Dictionary<string, int>();
        for (int i = 0; i < plan.Count; i++)
        {
            if (!dedup) break;
            var key = Convert.ToHexString(SHA256.HashData(pay.AsSpan((int)plan[i].Logical, plan[i].LogicalLen))) + ":" + plan[i].LogicalLen.ToString();
            if (firstOf.TryGetValue(key, out var j)) { plan[i].CopyOf = j; copies++; copyBytes += plan[i].LogicalLen; }
            else firstOf[key] = i;
        }
        Say($"dedup: {copies} chunks ({copyBytes / 1048576.0:F2} MiB) are byte-identical copies of earlier chunks (stored once)");

        // encode every distinct chunk with every candidate
        var t0 = Stopwatch.StartNew();
        Parallel.For(0, plan.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            var p = plan[i]; if (p.CopyOf >= 0) return;
            var s = pay.AsSpan((int)p.Logical, p.LogicalLen);
            if (codec is "auto" or "lz4" || analyze) p.Lz4 = NativeCodecs.Lz4Hc(s, llevel);
            if (codec is "auto" or "zstd" || analyze) p.Zstd = NativeCodecs.Zstd(s, zlevel);
            if (analyze) { p.Lz4Fast = NativeCodecs.Lz4Hc(s, 1); p.Zstd3 = NativeCodecs.Zstd(s, 3); p.Zstd9 = NativeCodecs.Zstd(s, 9); }
        });
        Say($"encoded in {t0.Elapsed.TotalSeconds:F1} s");

        // choose (the device time model) -- per chunk: stored / lz4 / zstd
        double cardB = card * 1048576.0, l4B = rLz4 * 1048576.0, zB = rZstd * 1048576.0;
        int ruleZ = 0, nz = 0, n4 = 0, ns = 0;
        foreach (var p in plan)
        {
            if (p.CopyOf >= 0) continue;
            double tS = p.LogicalLen / cardB;
            double t4 = p.Lz4 != null ? Math.Max(p.Lz4.Length / cardB, p.LogicalLen / l4B) : double.MaxValue;
            double tZ = p.Zstd != null ? Math.Max(p.Zstd.Length / cardB, p.LogicalLen / zB) : double.MaxValue;
            if (p.Zstd != null && zB > cardB * ((double)p.LogicalLen / p.Zstd.Length)) ruleZ++;
            // stored when no codec saves time; zstd only when it beats LZ4 by more than 1 % (LZ4 decodes ~4x faster when the reading thread
            // itself has to decode: a miss the worker did not cover)
            if (codec == "store" || (tS <= t4 && tS <= tZ)) { p.Codec = C.CodecStored; p.Stored = pay.AsSpan((int)p.Logical, p.LogicalLen).ToArray(); ns++; }
            else if (tZ < t4 * 0.99) { p.Codec = C.CodecZstd; p.Stored = p.Zstd; nz++; }
            else { p.Codec = C.CodecLz4; p.Stored = p.Lz4; n4++; }
            if (p.Codec != C.CodecStored && p.Stored.Length >= p.LogicalLen) { p.Codec = C.CodecStored; p.Stored = pay.AsSpan((int)p.Logical, p.LogicalLen).ToArray(); }
        }
        foreach (var p in plan) if (p.CopyOf >= 0) { var o = plan[p.CopyOf]; p.Codec = o.Codec; p.Stored = o.Stored; }
        // a copy shares its original's stored bytes only when they are big enough to be worth a card request of their own (--dedup-min, stored
        // bytes): a small copy (a transparent tile, ~1 KiB) is stored again in place, so the run of chunks a big read takes stays one request
        int undone = 0; long undoneBytes = 0;
        foreach (var p in plan) if (p.CopyOf >= 0 && p.Stored.Length < dedupMin) { p.CopyOf = -1; undone++; undoneBytes += p.Stored.Length; }
        if (dedup) Say($"dedup: {copies - undone} copies kept shared (stored >= {dedupMin} B); {undone} small copies ({undoneBytes / 1024.0:F1} KiB stored) stored again in place (runs stay whole)");
        Say($"codec choice ({codec}): stored {plan.Count(p => p.CopyOf < 0 && p.Codec == C.CodecStored)}, lz4 {plan.Count(p => p.CopyOf < 0 && p.Codec == C.CodecLz4)}, zstd {plan.Count(p => p.CopyOf < 0 && p.Codec == C.CodecZstd)} of the {plan.Count(p => p.CopyOf < 0)} distinct chunks; " +
            $"the owner's rule (zstd decode rate > card rate x zstd ratio) holds for {ruleZ} of them");

        if (analyze) Analyze(plan, pay, v1, Say, cardB, l4B, zB);

        // read-back: every distinct chunk decoded with the library decoders AND (lz4) the managed decoder
        var bad = 0;
        Parallel.For(0, plan.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            var p = plan[i]; if (p.CopyOf >= 0) return;
            var want = pay.AsSpan((int)p.Logical, p.LogicalLen);
            var got = new byte[p.LogicalLen]; int n;
            switch (p.Codec)
            {
                case C.CodecStored: n = p.Stored.Length; p.Stored.CopyTo(got, 0); break;
                case C.CodecLz4:
                    n = NativeCodecs.Lz4Decode(p.Stored, got);
                    var got2 = new byte[p.LogicalLen]; int n2 = Shepherd.Assets.Compression.Lz4.Decode(p.Stored, got2);
                    if (n2 != p.LogicalLen || !got2.AsSpan().SequenceEqual(want)) n = -1;
                    break;
                default: n = NativeCodecs.ZstdDecode(p.Stored, got); break;
            }
            if (n != p.LogicalLen || !got.AsSpan().SequenceEqual(want)) Interlocked.Increment(ref bad);
            p.StoredHash = C.Xxh32(p.Stored); p.LogicalHash = C.Xxh32(want);
        });
        foreach (var p in plan) if (p.CopyOf >= 0) { var o = plan[p.CopyOf]; p.StoredHash = o.StoredHash; p.LogicalHash = o.LogicalHash; }
        Say(bad == 0 ? $"read-back: every chunk decodes to the version-1 bytes (library decoders; LZ4 chunks also the managed decoder)" : $"read-back: {bad} chunks FAILED");
        if (bad != 0) return 1;
        if (analyze && outDir == null) return 0;

        // the visit order
        var order = VisitOrder(plan, v1, orders, Say);

        // the TOC, stored with the better of lz4 / zstd (read once at open, on the reading thread)
        var tocZ = NativeCodecs.Zstd(v1.Toc, zlevel); var tocL = NativeCodecs.Lz4Hc(v1.Toc, llevel);
        // (the TOC follows --codec: an LZ4-only pack must open on a device build without zstd)
        byte tocCodec = codec == "store" ? C.CodecStored : codec == "lz4" ? C.CodecLz4 : codec == "zstd" ? C.CodecZstd : tocZ.Length < tocL.Length * 0.9 ? C.CodecZstd : C.CodecLz4;
        var tocStored = tocCodec == C.CodecZstd ? tocZ : tocCodec == C.CodecLz4 ? tocL : v1.Toc;
        { var chk = new byte[v1.Toc.Length]; int n = tocCodec == C.CodecZstd ? NativeCodecs.ZstdDecode(tocStored, chk) : tocCodec == C.CodecLz4 ? NativeCodecs.Lz4Decode(tocStored, chk) : CopyN(tocStored, chk); if (n != chk.Length || !chk.AsSpan().SequenceEqual(v1.Toc)) { Say("TOC read-back FAILED"); return 1; } }

        // layout
        long Align(long x) => (x + align - 1) & ~(long)(align - 1);
        long tableAt = C.HeaderSize, tableBytes = (long)plan.Count * C.EntrySize;
        long tocAt = Align(tableAt + tableBytes); long at = Align(tocAt + tocStored.Length);
        long padBytes = 0;
        foreach (var i in order)
        {
            var p = plan[i];
            if (p.CopyOf >= 0) continue;
            p.StoredAt = at; long next = Align(at + p.Stored.Length); padBytes += next - (at + p.Stored.Length); at = next;
        }
        foreach (var p in plan) if (p.CopyOf >= 0) p.StoredAt = plan[p.CopyOf].StoredAt;
        long fileSize = at;

        // write
        var table = new byte[tableBytes];
        for (int i = 0; i < plan.Count; i++)
        {
            var p = plan[i];
            new C.Chunk { Logical = p.Logical, LogicalLen = p.LogicalLen, StoredLen = p.Stored.Length, Stored = p.StoredAt, Codec = p.Codec, Flags = (ushort)(p.Flags | (p.CopyOf >= 0 ? C.ChunkCopy : 0)), StoredHash = p.StoredHash, LogicalHash = p.LogicalHash }
                .Write(table.AsSpan(i * C.EntrySize, C.EntrySize));
        }
        var h = new byte[C.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(h, VitaPackFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), C.Version); BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), C.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), v1.Flags); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), v1.TocHash);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(16), v1.TocOff); BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), v1.TocSize);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), v1.PayOff); BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), v1.PaySize);
        v1.Header.AsSpan(48, 16).CopyTo(h.AsSpan(48));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(64), plan.Count); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(68), C.EntrySize);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), tableAt); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(80), (int)tableBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), VitaPackFormat.Fnv1a(table));
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(88), tocAt); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(96), tocStored.Length); h[100] = tocCodec;
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(104), align); BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(108), chunkKiB << 10);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(112), new FileInfo(src).Length);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(120), VitaPackFormat.Fnv1a(h.AsSpan(0, 120)));
        outDir = Path.GetFullPath(outDir);
        if (!outDir.Replace('\\', '/').Contains("/runtime/vitapack/")) { Console.Error.WriteLine($"refusing to write derived game data outside runtime/vitapack/: {outDir}"); return 2; }
        Directory.CreateDirectory(outDir);
        var dst = Path.Combine(outDir, C.FileName);
        using (var fs = new FileStream(dst + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            fs.Write(h); fs.Write(table);
            fs.Position = tocAt; fs.Write(tocStored);
            foreach (var p in plan) if (p.CopyOf < 0) { fs.Position = p.StoredAt; fs.Write(p.Stored); }
            fs.SetLength(fileSize);
        }
        File.Move(dst + ".tmp", dst, overwrite: true);
        // data/ beside it (the plain files the boot reads), as the version-1 pack's folder has them
        var srcData = Path.Combine(Path.GetDirectoryName(src), "data");
        if (Directory.Exists(srcData)) CopyDir(srcData, Path.Combine(outDir, "data"));
        // Verify files for the version-1 pack too (the switch back): its XXH32 per 1 MiB block, to sit beside it (shepherd.pack.xxh)
        File.WriteAllBytes(Path.Combine(outDir, VitaPackFormat.PackFileName + ".xxh"), PackVerify.Sidecar(src));
        long stored = plan.Where(p => p.CopyOf < 0).Sum(p => (long)p.Stored.Length);
        Say($"wrote {dst}: {fileSize:N0} B ({fileSize / 1048576.0:F2} MiB) vs version 1 {new FileInfo(src).Length:N0} B ({new FileInfo(src).Length / 1048576.0:F2} MiB) = {100.0 * fileSize / new FileInfo(src).Length:F1} %; " +
            $"TOC {v1.TocSize:N0} -> {tocStored.Length:N0} B ({C.CodecName(tocCodec)}); chunks stored {stored:N0} B, alignment padding {padBytes:N0} B, table {tableBytes:N0} B");
        // where the bytes went, by kind
        foreach (var g in plan.Where(p => p.CopyOf < 0).GroupBy(p => (p.Flags & C.ChunkImage) != 0 ? "image tiles" : "record tiles"))
            Say($"  {g.Key,-13} {g.Count(),6} chunks: {g.Sum(p => (long)p.LogicalLen) / 1048576.0,8:F2} MiB -> {g.Sum(p => (long)p.Stored.Length) / 1048576.0,8:F2} MiB ({100.0 * g.Sum(p => (long)p.Stored.Length) / g.Sum(p => (long)p.LogicalLen):F1} %)" +
                $"  [stored {g.Count(p => p.Codec == C.CodecStored)}, lz4 {g.Count(p => p.Codec == C.CodecLz4)}, zstd {g.Count(p => p.Codec == C.CodecZstd)}]");
        if (verify)
        {
            int e = VerifyWithReader(src, dst, Say);
            if (e != 0) { File.Delete(dst); Say("VERIFY FAILED: the new pack was removed"); return 1; }
        }
        Say($"repack done in {sw.Elapsed.TotalSeconds:F1} s");
        File.WriteAllText(Path.Combine(outDir, "repack-report.txt"), report.ToString());
        return 0;
    }

    static int CopyN(byte[] s, byte[] d) { s.CopyTo(d, 0); return s.Length; }
    static void CopyDir(string from, string to)
    {
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
    }

    // ------------------------------------------------------------------ the version-1 file

    sealed class V1
    {
        public byte[] Header, Toc; public uint Flags, TocHash; public long TocOff, TocSize, PayOff, PaySize, RecordsEnd;
        public readonly Dictionary<string, string> Props = new(StringComparer.Ordinal);   // review: the TOC's properties (bake parameters, payload-hash)
        public readonly List<(long off, int len, int fmt)> Images = new();
        public static V1 Open(string path)
        {
            var v = new V1();
            using var fs = File.OpenRead(path);
            v.Header = new byte[VitaPackFormat.HeaderSize]; fs.ReadExactly(v.Header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(v.Header) != VitaPackFormat.Magic || BinaryPrimitives.ReadUInt16LittleEndian(v.Header.AsSpan(4)) != 1) throw new InvalidDataException($"{path}: not a version-1 Shepherd pack");
            v.Flags = BinaryPrimitives.ReadUInt32LittleEndian(v.Header.AsSpan(8)); v.TocHash = BinaryPrimitives.ReadUInt32LittleEndian(v.Header.AsSpan(12));
            v.TocOff = BinaryPrimitives.ReadInt64LittleEndian(v.Header.AsSpan(16)); v.TocSize = BinaryPrimitives.ReadInt64LittleEndian(v.Header.AsSpan(24));
            v.PayOff = BinaryPrimitives.ReadInt64LittleEndian(v.Header.AsSpan(32)); v.PaySize = BinaryPrimitives.ReadInt64LittleEndian(v.Header.AsSpan(40));
            if (v.TocOff != v.PayOff + v.PaySize || v.TocOff + v.TocSize != fs.Length) throw new InvalidDataException($"{path}: unexpected layout (payload {v.PayOff}+{v.PaySize}, TOC {v.TocOff}+{v.TocSize}, file {fs.Length})");
            v.Toc = new byte[v.TocSize]; fs.Position = v.TocOff; fs.ReadExactly(v.Toc);
            if (VitaPackFormat.Fnv1a(v.Toc) != v.TocHash) throw new InvalidDataException($"{path}: TOC checksum mismatch");
            // walk the TOC to the object table
            var r = new SpanReader(v.Toc, bigEndian: false);
            int ns = r.I32(); r.Bytes((ns + 1) * 4); int blob = BinaryPrimitives.ReadInt32LittleEndian(v.Toc.AsSpan(4 + ns * 4)); r.Bytes(blob);
            int nsh = r.I32(); for (int i = 0; i < nsh; i++) { r.I32(); int nf = r.U16(); r.Bytes(nf * 5); }
            int nb = r.I32(); for (int i = 0; i < nb; i++) { r.I32(); int nf = r.U16(); r.Bytes(nf * 4); }
            int nfi = r.I32(); for (int i = 0; i < nfi; i++) { r.I32(); r.I32(); r.U16(); int ne = r.U16(); r.Bytes(ne * 4); r.I32(); r.I32(); int nc = r.U16(); r.Bytes(nc * 8); }
            int na = r.I32(); r.Bytes(na * 4);
            int no = r.I32(); var objs = r.Bytes(no * 32).ToArray();
            {   // review: the absent objects, then (header flag) the properties -- string ids into the TOC's string section
                int nao = r.I32(); r.Bytes(nao * 12);
                if ((v.Flags & VitaPackFormat.FlagProperties) != 0)
                {
                    int blobAt = 4 + (ns + 1) * 4;
                    string S(int id) { int a0 = BinaryPrimitives.ReadInt32LittleEndian(v.Toc.AsSpan(4 + id * 4)), a1 = BinaryPrimitives.ReadInt32LittleEndian(v.Toc.AsSpan(8 + id * 4)); return Encoding.UTF8.GetString(v.Toc, blobAt + a0, a1 - a0); }
                    int np = r.I32(); for (int i = 0; i < np; i++) { int k = r.I32(), x = r.I32(); v.Props[S(k)] = S(x); }
                }
            }
            long minImg = v.PaySize;
            var hdr = new byte[64];
            for (int i = 0; i < no; i++)
            {
                var e = objs.AsSpan(i * 32, 32);
                long off = BinaryPrimitives.ReadInt64LittleEndian(e[8..]); ushort fl = BinaryPrimitives.ReadUInt16LittleEndian(e[26..]);
                if ((fl & VitaPackFormat.ObjImage) == 0) continue;
                fs.Position = v.PayOff + off; fs.ReadExactly(hdr.AsSpan(0, 24));
                int tree = BinaryPrimitives.ReadInt32LittleEndian(hdr), raw = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(4)), img = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(8));
                int script = (fl & VitaPackFormat.ObjScript) != 0 ? 12 : 0;
                if (img < 28) continue;
                fs.Position = v.PayOff + off + 12 + script + tree + raw; fs.ReadExactly(hdr.AsSpan(0, 28));
                int fmt = BinaryPrimitives.ReadInt32LittleEndian(hdr), len = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(16)); long io = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(20));
                v.Images.Add((io, len, fmt)); if (io < minImg) minImg = io;
            }
            v.Images.Sort((a, b) => a.off.CompareTo(b.off));
            // drop duplicates (one image referenced twice) and check the images tile the region after the records exactly
            for (int i = v.Images.Count - 1; i > 0; i--) if (v.Images[i].off == v.Images[i - 1].off) v.Images.RemoveAt(i);
            v.RecordsEnd = minImg;
            long cur = minImg;
            foreach (var im in v.Images) { if (im.off != cur) throw new InvalidDataException($"{path}: images do not tile the image region (gap / overlap at {cur} vs {im.off})"); cur += im.len; }
            if (cur != v.PaySize) throw new InvalidDataException($"{path}: the image region ends at {cur}, the payload at {v.PaySize}");
            return v;
        }
    }

    sealed class P
    {
        public long Logical; public int LogicalLen; public ushort Flags; public int CopyOf = -1; public int ImageFmt = -1;
        public byte[] Lz4, Zstd, Lz4Fast, Zstd3, Zstd9, Stored; public byte Codec; public long StoredAt; public uint StoredHash, LogicalHash;
    }

    static List<P> Plan(V1 v, int tile)
    {
        var l = new List<P>();
        for (long o = 0; o < v.RecordsEnd; o += tile) l.Add(new P { Logical = o, LogicalLen = (int)Math.Min(tile, v.RecordsEnd - o) });
        foreach (var im in v.Images)
            for (long o = 0; o < im.len; o += tile) l.Add(new P { Logical = im.off + o, LogicalLen = (int)Math.Min(tile, im.len - o), Flags = C.ChunkImage, ImageFmt = im.fmt });
        return l;
    }

    static List<int> VisitOrder(List<P> plan, V1 v1, List<string> logs, Action<string> say)
    {
        var starts = plan.Select(p => p.Logical).ToArray();
        int Find(long rel) { int i = Array.BinarySearch(starts, rel); return i >= 0 ? i : ~i - 1; }
        var seen = new bool[plan.Count]; var order = new List<int>(plan.Count); long reads = 0;
        foreach (var path in logs)
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('\t', ' ');
                if (f.Length < 2 || !long.TryParse(f[0], out var off) || !int.TryParse(f[1], out var len) || len <= 0) continue;
                long rel = off - v1.PayOff; if (rel < 0 || rel >= v1.PaySize) continue;
                reads++;
                for (int i = Find(rel); i < plan.Count && plan[i].Logical < rel + len; i++)
                {
                    int k = plan[i].CopyOf >= 0 ? plan[i].CopyOf : i;
                    if (!seen[k]) { seen[k] = true; order.Add(k); }
                }
            }
        int touched = order.Count;
        for (int i = 0; i < plan.Count; i++) { int k = plan[i].CopyOf >= 0 ? plan[i].CopyOf : i; if (!seen[k]) { seen[k] = true; order.Add(k); } }
        say(logs.Count == 0 ? "visit order: none given (pack order: records, then images)" :
            $"visit order: {reads} reads in {logs.Count} log(s) touched {touched} distinct chunks ({plan.Where((p, i) => order.IndexOf(i) is var x && x >= 0 && x < touched).Sum(p => (long)p.LogicalLen) / 1048576.0:F1} MiB logical) -- stored first, in first-touch order; the other {order.Count - touched} after, in pack order");
        return order;
    }

    // ------------------------------------------------------------------ --analyze

    static void Analyze(List<P> plan, byte[] pay, V1 v1, Action<string> say, double cardB, double l4B, double zB)
    {
        string Kind(P p) => (p.Flags & C.ChunkImage) == 0 ? "records" : "image " + ((TextureFormat)p.ImageFmt).ToString();
        say("analysis (distinct chunks; stored sizes in MiB; % of logical):");
        say($"  {"kind",-22} {"n",5} {"logical",9} {"lz4-1",14} {"lz4hc-12",14} {"zstd-3",14} {"zstd-9",14} {"zstd-19",14} {"chosen",14}");
        foreach (var g in plan.Where(p => p.CopyOf < 0).GroupBy(Kind).OrderByDescending(g => g.Sum(p => (long)p.LogicalLen)))
        {
            double L = g.Sum(p => (long)p.LogicalLen);
            string Col(Func<P, byte[]> f) { double s = g.Sum(p => (long)(f(p)?.Length ?? p.LogicalLen)); return $"{s / 1048576.0,7:F2} {100 * s / L,5:F1}%"; }
            say($"  {g.Key,-22} {g.Count(),5} {L / 1048576.0,9:F2} {Col(p => p.Lz4Fast),14} {Col(p => p.Lz4),14} {Col(p => p.Zstd3),14} {Col(p => p.Zstd9),14} {Col(p => p.Zstd),14} {Col(p => p.Stored),14}");
        }
        double LL = plan.Where(p => p.CopyOf < 0).Sum(p => (long)p.LogicalLen);
        double S4 = plan.Where(p => p.CopyOf < 0).Sum(p => (long)p.Lz4.Length), SZ = plan.Where(p => p.CopyOf < 0).Sum(p => (long)p.Zstd.Length), SC = plan.Where(p => p.CopyOf < 0).Sum(p => (long)p.Stored.Length);
        // the device model over the whole pack (a full sequential read through the decode-ahead worker)
        double T(Func<P, double> f) => plan.Where(p => p.CopyOf < 0).Sum(f);
        double tRaw = LL / cardB;
        double t4 = T(p => Math.Max(p.Lz4.Length / cardB, p.LogicalLen / l4B)), tZ = T(p => Math.Max(p.Zstd.Length / cardB, p.LogicalLen / zB));
        double tC = T(p => p.Codec == C.CodecStored ? p.LogicalLen / cardB : p.Codec == C.CodecLz4 ? Math.Max(p.Lz4.Length / cardB, p.LogicalLen / l4B) : Math.Max(p.Zstd.Length / cardB, p.LogicalLen / zB));
        say($"  whole pack, pipelined model (ESTIMATE): raw {tRaw:F1} s, all-lz4hc {t4:F1} s ({S4 / 1048576.0:F1} MiB), all-zstd {tZ:F1} s ({SZ / 1048576.0:F1} MiB), chosen {tC:F1} s ({SC / 1048576.0:F1} MiB)");
        // decode cost on the reading thread when the worker is not ahead (serial: read + decode)
        double s4 = T(p => p.Lz4.Length / cardB + p.LogicalLen / l4B), sZ = T(p => p.Zstd.Length / cardB + p.LogicalLen / zB);
        say($"  serial model (no decode-ahead, ESTIMATE): all-lz4hc {s4:F1} s, all-zstd {sZ:F1} s");
        // mac decode speed (relative)
        var sw = Stopwatch.StartNew(); long nb = 0; var buf = new byte[1 << 20];
        foreach (var p in plan.Where(p => p.CopyOf < 0).Take(800)) { NativeCodecs.Lz4Decode(p.Lz4, buf.AsSpan(0, p.LogicalLen)); nb += p.LogicalLen; }
        double mac4 = nb / sw.Elapsed.TotalSeconds / 1048576.0; sw.Restart(); nb = 0;
        foreach (var p in plan.Where(p => p.CopyOf < 0).Take(800)) { NativeCodecs.ZstdDecode(p.Zstd, buf.AsSpan(0, p.LogicalLen)); nb += p.LogicalLen; }
        double macZ = nb / sw.Elapsed.TotalSeconds / 1048576.0; sw.Restart(); nb = 0;
        foreach (var p in plan.Where(p => p.CopyOf < 0).Take(800)) { Shepherd.Assets.Compression.Lz4.Decode(p.Lz4, buf.AsSpan(0, p.LogicalLen)); nb += p.LogicalLen; }
        double macM = nb / sw.Elapsed.TotalSeconds / 1048576.0;
        say($"  Mac decode (MEASURED-M, first 800 chunks, one thread): lz4 (library) {mac4:F0} MiB/s, zstd {macZ:F0} MiB/s, lz4 (managed Shepherd.Assets) {macM:F0} MiB/s -- zstd / lz4 = {macZ / mac4:F2}");
    }

    // ------------------------------------------------------------------ the engine's reader over the new file vs the old one

    sealed class MacDecoder : IPackDecoder
    {
        public unsafe int Decode(byte codec, byte* src, int srcLen, byte* dst, int dstLen, int ctx)
            => codec == C.CodecLz4 ? NativeCodecs.Lz4Decode(new ReadOnlySpan<byte>(src, srcLen), new Span<byte>(dst, dstLen))
             : codec == C.CodecZstd ? NativeCodecs.ZstdDecode(new ReadOnlySpan<byte>(src, srcLen), new Span<byte>(dst, dstLen)) : -1;
        public string Describe() => "Mac libraries (" + NativeCodecs.Versions() + ")";
    }

    /// <summary>The engine's PackReader opens both files; every object's record (tree / raw bytes), every image and the whole logical payload
    /// in random-sized pieces through both readers must agree byte for byte; then PackVerify checks every chunk's two hashes.</summary>
    static int VerifyWithReader(string v1Path, string v2Path, Action<string> say)
    {
        var sw = Stopwatch.StartNew();
        PackReader.ChunkDecoder = new MacDecoder();
        using var a = PackReader.Open(v1Path);
        using var b = PackReader.Open(v2Path);
        int errors = 0;
        if (a.ObjectCount != b.ObjectCount || a.FileCount != b.FileCount || a.Identity == null) { say("verify: counts differ"); return 1; }
        // the whole logical payload, in pieces of every size class the engine uses (window-sized, straddling, big)
        var rng = new Random(12345); long pos = a.PayloadOffset, end = a.PayloadOffset + a.PayloadSize; long pieces = 0;
        var ba = new byte[4 << 20]; var bb = new byte[4 << 20];
        while (pos < end)
        {
            int n = (int)Math.Min(end - pos, rng.Next(4) switch { 0 => rng.Next(1, 4096), 1 => rng.Next(4096, 70000), 2 => rng.Next(70000, 600000), _ => rng.Next(1, 4 << 20) });
            a.ReadLogicalForTest(pos, ba.AsSpan(0, n)); b.ReadLogicalForTest(pos, bb.AsSpan(0, n));
            if (!ba.AsSpan(0, n).SequenceEqual(bb.AsSpan(0, n))) { if (errors++ < 5) say($"verify: logical bytes differ in [{pos}, {pos + n})"); }
            pos += n; pieces++;
        }
        // every file's objects through the public readers
        long trees = 0, raws = 0, images = 0;
        foreach (var name in a.FileNames.ToList())
        {
            var fa = a.GetFile(name); var fb = b.GetFile(name);
            for (int i = 0; i < fa.Objects.Count; i++)
            {
                var oa = fa.Objects[i]; var ob = fb.Objects[i];
                if (oa.PathId != ob.PathId || oa.ByteStart != ob.ByteStart || oa.ByteSize != ob.ByteSize) { if (errors++ < 5) say($"verify: object table differs at {name} #{oa.PathId}"); continue; }
                if (PackReader.HasTree(oa))
                {
                    var ta = a.ReadTree(fa, oa); var tb = b.ReadTree(fb, ob); trees++;
                    if (!SameTree(ta, tb)) { if (errors++ < 5) say($"verify: tree differs at {name} #{oa.PathId}"); }
                    if (ta.TryGet(PackReader.ImageKey, out var ia) && tb.TryGet(PackReader.ImageKey, out var ib))
                    {
                        var xa = ((PackImage)ia).Read(); var xb = ((PackImage)ib).Read(); images++;
                        if (!xa.AsSpan().SequenceEqual(xb)) { if (errors++ < 5) say($"verify: image differs at {name} #{oa.PathId}"); }
                    }
                }
                if (a.RawLength(oa) > 0)
                {
                    var ra = a.ReadRaw(fa, oa); var rb = b.ReadRaw(fb, ob); raws++;
                    if (!ra.AsSpan().SequenceEqual(rb)) { if (errors++ < 5) say($"verify: raw bytes differ at {name} #{oa.PathId}"); }
                }
            }
        }
        var vr = PackVerify.Run(v2Path, PackReader.ChunkDecoder, deep: true, progress: null);
        say($"verify (engine PackReader, version 1 vs version 2): {pieces} logical pieces over {(end - a.PayloadOffset) / 1048576.0:F1} MiB, {trees} trees, {raws} raw records, {images} images -- {(errors == 0 ? "ALL IDENTICAL" : errors + " DIFFERENCES")}; " +
            $"PackVerify deep: {vr.Summary()} ({sw.Elapsed.TotalSeconds:F1} s)");
        return errors + (vr.Ok ? 0 : 1);
    }

    static bool SameTree(object x, object y)
    {
        if (x == null || y == null) return x == null && y == null;
        if (x.GetType() != y.GetType()) return false;
        switch (x)
        {
            case TypeTreeObject tx:
            {
                var ty = (TypeTreeObject)y; if (tx.TypeName != ty.TypeName || tx.Count != ty.Count) return false;
                for (int i = 0; i < tx.Count; i++)
                {
                    if (tx.Keys[i] != ty.Keys[i]) return false;
                    if (tx.Keys[i] == PackReader.ImageKey) { var a = (PackImage)tx.Values[i]; var b = (PackImage)ty.Values[i]; if (a.Offset != b.Offset || a.Length != b.Length || a.Format != b.Format || a.Width != b.Width || a.Height != b.Height || a.TopDown != b.TopDown) return false; continue; }
                    if (!SameTree(tx.Values[i], ty.Values[i])) return false;
                }
                return true;
            }
            case List<object> lx: { var ly = (List<object>)y; if (lx.Count != ly.Count) return false; for (int i = 0; i < lx.Count; i++) if (!SameTree(lx[i], ly[i])) return false; return true; }
            case byte[] bx: return bx.AsSpan().SequenceEqual((byte[])y);
            case float fx: return BitConverter.SingleToInt32Bits(fx) == BitConverter.SingleToInt32Bits((float)y);
            case double dx: return BitConverter.DoubleToInt64Bits(dx) == BitConverter.DoubleToInt64Bits((double)y);
            default: return x.Equals(y);
        }
    }
}
