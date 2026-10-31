#if !SHEPHERD_EBOOT   // kit #8 (k8-physics): a physics check / bench / check-mode file -- Mac-only (and SHEPHERD_ENGINE_TOOLS=1 eboots): AOT code is eboot span
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Physics;

/// <summary>Physics stage 0 (physics-plan-v2 sections 4 and 5, tier T2): <c>physcheck [all|settings|abi|unit]</c> and <c>physbench</c>
/// (Physics/PhysBench.cs), dispatched next to vitatest / rendertest / trigtest (Boot.cs). Silent: neither boots the game (no RuntimeInit,
/// so FMOD is never initialised) and SHEPHERD_AUDIO_OUT=null is set for anything that might look. Exit code = failed checks.
/// <list type="bullet">
/// <item><c>settings</c>: the settings self-test (Physics/PhysSettings.cs): TimeManager, Physics2DSettings, PhysicsManager decoded from the
/// owner's install at run time; structural invariants; the engine's fixed-step constants; Box2D's constants through sp_settings_check
/// (exactly one mismatch expected: maxRotationSpeed, H-MAXROT).</item>
/// <item><c>abi</c>: the core binds (dylib on the Mac 64-bit engine, or the host's shepphys_proc) and its handshake passes; the pool's header
/// is read through the one base pointer; pool alloc / free / stats; sp_nop; the trace writer (a file under the log folder).</item>
/// <item><c>unit</c>: the groundwork on synthetic objects: typed records with Unity's defaults for runtime AddComponent; PhysInterest /
/// PhysDesc through add, reparent, destroy and Instantiate (against a recount); the corrected dirty hook (the written transform itself is
/// exempt, its collider children are not); the typed state copied by Instantiate; the hooks' call counts (SHEPHERD_CHECK_PHYS=hooks);
/// the loop slots' counts at SHEPHERD_CAPTURE_DT=1/30 pacing; the AutoProps assert; SHEPHERD_CAPTURE_DT's parser.</item>
/// </list>
/// No method here names a Unity physics type in its signature (IcallRegistry's start-up scan of every static method would load the
/// physics modules early -- Diag/TriggerTest.cs).</summary>
[ProfileOnly]   // test text only; reads no property-bag key (Assets/Diet.cs)
public static partial class PhysCheck
{
    public static bool Handles(string mode) => mode is "physcheck" or "physbench";
    static int _pass, _fail;
    static void Check(string what, bool ok, string detail = null)
    {
        if (ok) _pass++; else _fail++;
        Log.Info($"[physcheck] {(ok ? "PASS" : "FAIL")} {what}{(detail != null ? " -- " + detail : "")}");
    }

