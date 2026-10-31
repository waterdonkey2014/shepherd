// Device kit #10, track cache (vita/notes/kit10-cache.md): the Mac self-test of the learning cache's store (engine Runtime/K10Store.cs, linked
// as source -- the same code the Vita runs). Every case works in a fresh temp folder; nothing outside it is touched.
// usage: dotnet run --project tools/K10CacheTest   -> "[k10test] N PASS, M FAIL", exit 0 when all pass
using System.Text;
using Shepherd.Engine.Runtime;

int pass = 0, fail = 0;
void Check(bool ok, string what) { if (ok) pass++; else fail++; Console.WriteLine($"[k10test] {(ok ? "PASS" : "FAIL")} {what}"); }
var tmp = Path.Combine(Path.GetTempPath(), $"shepherd-k10test-{Environment.ProcessId}");
var outside = Path.Combine(tmp, "outside.txt");
string Root(string name) => Path.Combine(tmp, name, "cache");
byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
var rnd = new Random(12345);
Directory.CreateDirectory(tmp); File.WriteAllText(outside, "not the cache's");
try
{
    // ---- 1. the entry file
    const string key = "shepherd learning cache 1|kind=t|v=1|engine=e|game=g|pack=p|scene=Main Menu";
    var pay = Bytes(1000, 1);
    var f = K10Store.Encode(key, 1, pay);
    Check(K10Store.Decode(f, key, 1, out var back) == null && back.AsSpan().SequenceEqual(pay), "entry round trip");
    Check(K10Store.Decode(f, key + "x", 1, out _)?.Contains("key differs") == true, "another key is refused");
    Check(K10Store.Decode(f, key, 2, out _)?.Contains("kind version") == true, "another kind version is refused");
    int flips = 0, flipOk = 0;
    for (int i = 0; i < f.Length; i++) { var g = (byte[])f.Clone(); g[i] ^= (byte)(1 + rnd.Next(255)); flips++; try { if (K10Store.Decode(g, key, 1, out _) != null) flipOk++; } catch { } }
    Check(flipOk == flips, $"every single-byte change refused without an exception ({flipOk} / {flips})");
    int cuts = 0, cutOk = 0;
    for (int n = 0; n < f.Length; n += 7) { cuts++; try { if (K10Store.Decode(f.AsSpan(0, n).ToArray(), key, 1, out _) != null) cutOk++; } catch { } }
    Check(cutOk == cuts, $"every truncation refused without an exception ({cutOk} / {cuts})");
    { var g = new byte[f.Length + 1]; f.CopyTo(g, 0); Check(K10Store.Decode(g, key, 1, out _) != null, "a byte after the checksum is refused"); }
    Check(K10Store.PathOf("readorder", key) == K10Store.PathOf("readorder", key) && K10Store.PathOf("readorder", key) != K10Store.PathOf("readorder", key + " "), "the file name follows the exact key");

    // ---- 2. put / get / verified on first reuse / the index across sessions
    var free = new Free { Bytes = 32L << 30 };
    var r1 = Root("s1");
    var s = new K10Store(r1, 1L << 20, 0, free);
    Check(s.Get("t", key, 1, out _) == null && s.Misses == 1, "a missing entry is a miss");
    Check(s.Put("t", key, 1, pay) == null && s.Writes == 1, "put");
    var got = s.Get("t", key, 1, out var ver);
    Check(got != null && got.AsSpan().SequenceEqual(pay) && !ver, "get: the payload, unverified (a new entry)");
    s.Confirm("t", key);
    got = s.Get("t", key, 1, out ver);
    Check(ver, "confirmed = verified");
    Check(s.Flush() == null && File.Exists(Path.Combine(r1, K10Store.IndexName)) && !s.Dirty, "the index written (and not dirty after)");
    var s2 = new K10Store(r1, 1L << 20, 0, free);
    string note2 = null; s2.Note = n => note2 ??= n;
    s2.Reconcile();
    Check(note2 != null && note2.Contains(" 0 not in the index, 0 gone, 0 changed size") && !s2.Dirty, "a new session on an unchanged folder: nothing added or gone (the index does not count itself), the index not rewritten (" + note2 + ")");
    got = s2.Get("t", key, 1, out ver);
    Check(got != null && ver, "a new session (a new store on the same folder): the entry and its verified flag");
    s2.Drop("t", key);
    Check(s2.Get("t", key, 1, out _) == null && !File.Exists(Path.Combine(r1, K10Store.PathOf("t", key))), "drop deletes the entry");
    Check(s2.Get("t", key + "!", 1, out _) == null, "another key: a miss");
    // the same path with another key text in it (a 64-bit name collision, forced): refused, deleted
    s2.Put("t", key, 1, pay);
    var p1 = Path.Combine(r1, K10Store.PathOf("t", key));
    File.WriteAllBytes(p1, K10Store.Encode(key + "-other", 1, pay));
    Check(s2.Get("t", key, 1, out _) == null && !File.Exists(p1) && s2.Damaged >= 1, "a file holding another key under this key's name: refused and deleted");
    // a damaged entry
    s2.Put("t", key, 1, pay);
    var dmg = File.ReadAllBytes(p1); dmg[40] ^= 0x55; File.WriteAllBytes(p1, dmg);
    Check(s2.Get("t", key, 1, out _) == null && !File.Exists(p1), "a damaged entry: a miss, deleted");
    // review fix 2026-10-28: the engine defers that delete to an idle window (a delete is a card write)
    var defer = new DeferLog(); s2.Defer = defer;
    s2.Put("t", key, 1, pay); File.WriteAllBytes(p1, dmg);
    Check(s2.Get("t", key, 1, out _) == null && File.Exists(p1) && defer.N == 1 && defer.Kind == "t" && defer.Key == key, "Defer: a damaged entry is a miss, handed over (not deleted by the read)");
    Check(s2.DropIfUnusable("t", key, 1) && !File.Exists(p1), "Defer: the deferred delete removes it while it is still unusable");
    File.WriteAllBytes(p1, dmg); s2.Get("t", key, 1, out _); s2.Put("t", key, 1, pay);
    Check(!s2.DropIfUnusable("t", key, 1) && s2.Get("t", key, 1, out _) != null, "Defer: a good entry written before the deferred delete runs is kept");
    s2.Defer = null;

    // ---- 3. the cap: least recently used out first; the older kinds counted, never evicted by age
    var r3 = Root("s3");
    Directory.CreateDirectory(Path.Combine(r3, "gxp"));
    File.WriteAllBytes(Path.Combine(r3, "gxp", "programs-x.gxpc"), Bytes(100_000, 2));   // an older kind's file
    var s3 = new K10Store(r3, 400_000, 0, free);
    string K(int i) => key + "#" + i;
    s3.Put("t", K(1), 1, Bytes(90_000, 11)); s3.Put("t", K(2), 1, Bytes(90_000, 12)); s3.Put("t", K(3), 1, Bytes(90_000, 13));
    s3.Get("t", K(1), 1, out _);   // 1 is used again: 2 is now the least recently used
    Check(s3.InUse > 370_000 && s3.InUse < 400_000, $"in use counts every file ({s3.InUse} B: the gxp file + three entries)");
    s3.Put("t", K(4), 1, Bytes(90_000, 14));
    Check(s3.Get("t", K(2), 1, out _) == null && s3.Get("t", K(1), 1, out _) != null && s3.Get("t", K(3), 1, out _) != null && s3.Get("t", K(4), 1, out _) != null && s3.Evicted == 1,
          "over the cap: the least recently used entry (2) went, 1 / 3 / 4 stay");
    Check(File.Exists(Path.Combine(r3, "gxp", "programs-x.gxpc")), "the older kind's file is never evicted by age");
    Check(s3.Put("t", K(5), 1, Bytes(500_000, 15)) != null && s3.Refused == 1, "an entry bigger than the cap is refused");
    // nothing older to remove: the older kinds fill the cap
    var r3b = Root("s3b"); Directory.CreateDirectory(Path.Combine(r3b, "json")); File.WriteAllBytes(Path.Combine(r3b, "json", "catalog.jbake"), Bytes(380_000, 3));
    var s3b = new K10Store(r3b, 400_000, 0, free);
    Check(s3b.Put("t", K(1), 1, Bytes(30_000, 16)) != null && File.Exists(Path.Combine(r3b, "json", "catalog.jbake")), "a cap filled by the older kinds: refused, they stay");
    // shrink after the cap went down
    s3.Cap = 200_000; long sh = s3.Shrink();
    Check(s3.InUse <= 200_000 && sh > 0 && File.Exists(Path.Combine(r3, "gxp", "programs-x.gxpc")), $"Cache Size lowered: least recently used entries out ({sh} B), in use {s3.InUse} <= cap");

    // ---- 4. the free-space margin
    var r4 = Root("s4");
    var tight = new Free { Bytes = 300_000 };
    var s4 = new K10Store(r4, 10L << 20, 250_000, tight);
    Check(s4.Put("t", K(1), 1, Bytes(40_000, 21)) == null, "a write that leaves more than the margin free: written");
    tight.Bytes = 300_000 - 40_000;
    var why = s4.Put("t", K(2), 1, Bytes(40_000, 22));
    Check(why == null && s4.Get("t", K(1), 1, out _) == null && s4.Evicted == 1, "a write that would cross the margin evicts the oldest entry first");
    tight.Bytes = 100_000;
    why = s4.Put("t", K(3), 1, Bytes(40_000, 23));
    Check(why != null && why.Contains("margin") && s4.Get("t", K(3), 1, out _) == null, "not enough even after evicting: refused (" + why + ")");
    var s4u = new K10Store(Root("s4u"), 10L << 20, 250_000, new Free { Bytes = -1 });
    Check(s4u.Put("t", K(1), 1, Bytes(1000, 24)) == null, "free space unknown (-1): the cap alone decides");

    // ---- 5. reconcile: stray temp files, changed sizes, vanished files, a lost index
    var r5 = Root("s5");
    var s5 = new K10Store(r5, 1L << 20, 0, free);
    s5.Put("t", K(1), 1, Bytes(5000, 31)); s5.Put("t", K(2), 1, Bytes(5000, 32)); s5.Confirm("t", K(1)); s5.Confirm("t", K(2)); s5.Flush();
    File.WriteAllBytes(Path.Combine(r5, "t", "junk.k10.tmp"), new byte[] { 1, 2, 3 });
    File.WriteAllBytes(Path.Combine(r5, K10Store.IndexName + ".tmp"), new byte[] { 1, 2, 3 });
    Directory.CreateDirectory(Path.Combine(r5, "gxp")); File.WriteAllBytes(Path.Combine(r5, "gxp", "programs-x.gxpc.tmp"), new byte[] { 4, 5 });   // other writers mid-write
    File.WriteAllBytes(Path.Combine(r5, "gc-nursery.tsv.tmp"), new byte[] { 6, 7, 8 });
    File.Delete(Path.Combine(r5, K10Store.PathOf("t", K(2))));
    var e1 = Path.Combine(r5, K10Store.PathOf("t", K(1))); var e1b = File.ReadAllBytes(e1);
    File.WriteAllBytes(e1, e1b.Concat(new byte[] { 0 }).ToArray());   // its size changed behind the index
    var s5b = new K10Store(r5, 1L << 20, 0, free);
    s5b.Reconcile();
    Check(!File.Exists(Path.Combine(r5, "t", "junk.k10.tmp")) && !File.Exists(Path.Combine(r5, K10Store.IndexName + ".tmp")), "this store's stray temp files (an entry's, the index's) are deleted");
    Check(File.Exists(Path.Combine(r5, "gxp", "programs-x.gxpc.tmp")) && File.Exists(Path.Combine(r5, "gc-nursery.tsv.tmp")), "other writers' temp files (GXP cache, gc-nursery.tsv.tmp) are left alone");
    { long f5 = 0; foreach (var file in Directory.GetFiles(r5, "*", SearchOption.AllDirectories)) if (!file.EndsWith(".tmp")) f5 += new FileInfo(file).Length; Check(s5b.InUse == f5, $"other writers' temp files are not counted ({s5b.InUse} = {f5})"); }
    Check(s5b.Get("t", K(1), 1, out _) == null, "an entry changed behind the index: refused (its checksum) and deleted");
    Check(s5b.Get("t", K(2), 1, out _) == null, "a vanished entry: a miss");
    var s5c = new K10Store(r5, 1L << 20, 0, free); s5c.Put("t", K(3), 1, Bytes(5000, 33)); s5c.Confirm("t", K(3)); s5c.Flush();
    File.Delete(Path.Combine(r5, K10Store.IndexName));
    var s5d = new K10Store(r5, 1L << 20, 0, free);
    got = s5d.Get("t", K(3), 1, out ver);
    Check(got != null && !ver, "the index lost: the entry still serves, unverified again (re-verified on its next reuse)");

    // ---- 6. Clear: everything under cache/, nothing outside it
    var r6 = Root("s6");
    var s6 = new K10Store(r6, 1L << 20, 0, free);
    s6.Put("t", K(1), 1, Bytes(5000, 41)); s6.Flush();
    Directory.CreateDirectory(Path.Combine(r6, "gxp")); File.WriteAllBytes(Path.Combine(r6, "gxp", "a.gxpc"), new byte[10]);
    File.WriteAllBytes(Path.Combine(r6, "jit-learn.tsv"), new byte[10]);
    var save = Path.Combine(tmp, "s6", "persistent", "saves", "slot_0.mp"); Directory.CreateDirectory(Path.GetDirectoryName(save)); File.WriteAllBytes(save, new byte[] { 7 });
    var settings = Path.Combine(tmp, "s6", "vita-settings.txt"); File.WriteAllText(settings, "core3=on\n");
    long cleared = s6.Clear(out int failed);
    Check(failed == 0 && cleared > 5000 && Directory.Exists(r6) && Directory.GetFileSystemEntries(r6).Length == 0 && s6.InUse == 0, $"Clear: every file and folder under cache/ deleted ({cleared} B)");
    Check(File.Exists(save) && File.Exists(settings) && File.Exists(outside), "Clear: saves, vita-settings.txt and everything outside cache/ untouched");
    Check(s6.Put("t", K(2), 1, Bytes(100, 42)) == null && s6.Get("t", K(2), 1, out _) != null, "after Clear the store works again");
    // review fix 2026-10-28: Clear keeps json/ (the catalog bake the kit stages there)
    var r6k = Root("s6k");
    var s6k = new K10Store(r6k, 1L << 20, 0, free);
    s6k.Put("t", K(1), 1, Bytes(5000, 43)); s6k.Flush();
    Directory.CreateDirectory(Path.Combine(r6k, "json")); File.WriteAllBytes(Path.Combine(r6k, "json", "catalog.jbake"), Bytes(1234, 44)); File.WriteAllText(Path.Combine(r6k, "json", "catalog.token.json"), "{}");
    Directory.CreateDirectory(Path.Combine(r6k, "jsonx")); File.WriteAllBytes(Path.Combine(r6k, "jsonx", "a"), new byte[10]);
    Directory.CreateDirectory(Path.Combine(r6k, "gxp")); File.WriteAllBytes(Path.Combine(r6k, "gxp", "a.gxpc"), new byte[10]);
    var s6k2 = new K10Store(r6k, 1L << 20, 0, free); s6k2.Reconcile();
    var keepJ = new[] { "json" };
    long clr = s6k2.Clearable(keepJ);
    long ck = s6k2.Clear(keepJ, out int fk);
    Check(fk == 0 && File.Exists(Path.Combine(r6k, "json", "catalog.jbake")) && File.Exists(Path.Combine(r6k, "json", "catalog.token.json")), "Clear (keep json): the catalog bake and its token stay");
    Check(!Directory.Exists(Path.Combine(r6k, "jsonx")) && !Directory.Exists(Path.Combine(r6k, "gxp")) && !Directory.Exists(Path.Combine(r6k, "t")) && !File.Exists(Path.Combine(r6k, K10Store.IndexName)), "Clear (keep json): every other file and folder deleted (a folder whose name only starts with json too)");
    Check(ck == clr && s6k2.InUse == 1234 + 2 && s6k2.Clearable(keepJ) == 0, $"Clear (keep json): the confirmation's amount = what was deleted ({clr} = {ck}); the kept files stay counted ({s6k2.InUse} B)");

    // ---- 7. threads: parallel puts / gets / flushes (the worker + the main thread's reads)
    var r7 = Root("s7");
    var s7 = new K10Store(r7, 2L << 20, 0, free);
    int errs = 0;
    var ts = new Thread[4];
    for (int t = 0; t < ts.Length; t++)
    {
        int tt = t;
        ts[t] = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 60; i++)
                {
                    var kk = K(tt * 1000 + i % 20); var pl = Bytes(3000 + i, tt * 100 + i);
                    if (i % 3 == 0) s7.Put("t", kk, 1, pl); else s7.Get("t", kk, 1, out _);
                    if (i % 10 == 0) s7.Flush();
                }
            }
            catch (Exception) { Interlocked.Increment(ref errs); }
        });
        ts[t].Start();
    }
    foreach (var t in ts) t.Join();
    long sum = 0; foreach (var file in Directory.GetFiles(r7, "*", SearchOption.AllDirectories)) sum += new FileInfo(file).Length;
    s7.Flush(); sum = 0; foreach (var file in Directory.GetFiles(r7, "*", SearchOption.AllDirectories)) sum += new FileInfo(file).Length;
    Check(errs == 0 && sum == s7.InUse, $"four threads: no exception, in use = the folder's bytes ({s7.InUse} = {sum})");
}
finally { try { Directory.Delete(tmp, true); } catch { } }
Console.WriteLine($"[k10test] {pass} PASS, {fail} FAIL");
return fail == 0 ? 0 : 1;

sealed class Free : K10Store.IFree { public long Bytes; public long FreeBytes() => Bytes; }
sealed class DeferLog : K10Store.IDefer { public int N; public string Kind, Key; public void Later(string kind, string key, int kv) { N++; Kind = kind; Key = key; } }
