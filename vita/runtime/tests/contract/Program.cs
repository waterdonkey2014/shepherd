// SHEPHERD-VITA: managed contract test of the Vita native shim (vita/runtime/tests/run-tests.sh).
// The EXACT linux-arm BCL assemblies the Vita runs (System.Security.Cryptography, System.IO.Compression[.Brotli],
// System.Net.*) are loaded on the Mac (CoreCLR) into their own AssemblyLoadContext, and their P/Invokes are bound to a
// Mac build of the Vita shim's C files (crypto, network refusal, zlib PAL, generated stubs; libvitapal.dylib, which
// re-exports the Mac libSystem.Native for everything else). The results are compared with the Mac's own BCL (Apple
// crypto, system zlib) -- so this tests the managed <-> native contract (call order, buffer sizes, error codes) that the
// C unit tests cannot see.  usage: Contract <linux-arm lib/net8.0 dir> <libvitapal.dylib>   (exit 0 = all pass)
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

namespace ShepherdVita.Contract;

sealed class LinuxBcl : AssemblyLoadContext
{
    readonly string _dir, _pal;
    public LinuxBcl(string dir, string pal) : base("linux-arm-bcl") { _dir = dir; _pal = pal; }
    static bool Ours(string n) => n.StartsWith("System.Security.Cryptography", StringComparison.Ordinal) || n.StartsWith("System.IO.Compression", StringComparison.Ordinal)
        || n.StartsWith("System.Net.", StringComparison.Ordinal);
    protected override Assembly Load(AssemblyName n) => Ours(n.Name) && File.Exists(Path.Combine(_dir, n.Name + ".dll")) ? LoadFromAssemblyPath(Path.Combine(_dir, n.Name + ".dll")) : null;
    protected override IntPtr LoadUnmanagedDll(string lib) =>
        lib is "libSystem.Security.Cryptography.Native.OpenSsl" or "libSystem.IO.Compression.Native" or "libSystem.Native" or "libSystem.Net.Security.Native"
            ? LoadUnmanagedDllFromPath(_pal) : IntPtr.Zero;
}