    public static int Run(string[] args)
    {
        Environment.SetEnvironmentVariable("SHEPHERD_AUDIO_OUT", "null");   // silent, whatever might look (nothing here initialises FMOD)
        if (args[0] == "physbench") return PhysBench.Run(args);
        var what = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "all";   // (physics Stage 2: "room:Base Biome 1" may arrive split at its spaces)
        Log.Info(PhysConfig.Describe());
        if (!PhysConfig.On) { Log.Error("[physcheck] SHEPHERD_PHYS=none: the stage 0 groundwork is off; nothing to check"); return 1; }
        Runtime.Time.CaptureDeltaTime = 1f / 60f;
        if (what is "all" or "settings")
        {
            Assets.Loader.Init();
            int f = PhysSettings.SelfTest((ok, line) => Log.Info($"[physcheck] {(ok ? "    " : "FAIL")} {line}"));
            Check("settings self-test", f == 0, $"{f} failed check(s)");
        }
        if (what is "all" or "abi") Abi();
        if (what is "all" or "unit") Unit();
        // physics Stage 1 (Physics/PhysCheck1.cs): at SHEPHERD_PHYS=1 the unit scenes, render60 and the composite oracle
        if (PhysConfig.Level >= 1 && what is "all" or "unit1") Unit1();
        if (PhysConfig.Level >= 1 && what is "all" or "render60") Render60();
        if (what is "all" or "composite") CompositeOracle();
        // physics Stage 2 (Physics/PhysCheck2.cs, PhysRoom.cs): at SHEPHERD_PHYS=2 the trigger / dispatcher scenes and the query contract
        if (PhysConfig.Level >= 2 && what is "all" or "unit2") Unit2();
        if (PhysConfig.Level >= 2 && what is "all" or "queries") Queries();
        if (PhysConfig.Level < 2 && what is "unit2" or "queries") Check($"physcheck {what} needs SHEPHERD_PHYS=2", false);
        // physics Stage 3 (Physics/PhysCheck3.cs): at SHEPHERD_PHYS=3 the combat scenes (dynamics, collisions + Collision2D, the child bodies, the mirrored swipe)
        if (PhysConfig.Level >= 3 && what is "all" or "unit3") Unit3();
        if (PhysConfig.Level < 3 && what is "unit3") Check($"physcheck {what} needs SHEPHERD_PHYS=3", false);
        // physics Stage 4 step 4a (Physics/PhysCheck4.cs): at SHEPHERD_PHYS=4 (and 6) the path icalls against the Unity 2022.3 oracle's numbers
        if (PhysConfig.Level >= 4 && PhysWorld.Sim4 && what is "all" or "unit4") Unit4();
        if (PhysConfig.Level >= 4 && PhysWorld.Sim4 && what is "all" or "unit4b") Unit4b();   // Stage 4b: H-STARTVTX / H-DECOMP against the oracle, bit for bit
        if (PhysConfig.Level >= 4 && PhysWorld.Sim4 && what is "all" or "unit4br") Unit4bReview();   // the 4b review: body frames, Synchronous composites on moving bodies, the memo, useDelaunayMesh
        if (PhysConfig.Level < 4 && what is "unit4" or "unit4b" or "unit4br") Check($"physcheck {what} needs SHEPHERD_PHYS=4", false);
        if (what.StartsWith("room:")) Room(what[5..]);
        // physics Stage 6 (Physics/PhysCheck6.cs): at SHEPHERD_PHYS=6 the island-ground ray through the Unity API; room3d on the game's scenes
        if (PhysConfig.Level >= 6 && PhysWorld.Sim6 && what is "all" or "unit6") Unit6();
        if (PhysConfig.Level < 6 && what is "unit6") Check($"physcheck {what} needs SHEPHERD_PHYS=6", false);
        if (what.StartsWith("room3d:")) { if (PhysWorld.Sim6) Room3D(what[7..]); else Check($"physcheck {what} needs SHEPHERD_PHYS=6 with a core", false); }
        if (PhysConfig.Level < 1 && what is "unit1" or "render60") Check($"physcheck {what} needs SHEPHERD_PHYS=1", false);
        Log.Info($"[physcheck] RESULT {(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED")}: {_pass} passed, {_fail} failed");
        return _fail;
    }

    // ---------------- abi: the native core from the engine ----------------
    static void Abi()
    {
        var core = ShepPhys.Core;
        Check($"libshepphys binds and the ABI handshake passes (version {ShepPhys.AbiVersion}, {ShepPhys.AbiSizes} struct sizes, float probe 51.75)", core != null, ShepPhys.Status);
        if (core == null) return;
        int initFlags = Environment.OSVersion.Platform == PlatformID.Unix ? ShepPhys.InitPoolGrow : 0;
        int r = PhysConfig.Workers > 0 ? core.InitWorkers(PhysConfig.PoolMb << 20, initFlags, PhysConfig.Workers) : core.Init(PhysConfig.PoolMb << 20, initFlags);   // kit #9: the worker pool too
        Check($"sp_init: a {PhysConfig.PoolMb} MiB pool", r == 0, $"rc {r}");
        var h = ((ShepPhys)core).ReadHeader();
        Check("the pool's header read through its one base pointer (magic, ABI version)", h.Magic == ShepPhys.HeaderMagic && h.AbiVersion == ShepPhys.AbiVersion,
              $"base 0x{core.PoolBase.ToInt64():x}, magic 0x{h.Magic:x8}, abi {h.AbiVersion}");
        int a = core.PoolAlloc(1000), b = core.PoolAlloc(64), fr = core.PoolFree(a), c = core.PoolAlloc(1000);
        core.PoolStats(out var st);
        Check("pool: offsets, LIFO reuse, stats", a > 0 && b > a && fr == 0 && c == a && st.LiveBlocks == 3 && st.Allocs == 4 && st.Frees == 1,
              $"offsets {a} {b}, freed {a} -> {c}; live {st.LiveBlocks}, used {st.Used} of {st.Bytes} B, high water {st.HighWater}");
        Check("sp_nop", core.Nop() == 0);
        var path = Path.Combine(Program.LogDir, "physcheck.sptr");
        int tb = core.TraceBegin(path, 0), tn = core.TraceNote(1, stackalloc int[] { 7, 8, 9 }), te = core.TraceEnd();
        long len = File.Exists(path) ? new FileInfo(path).Length : -1;
        Check("trace writer: begin, a note, end (the file holds the 128-byte header + one 28-byte record)", tb == 0 && tn == 0 && te == 1 && len == 128 + 8 + 20, $"rc {tb}/{tn}/{te}, {len} B at {path}");
        if (!PhysWorld.Sim) core.Shutdown();   // (Stage 1: the world's runtime stays up for the scenes after this)
    }

    // ---------------- unit: the groundwork on synthetic objects ----------------
    static NTransform T(GameObject g) => Objects.Get<NTransform>(g.transform);
    static NComponent C(Component c) => Objects.Get<NComponent>(c);
    static string Desc(params GameObject[] gs) => string.Join(" ", gs.Select(g => $"{g.name}:{T(g).PhysInterest}/{T(g).PhysDesc}"));

    static void Unit()
    {
        Runtime.Time.CaptureDeltaTime = 1f / 30f;
        // U1: typed records with Unity's defaults for a runtime AddComponent (plan 1.4)
        var host = new GameObject("U1");
        var rb = C(host.AddComponent<Rigidbody2D>()) as NRigidbody2D;
        var box = C(host.AddComponent<BoxCollider2D>()) as NCollider2D;
        var circle = C(host.AddComponent<CircleCollider2D>()) as NCollider2D;
        var poly = C(host.AddComponent<PolygonCollider2D>()) as NCollider2D;
        var box3 = C(host.AddComponent<BoxCollider>()) as NCollider3D;
        Check("runtime AddComponent<Rigidbody2D> -> NRigidbody2D with Unity's defaults (Dynamic, mass 1, gravity scale 1, drag 0, angular drag 0.05, simulated, " +
              "no interpolation, StartAwake, Discrete, no constraints)", rb != null && rb.BodyType == 0 && rb.Mass == 1f && rb.GravityScale == 1f && rb.LinearDrag == 0f &&
              rb.AngularDrag == 0.05f && rb.Simulated && rb.Interpolate == 0 && rb.SleepingMode == 1 && rb.CollisionDetection == 0 && rb.Constraints == 0 && !rb.FromData);
        Check("runtime AddComponent<BoxCollider2D> -> NCollider2D Box, size (1, 1), offset (0, 0), not a trigger, no material, density 1",
              box != null && box.Kind == PhysRecords.Kind2D.Box && box.SizeX == 1f && box.SizeY == 1f && box.OffsetX == 0f && box.OffsetY == 0f && !box.IsTrigger && box.Material == null && box.Density == 1f);
        Check("runtime AddComponent<CircleCollider2D> -> radius 0.5", circle != null && circle.Kind == PhysRecords.Kind2D.Circle && circle.Radius == 0.5f);
        Check("runtime AddComponent<PolygonCollider2D> -> a typed record, defaults logged as UNVERIFIED", poly != null && poly.Kind == PhysRecords.Kind2D.Polygon);
        Check("runtime AddComponent<BoxCollider> (3D) -> NCollider3D Box, size (1, 1, 1)", box3 != null && box3.Kind == PhysRecords.Kind3D.Box && box3.SizeX == 1f && box3.SizeZ == 1f);
        Check("the collider records are still Behaviours (Registry.Colliders2D, Triggers2D see them)", box is NBehaviour && Registry.Colliders2D.Items().Contains(box));
        int u1 = 3 | (PhysWorld.Sim6 ? 4 : 0);   // (physics Stage 6: the BoxCollider gives bit 4 at level 6)
        Check($"U1's transform: PhysInterest {u1} (body + 2D colliders{(u1 == 7 ? " + a 3D collider" : "")}), PhysDesc 1", T(host).PhysInterest == u1 && T(host).PhysDesc == 1, Desc(host));
        UObject.DestroyImmediate(host);

        // U2: PhysInterest / PhysDesc through add, reparent and destroy
        var a = new GameObject("A"); var b = new GameObject("B"); var c = new GameObject("C");
        b.transform.SetParent(a.transform, false); c.transform.SetParent(b.transform, false);
        b.AddComponent<Rigidbody2D>(); c.AddComponent<CircleCollider2D>();
        Check("A > B (body) > C (collider): interest 0 / 1 / 2, PhysDesc 2 / 2 / 1", T(a).PhysInterest == 0 && T(b).PhysInterest == 1 && T(c).PhysInterest == 2 &&
              T(a).PhysDesc == 2 && T(b).PhysDesc == 2 && T(c).PhysDesc == 1, Desc(a, b, c));
        c.transform.SetParent(a.transform, true);
        Check("C reparented to A: PhysDesc A 2, B 1, C 1", T(a).PhysDesc == 2 && T(b).PhysDesc == 1 && T(c).PhysDesc == 1, Desc(a, b, c));
        UObject.DestroyImmediate(c.GetComponent<CircleCollider2D>());
        Check("C's collider destroyed: C interest 0, A PhysDesc 1", T(c).PhysInterest == 0 && T(c).PhysDesc == 0 && T(a).PhysDesc == 1, Desc(a, b, c));
        c.AddComponent<CircleCollider2D>(); c.transform.SetParent(b.transform, false);
        // U3: the corrected dirty hook (plan 1.5): an interested transform's MarkChanged dirties it, except the one physics is writing; its
        // interested descendants are dirtied either way
        long d0 = PhysWorld.DirtyCalls;
        b.transform.position = new Vector3(1f, 2f, 0f);
        long d1 = PhysWorld.DirtyCalls;
        PhysWorld.WritingTransform = T(b);
        b.transform.position = new Vector3(3f, 2f, 0f);
        long d2 = PhysWorld.DirtyCalls;
        PhysWorld.WritingTransform = null;
        a.transform.position = new Vector3(0f, 1f, 0f);
        long d3 = PhysWorld.DirtyCalls;
        Check("dirty hook: a write to B dirties B and its collider child C (2); while B is the written transform only C (1); a write to the " +
              "uninterested parent A dirties B and C (2)", d1 - d0 == 2 && d2 - d1 == 1 && d3 - d2 == 2, $"{d1 - d0} / {d2 - d1} / {d3 - d2}");
        // U3b (kit #9, physics-plan-v2 Stage 7 item 4): the lazy hasChanged against an eager model -- position writes (MarkChanged: the transform
        // and its subtree), hasChanged = true / false (one transform), and parent changes WITHOUT a mark (the loader's way: Native/Objects.cs
        // rebases) -- 3,000 seeded steps over a 12-transform tree; every transform compared after every step (with SHEPHERD_LAZY_HASCHANGED=0
        // the same test checks the eager flags)
        {
            var hg = new GameObject[12];
            for (int i = 0; i < hg.Length; i++) { hg[i] = new GameObject("HC" + i); if (i > 0) hg[i].transform.SetParent(hg[(i - 1) / 2].transform, false); }
            var model = new Dictionary<NTransform, bool>();
            foreach (var g in hg) model[T(g)] = true;   // (new transforms start changed; SetParent marked the subtrees anyway)
            uint rng = 12345u;
            int Next(int n) { rng = rng * 1664525u + 1013904223u; return (int)((rng >> 8) % (uint)n); }
            void MarkModel(NTransform t) { model[t] = true; foreach (var ch in t.Children) MarkModel(ch); }
            int bad = 0, reparents = 0, writes = 0;
            for (int step = 0; step < 3000; step++)
            {
                int k = Next(hg.Length); var g = hg[k]; var t = T(g);
                switch (Next(4))
                {
                    case 0: g.transform.position = new Vector3(Next(100), 0f, 0f); MarkModel(t); break;
                    case 1: g.transform.hasChanged = true; model[t] = true; writes++; break;
                    case 2: g.transform.hasChanged = false; model[t] = false; writes++; break;
                    default:   // a parent change without a mark, inside the tree (the root stays the root)
                        if (k == 0) break;
                        var p = T(hg[Next(hg.Length)]);
                        if (p == t || p.IsChildOf(t) || t.Parent == p) break;
                        t.Parent.Children.Remove(t); t.Parent = p; p.Children.Add(t); reparents++;
                        break;
                }
                foreach (var h in hg) if (h.transform.hasChanged != model[T(h)]) bad++;
            }
            Check($"lazy hasChanged (kit #9) = the eager model: 3,000 seeded steps (position writes, hasChanged writes {writes}, parent changes without a mark {reparents}) over 12 transforms, every transform compared after each step",
                  bad == 0, $"{bad} mismatches ({(NTransform.HcLazy ? "lazy" : "eager")})");
            UObject.DestroyImmediate(hg[0]);
        }
        // U4: Instantiate copies the typed state and sets the clone's interest
        ((NRigidbody2D)C(b.GetComponent<Rigidbody2D>())).Mass = 3f;
        ((NCollider2D)C(c.GetComponent<CircleCollider2D>())).Radius = 0.25f;
        var clone = UObject.Instantiate(b);
        var crb = C(clone.GetComponent<Rigidbody2D>()) as NRigidbody2D;
        var cc = clone.transform.childCount > 0 ? C(clone.transform.GetChild(0).GetComponent<CircleCollider2D>()) as NCollider2D : null;
        Check("Instantiate: the clone's records carry the typed state (mass 3, radius 0.25) and interest (1 / 2, PhysDesc 2)",
              crb != null && crb.Mass == 3f && cc != null && cc.Radius == 0.25f && T(clone).PhysInterest == 1 && T(clone).PhysDesc == 2, Desc(clone));
        // U5: hooks (counted only under SHEPHERD_CHECK_PHYS=hooks)
        var hk = (long[])PhysWorld.HookCalls.Clone();
        b.layer = 21; c.GetComponent<CircleCollider2D>().enabled = false; c.GetComponent<CircleCollider2D>().enabled = true;
        b.SetActive(false); b.SetActive(true);
        long Dh(PhysWorld.Hook h) => PhysWorld.HookCalls[(int)h] - hk[(int)h];
        if (PhysConfig.CheckHooks)
            Check("hooks: layer 1, enable 2, deactivate 2 and activate 2 (B and C), add / destroy / reparent seen", Dh(PhysWorld.Hook.Layer) == 1 && Dh(PhysWorld.Hook.Enabled) == 2 &&
                  Dh(PhysWorld.Hook.Deactivated) == 2 && Dh(PhysWorld.Hook.Activated) == 2 && PhysWorld.HookCalls[(int)PhysWorld.Hook.Added] > 0 &&
                  PhysWorld.HookCalls[(int)PhysWorld.Hook.Destroying] > 0 && PhysWorld.HookCalls[(int)PhysWorld.Hook.Reparented] > 0,
                  string.Join(", ", Enum.GetNames(typeof(PhysWorld.Hook)).Select((n, i) => n + " " + PhysWorld.HookCalls[i])));
        else Log.Info("[physcheck]      hooks: not counted (run with SHEPHERD_CHECK_PHYS=hooks or all)");
        UObject.DestroyImmediate(clone);
        UObject.DestroyImmediate(b);
        Check("B's subtree destroyed: A PhysDesc 0", T(a).PhysDesc == 0, Desc(a));
        UObject.DestroyImmediate(a);
        // U6: the tree recount over everything alive (the same check SHEPHERD_CHECK_PHYS=tree runs every frame)
        var g1 = new GameObject("G1"); var g2 = new GameObject("G2"); g2.transform.SetParent(g1.transform, false); g2.AddComponent<BoxCollider2D>();
        int bi = 0, bd = 0;
        foreach (var o in Registry.Transforms.Items())
        {
            if (o is not NTransform t || t.Destroyed) continue;
            int want = 0; foreach (var k in t.Go.Components) if (!k.Destroyed) want |= PhysRecords.InterestOf(k);
            if (want != t.PhysInterest) bi++;
            int dsum = 0; foreach (var o2 in Registry.Transforms.Items()) if (o2 is NTransform u && !u.Destroyed && u.PhysInterest != 0 && u.IsChildOf(t)) dsum++;
            if (dsum != t.PhysDesc) bd++;
        }
        Check("tree recount: every live transform's PhysInterest and PhysDesc equal a recount from the components", bi == 0 && bd == 0, $"{bi} interest, {bd} desc mismatches");
        // U7: the loop slots at SHEPHERD_CAPTURE_DT pacing (1/30 s per frame): A and C once per frame, B once per 50 Hz step
        long sa = PhysWorld.SlotA, sb = PhysWorld.SlotB, sc = PhysWorld.SlotC; double fx = Runtime.Time.FixedNow;
        for (int i = 0; i < 30; i++) Lifecycle.Frame();
        long steps = (long)Math.Round((Runtime.Time.FixedNow - fx) / Runtime.Time.FixedDeltaTime);
        Check("loop slots over 30 frames of 1/30 s: A 30, C 30, B = the fixed steps taken (50 per second: 50)", PhysWorld.SlotA - sa == 30 && PhysWorld.SlotC - sc == 30 &&
              PhysWorld.SlotB - sb == steps && steps == 50, $"A {PhysWorld.SlotA - sa}, B {PhysWorld.SlotB - sb}, C {PhysWorld.SlotC - sc}, fixed steps {steps}");
        UObject.DestroyImmediate(g1);
        // U8: the AutoProps assert (Physics2DModule and PhysicsModule are loaded now)
        var (rows, icalls, bag, first) = PhysGuard.Check();
        Check("AutoProps assert: no icall of the loaded physics modules is bag-backed", rows > 0 && bag == 0,
              $"{rows} rows, {icalls} served by [Icall], {bag} bag-backed{(first != null ? " e.g. " + first : "")}; the guard kept {PhysGuard.Skipped} at registration");
        // U9: SHEPHERD_CAPTURE_DT's parser
        bool Same(float x, float y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y);
        Check("SHEPHERD_CAPTURE_DT: unset 1f/60f; 1/30, 1/20 and 0.05 exact; bad values and out of range fall back to 1f/60f",
              Same(Runtime.Time.ParseCaptureDt(null), 1f / 60f) && Same(Runtime.Time.ParseCaptureDt("1/30"), 1f / 30f) && Same(Runtime.Time.ParseCaptureDt("1/20"), 1f / 20f) &&
              Same(Runtime.Time.ParseCaptureDt("0.05"), 0.05f) && Same(Runtime.Time.ParseCaptureDt("bogus"), 1f / 60f) && Same(Runtime.Time.ParseCaptureDt("1/0"), 1f / 60f) &&
              Same(Runtime.Time.ParseCaptureDt("2"), 1f / 60f));
        Runtime.Time.ParseCaptureDt(Environment.GetEnvironmentVariable("SHEPHERD_CAPTURE_DT"));   // (the note back to this run's value)
    }
}
#endif