static class P
{
    static int _fail, _checks;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fail++; Console.WriteLine("FAIL " + what); } }
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly Ssc, Comp, Brotli, Prim, Sock, Dns_, NetInfo, Http;
    static readonly Random Rng = new(1234);

    static Type T(Assembly a, string n) => a.GetType(n, true);
    static object E(Assembly a, string type, int v) => Enum.ToObject(T(a, type), v);
    static bool Fits(ParameterInfo[] ps, object[] args) => ps.Length == args.Length && ps.Zip(args).All(x => x.Second == null ? !x.First.ParameterType.IsValueType : x.First.ParameterType.IsInstanceOfType(x.Second));
    static object Call(object o, string name, params object[] args)
    {
        var t = o as Type ?? o.GetType();
        var m = t.GetMethods(All).FirstOrDefault(m => m.Name == name && !m.IsGenericMethodDefinition && Fits(m.GetParameters(), args)) ?? throw new MissingMethodException(t.FullName, name);
        try { return m.Invoke(o is Type ? null : o, args); } catch (TargetInvocationException e) { throw e.InnerException; }
    }
    static object Get(object o, string name) { var t = o as Type ?? o.GetType(); return t.GetProperty(name, All).GetValue(o is Type ? null : o); }
    static void Set(object o, string name, object v) => o.GetType().GetProperty(name, All).SetValue(o, v);
    static object New(Type t, params object[] args) { try { return Activator.CreateInstance(t, args); } catch (TargetInvocationException e) { throw e.InnerException; } }
    static byte[] Bytes(int n) { var b = new byte[n]; Rng.NextBytes(b); return b; }

    static int Main(string[] a)
    {
        var alc = new LinuxBcl(a[0], a[1]);
        Assembly L(string n) => alc.LoadFromAssemblyName(new AssemblyName(n));
        Ssc = L("System.Security.Cryptography"); Comp = L("System.IO.Compression"); Brotli = L("System.IO.Compression.Brotli");
        Prim = L("System.Net.Primitives"); Sock = L("System.Net.Sockets"); Dns_ = L("System.Net.NameResolution"); NetInfo = L("System.Net.NetworkInformation"); Http = L("System.Net.Http");
        foreach (var x in new[] { Ssc, Comp, Prim, Sock, Http })
            Check(AssemblyLoadContext.GetLoadContext(x) == alc && x.Location.StartsWith(a[0]), $"{x.GetName().Name} comes from the linux-arm pack ({x.Location})");
        Run("crypto: AES", Aes_);
        Run("crypto: hashes, HMAC, PBKDF2, RNG", Hashes);
        Run("crypto: unsupported algorithms fail cleanly", Unsupported);
        Run("compression: Deflate/GZip/ZLib vs the Mac's zlib", Compression);
        Run("network: refused fast, socket addresses real", Network);
        Console.WriteLine($"{(_fail == 0 ? "PASS" : "FAIL")} contract ({_checks} checks, {_fail} failed)");
        return _fail == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        int f0 = _fail, c0 = _checks; var sw = Stopwatch.StartNew();
        try { t(); } catch (Exception e) { Check(false, $"{name}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        Console.WriteLine($"{(_fail == f0 ? "ok  " : "FAIL")} {name} ({_checks - c0} checks, {sw.ElapsedMilliseconds} ms)");
    }

    // ------------------------------------------------------------------ crypto
    static void Aes_()
    {
        var aesT = T(Ssc, "System.Security.Cryptography.Aes");
        foreach (var keyBits in new[] { 128, 192, 256 })
            foreach (var mode in new[] { CipherMode.CBC, CipherMode.ECB, CipherMode.CFB })
                foreach (var fb in mode == CipherMode.CFB ? new[] { 8, 128 } : new[] { 128 })
                    foreach (var pad in new[] { PaddingMode.PKCS7, PaddingMode.None, PaddingMode.Zeros, PaddingMode.ANSIX923, PaddingMode.ISO10126 })
                        for (int it = 0; it < 6; it++)
                        {
                            int block = mode == CipherMode.CFB ? fb / 8 : 16;
                            int n = Rng.Next(0, 3000); if (pad == PaddingMode.None) n -= n % block;
                            var key = Bytes(keyBits / 8); var iv = Bytes(16); var pt = Bytes(n);
                            using var mac = Aes.Create(); mac.Key = key; mac.IV = iv; mac.Mode = mode; mac.Padding = pad; mac.FeedbackSize = fb;
                            var lin = Call(aesT, "Create");
                            Set(lin, "Key", key); Set(lin, "IV", iv); Set(lin, "Mode", E(Ssc, "System.Security.Cryptography.CipherMode", (int)mode));
                            Set(lin, "Padding", E(Ssc, "System.Security.Cryptography.PaddingMode", (int)pad)); Set(lin, "FeedbackSize", fb);
                            string tag = $"AES-{keyBits} {mode}{(mode == CipherMode.CFB ? fb.ToString() : "")} {pad} n={n}";
                            var want = mac.CreateEncryptor().TransformFinalBlock(pt, 0, pt.Length);
                            var enc = Call(lin, "CreateEncryptor");
                            var got = (byte[])Call(enc, "TransformFinalBlock", pt, 0, pt.Length);
                            if (pad != PaddingMode.ISO10126) Check(got.AsSpan().SequenceEqual(want), $"{tag}: TransformFinalBlock ciphertext == Apple");
                            // block-wise TransformBlock + TransformFinalBlock (UniversalCryptoTransform's streaming path)
                            var enc2 = Call(lin, "CreateEncryptor"); var ms = new MemoryStream(); int off = 0;
                            int ib = (int)Get(enc2, "InputBlockSize");
                            while (pt.Length - off > ib * 3) { int take = ib * Rng.Next(1, 4); var o = new byte[take + 32]; int w = (int)Call(enc2, "TransformBlock", pt, off, take, o, 0); ms.Write(o, 0, w); off += take; }
                            ms.Write((byte[])Call(enc2, "TransformFinalBlock", pt, off, pt.Length - off));
                            if (pad != PaddingMode.ISO10126) Check(ms.ToArray().AsSpan().SequenceEqual(want), $"{tag}: TransformBlock chunks == Apple");
                            // decrypt Apple's ciphertext: TransformFinalBlock and a CryptoStream read (MMJsonDataReadWriter.ReadBytes' path)
                            var dec = Call(lin, "CreateDecryptor");
                            var back = (byte[])Call(dec, "TransformFinalBlock", want, 0, want.Length);
                            if (pad != PaddingMode.Zeros) Check(back.AsSpan().SequenceEqual(pt), $"{tag}: decrypt Apple ciphertext");
                            else Check(back.Length >= pt.Length && back.AsSpan(0, pt.Length).SequenceEqual(pt), $"{tag}: decrypt Apple ciphertext (zeros)");
                            var cs = (Stream)New(T(Ssc, "System.Security.Cryptography.CryptoStream"), new MemoryStream(want), Call(lin, "CreateDecryptor"), E(Ssc, "System.Security.Cryptography.CryptoStreamMode", 0));
                            var rd = new MemoryStream(); var buf = new byte[Rng.Next(1, 600)]; int r; while ((r = cs.Read(buf, 0, buf.Length)) > 0) rd.Write(buf, 0, r);
                            if (pad != PaddingMode.Zeros) Check(rd.ToArray().AsSpan().SequenceEqual(pt), $"{tag}: CryptoStream read");
                            // one-shot APIs (EncryptCbc / EncryptEcb / EncryptCfb), Apple decrypts them
                            if (pad != PaddingMode.ISO10126)
                            {
                                var linPad = E(Ssc, "System.Security.Cryptography.PaddingMode", (int)pad);
                                var one = mode switch
                                {
                                    CipherMode.CBC => (byte[])Call(lin, "EncryptCbc", pt, iv, linPad),
                                    CipherMode.ECB => (byte[])Call(lin, "EncryptEcb", pt, linPad),
                                    _ => (byte[])Call(lin, "EncryptCfb", pt, iv, linPad, fb),
                                };
                                Check(one.AsSpan().SequenceEqual(want), $"{tag}: one-shot encrypt == Apple");
                            }
                        }
        // the IV is generated by the RNG when not set (MMJsonDataReadWriter's write path); CryptoStream write mode
        var gen = Call(aesT, "Create"); var giv = (byte[])Get(gen, "IV"); var gkey = (byte[])Get(gen, "Key");
        Check(giv.Length == 16 && giv.Any(b => b != 0) && gkey.Length == 32, "generated IV/key");
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"settings\":true,\"volume\":0.8}", 400)));
        var outMs = new MemoryStream();
        using (var w = (Stream)New(T(Ssc, "System.Security.Cryptography.CryptoStream"), outMs, Call(gen, "CreateEncryptor"), E(Ssc, "System.Security.Cryptography.CryptoStreamMode", 1)))
            for (int i = 0; i < text.Length; i += 777) w.Write(text, i, Math.Min(777, text.Length - i));
        var written = outMs.ToArray();   // (the CryptoStream closed it)
        using var am = Aes.Create(); am.Key = gkey; am.IV = giv;
        Check(am.CreateDecryptor().TransformFinalBlock(written, 0, written.Length).AsSpan().SequenceEqual(text), "CryptoStream write (generated key/IV) decrypts on Apple");
        // a corrupted last block: PKCS7 check fails with CryptographicException, no crash
        var bad = am.CreateEncryptor().TransformFinalBlock(text, 0, 100); bad[^1] ^= 0x55;
        var lb = Call(aesT, "Create"); Set(lb, "Key", gkey); Set(lb, "IV", giv);
        try { Call(Call(lb, "CreateDecryptor"), "TransformFinalBlock", bad, 0, bad.Length); Check(false, "bad padding not detected"); }
        catch (Exception e) { Check(e is CryptographicException, $"bad padding -> CryptographicException ({e.GetType().Name}: {e.Message})"); }
    }

    static void Hashes()
    {
        var names = new[] { "MD5", "SHA1", "SHA256", "SHA384", "SHA512" };
        foreach (var nm in names)
        {
            var linT = T(Ssc, "System.Security.Cryptography." + nm);
            var macT = typeof(SHA256).Assembly.GetType("System.Security.Cryptography." + nm);
            var hanL = Get(T(Ssc, "System.Security.Cryptography.HashAlgorithmName"), nm);
            for (int it = 0; it < 30; it++)
            {
                var data = Bytes(Rng.Next(0, 5000)); var key = Bytes(Rng.Next(0, 200));
                var want = (byte[])macT.GetMethod("HashData", new[] { typeof(byte[]) }).Invoke(null, new object[] { data });
                Check(((byte[])Call(linT, "HashData", data)).AsSpan().SequenceEqual(want), $"{nm}.HashData n={data.Length}");
                using var h = (IDisposable)Call(linT, "Create");
                Check(((byte[])Call(h, "ComputeHash", data)).AsSpan().SequenceEqual(want), $"{nm}.Create().ComputeHash");
                Check(((byte[])Call(h, "ComputeHash", data)).AsSpan().SequenceEqual(want), $"{nm} reuse after ComputeHash");
                var inc = Call(T(Ssc, "System.Security.Cryptography.IncrementalHash"), "CreateHash", hanL);
                for (int off = 0; off < data.Length;) { int t = Math.Min(data.Length - off, Rng.Next(1, 700)); Call(inc, "AppendData", data, off, t); off += t; }
                Check(((byte[])Call(inc, "GetHashAndReset")).AsSpan().SequenceEqual(want), $"IncrementalHash {nm}");
                // HMAC
                var macH = (byte[])typeof(HMACSHA256).Assembly.GetType("System.Security.Cryptography.HMAC" + nm).GetMethod("HashData", new[] { typeof(byte[]), typeof(byte[]) }).Invoke(null, new object[] { key, data });
                var hm = Call(T(Ssc, "System.Security.Cryptography.HMAC" + nm), "HashData", key, data);
                Check(((byte[])hm).AsSpan().SequenceEqual(macH), $"HMAC{nm}.HashData");
                using var hmo = (IDisposable)New(T(Ssc, "System.Security.Cryptography.HMAC" + nm), key);
                Check(((byte[])Call(hmo, "ComputeHash", data)).AsSpan().SequenceEqual(macH), $"new HMAC{nm}(key).ComputeHash");
                var ih = Call(T(Ssc, "System.Security.Cryptography.IncrementalHash"), "CreateHMAC", hanL, key);
                Call(ih, "AppendData", data); Check(((byte[])Call(ih, "GetHashAndReset")).AsSpan().SequenceEqual(macH), $"IncrementalHash HMAC {nm}");
                if (nm != "MD5" && it < 3)
                {
                    var salt = Bytes(16); int iters = Rng.Next(1, 2000), len = Rng.Next(1, 90);
                    var macK = Rfc2898DeriveBytes.Pbkdf2(key, salt, iters, new HashAlgorithmName(nm), len);
                    var linK = (byte[])Call(T(Ssc, "System.Security.Cryptography.Rfc2898DeriveBytes"), "Pbkdf2", key, salt, iters, hanL, len);
                    Check(linK.AsSpan().SequenceEqual(macK), $"Rfc2898DeriveBytes.Pbkdf2 {nm}");
                }
            }
        }
        var rng = (byte[])Call(T(Ssc, "System.Security.Cryptography.RandomNumberGenerator"), "GetBytes", 64);
        var rng2 = (byte[])Call(T(Ssc, "System.Security.Cryptography.RandomNumberGenerator"), "GetBytes", 64);
        Check(rng.Length == 64 && !rng.AsSpan().SequenceEqual(rng2), "RandomNumberGenerator.GetBytes");
        // Rewired.Utils.MiscTools.HashSHA1's shape: SHA1.Create() + ComputeHash(UTF8 bytes) + Dispose
        var s = Encoding.UTF8.GetBytes("Keyboard|Rewired|00000000-0000-0000-0000-000000000000");
        using var sh = (IDisposable)Call(T(Ssc, "System.Security.Cryptography.SHA1"), "Create");
        Check(((byte[])Call(sh, "ComputeHash", s)).AsSpan().SequenceEqual(SHA1.HashData(s)), "SHA1 (Rewired's use)");
    }

    static void Unsupported()
    {
        Check((bool)Get(T(Ssc, "System.Security.Cryptography.SHA3_256"), "IsSupported") == false, "SHA3_256.IsSupported == false");
        try { New(T(Ssc, "System.Security.Cryptography.AesGcm"), Bytes(16), 16); Check(false, "AesGcm constructed"); }
        catch (Exception e) { Check(e is CryptographicException or PlatformNotSupportedException, $"AesGcm -> {e.GetType().Name}: {e.Message}"); }
        try { var d = Call(T(Ssc, "System.Security.Cryptography.TripleDES"), "Create"); Set(d, "Key", Bytes(24)); Call(Call(d, "CreateEncryptor"), "TransformFinalBlock", Bytes(16), 0, 16); Check(false, "3DES worked"); }
        catch (Exception e) { Check(e is CryptographicException, $"TripleDES -> CryptographicException ({e.GetType().Name}: {e.Message})"); }
        try { var r = Call(T(Ssc, "System.Security.Cryptography.RSA"), "Create"); Call(r, "ExportParameters", false); Check(false, "RSA worked"); }
        catch (Exception e) { Check(e is CryptographicException or PlatformNotSupportedException, $"RSA -> {e.GetType().Name}: {e.Message}"); }
    }

    // ------------------------------------------------------------------ compression
    static void Compression()
    {
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 6000).Select(i => $"line {i % 97} of the cult save {i * 31 % 1000}\n")));
        foreach (var data in new[] { Array.Empty<byte>(), Bytes(70000), text })
            foreach (var kind in new[] { "DeflateStream", "GZipStream", "ZLibStream" })
                foreach (var lvl in new[] { CompressionLevelCompat.Optimal, CompressionLevelCompat.Fastest, CompressionLevelCompat.NoCompression, CompressionLevelCompat.SmallestSize })
                {
                    string tag = $"{kind} {lvl} n={data.Length}";
                    var linT = T(Comp, "System.IO.Compression." + kind);
                    var ms = new MemoryStream();
                    using (var z = (Stream)New(linT, ms, E(Comp, "System.IO.Compression.CompressionLevel", (int)lvl), true))
                        for (int i = 0; i < data.Length; i += 9000) z.Write(data, i, Math.Min(9000, data.Length - i));
                    var macT = typeof(System.IO.Compression.DeflateStream).Assembly.GetType("System.IO.Compression." + kind);
                    var back = new MemoryStream();
                    using (var z = (Stream)Activator.CreateInstance(macT, new MemoryStream(ms.ToArray()), System.IO.Compression.CompressionMode.Decompress)) z.CopyTo(back);
                    Check(back.ToArray().AsSpan().SequenceEqual(data), $"{tag}: linux-compressed -> Mac decompress");
                    var mc = new MemoryStream();
                    using (var z = (Stream)Activator.CreateInstance(macT, mc, (System.IO.Compression.CompressionLevel)(int)lvl, true)) z.Write(data);
                    var back2 = new MemoryStream();
                    using (var z = (Stream)New(linT, new MemoryStream(mc.ToArray()), E(Comp, "System.IO.Compression.CompressionMode", 0)))
                    { var buf = new byte[Rng.Next(1, 5000)]; int r; while ((r = z.Read(buf, 0, buf.Length)) > 0) back2.Write(buf, 0, r); }
                    Check(back2.ToArray().AsSpan().SequenceEqual(data), $"{tag}: Mac-compressed -> linux decompress");
                }
        // corrupt input -> InvalidDataException; Brotli (stubbed) -> a managed exception, not a crash
        try { using var z = (Stream)New(T(Comp, "System.IO.Compression.DeflateStream"), new MemoryStream(new byte[] { 0xff, 0xff, 0xff, 0xff }), E(Comp, "System.IO.Compression.CompressionMode", 0)); z.ReadByte(); Check(false, "corrupt deflate accepted"); }
        catch (Exception e) { Check(e is InvalidDataException, $"corrupt deflate -> InvalidDataException ({e.GetType().Name})"); }
        try { using var z = (Stream)New(T(Brotli, "System.IO.Compression.BrotliStream"), new MemoryStream(), E(Comp, "System.IO.Compression.CompressionLevel", 0)); z.Write(text, 0, 100); z.Flush(); Check(false, "brotli worked"); }
        catch (Exception e) { Check(e is IOException or InvalidOperationException or OutOfMemoryException, $"BrotliStream -> {e.GetType().Name}: {e.Message}"); }
    }
    enum CompressionLevelCompat { Optimal = 0, Fastest = 1, NoCompression = 2, SmallestSize = 3 }

    // ------------------------------------------------------------------ network
    static void Network()
    {
        Type Any(string n) => new[] { Prim, Sock }.Select(x => x.GetType(n)).First(x => x != null);
        var af = Any("System.Net.Sockets.AddressFamily"); var st = Any("System.Net.Sockets.SocketType"); var pt = Any("System.Net.Sockets.ProtocolType");
        var sockT = T(Sock, "System.Net.Sockets.Socket");
        var sw = Stopwatch.StartNew();
        try { New(sockT, Enum.ToObject(af, 2), Enum.ToObject(st, 1), Enum.ToObject(pt, 6)); Check(false, "a socket was created"); }
        catch (Exception e) { Check(e.GetType().FullName == "System.Net.Sockets.SocketException" && (int)Get(e, "SocketErrorCode") == 10047, $"new Socket -> SocketException AddressFamilyNotSupported ({e.GetType().Name}: {e.Message})"); }
        Check(sw.ElapsedMilliseconds < 500, $"socket refused fast ({sw.ElapsedMilliseconds} ms)");
        Check((bool)Get(sockT, "OSSupportsIPv4") == false && (bool)Get(sockT, "OSSupportsIPv6") == false, "Socket.OSSupportsIPv4/IPv6 == false");
        sw.Restart();
        try { New(T(Sock, "System.Net.Sockets.TcpClient"), "127.0.0.1", 9); Check(false, "TcpClient connected"); }
        catch (Exception e) { Check(e.GetType().FullName == "System.Net.Sockets.SocketException", $"TcpClient -> {e.GetType().Name}: {e.Message}"); }
        var dnsT = T(Dns_, "System.Net.Dns");
        try { Call(dnsT, "GetHostEntry", "example.com"); Check(false, "DNS resolved"); }
        catch (Exception e) { Check(e.GetType().FullName == "System.Net.Sockets.SocketException", $"Dns.GetHostEntry -> {e.GetType().Name}: {e.Message}"); }
        var lit = (Array)Call(dnsT, "GetHostAddresses", "127.0.0.1");
        Check(lit.Length == 1 && lit.GetValue(0).ToString() == "127.0.0.1", "Dns.GetHostAddresses(literal) needs no resolver");
        // HttpClient (SocketsHttpHandler): HttpRequestException at once
        try
        {
            using var hc = (IDisposable)New(T(Http, "System.Net.Http.HttpClient"));
            var task = (Task)Call(hc, "GetAsync", "http://127.0.0.1:9/");
            try { task.Wait(5000); } catch (AggregateException) { }
            Check(task.IsCompleted, "HttpClient finished");
            var ex = task.Exception?.GetBaseException();
            Check(task.IsFaulted && (ex?.GetType().FullName is "System.Net.Http.HttpRequestException" or "System.Net.Sockets.SocketException"), $"HttpClient.GetAsync -> {ex?.GetType().Name}: {ex?.Message}");
        }
        catch (Exception e) { Check(e.GetType().FullName is "System.Net.Http.HttpRequestException" or "System.Net.Sockets.SocketException", $"HttpClient -> {e.GetType().Name}: {e.Message}"); }
        Check(sw.ElapsedMilliseconds < 3000, $"TcpClient + DNS + HttpClient refused fast ({sw.ElapsedMilliseconds} ms)");
        // socket addresses: IPEndPoint <-> SocketAddress round trips run the real helpers
        var ipT = T(Prim, "System.Net.IPAddress"); var epT = T(Prim, "System.Net.IPEndPoint");
        foreach (var (addr, port, size) in new[] { ("10.1.2.3", 8080, 16), ("255.255.255.255", 65535, 16), ("fe80::1:2:3%7", 443, 28), ("::1", 1, 28) })
        {
            var ep = New(epT, Call(ipT, "Parse", addr), port);
            var sa = Call(ep, "Serialize");
            Check((int)Get(sa, "Size") == size, $"SocketAddress size {addr}");
            var ep2 = Call(ep, "Create", sa);
            Check(ep2.Equals(ep) && ep2.ToString() == ep.ToString(), $"IPEndPoint round trip {ep} -> {ep2}");
        }
        var ni = T(NetInfo, "System.Net.NetworkInformation.NetworkInterface");
        Check(((Array)Call(ni, "GetAllNetworkInterfaces")).Length == 0, "NetworkInterface.GetAllNetworkInterfaces () is empty");
        Check((bool)Call(ni, "GetIsNetworkAvailable") == false, "NetworkInterface.GetIsNetworkAvailable () == false");
    }
}
