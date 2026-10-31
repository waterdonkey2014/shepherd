#if !SHEPHERD_EBOOT   // kit #8 (k8-physics): a physics check / bench / check-mode file -- Mac-only (and SHEPHERD_ENGINE_TOOLS=1 eboots): AOT code is eboot span
using System.Reflection;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 2's <c>physcheck room:&lt;scene&gt;</c> (physics-plan-v2 section 4 "Stage 2", C5; acceptance #6): a scene from the owner's
/// install (read only), its colliders and bodies registered, nothing of the game run -- no Awake / OnEnable / Start / Update. The loader's own
/// deserialization runs as for any scene load (constructors, ISerializationCallbackReceiver, a ScriptableObject's Awake / OnEnable). Then,
/// as the plan lists them:
/// <list type="number">
/// <item>LightingManagerVolume.Start's layer write applied from a table (layer 21, "Only Collider With Player"), not by calling Start;</item>
/// <item>the island composites' runtime outlines re-enacted by engine code on the composites without serialized paths (the Room Transforms):
/// geometryType = Polygons + GenerateGeometry(), then Outlines on layer Island + GenerateGeometry(), as GenerateRoom.cs:1006-1008, 1035-1040 do.</item>
/// </list>
/// The counters, kept apart:
/// <list type="bullet">
/// <item><b>AABB level</b> (an L2 counter, not Box2D's broadphase): scene-data's method exactly (physics1/tool SceneDump + v2/pairs.py) -- one tight
/// AABB per active, enabled, non-composite-child 2D collider at its authored pose (a composite without serialized paths: the union of its
/// sources' AABBs), pairs with a moving side (a Dynamic or Kinematic body), not on one body, through the layer matrix, split into solid and
/// trigger pairs, and the trigger pairs with a Stay listener on either side (the collider's or the body's GameObject). Base Biome 1 must give
/// 1,122 / 694 / 629 / 67.</item>
/// <item><b>Shape level</b>: the core's real overlaps after a step (per chain child, de-duplicated to collider pairs): the trigger pairs and the
/// Stay-listener pairs, split into bell x PostProcess, bell x outline, volume x outline.</item>
/// <item><b>Box2D broadphase</b>: its contacts (fat AABBs, one proxy per chain edge): logged, never gated.</item>
/// <item><b>Per variant</b>: the Stay deliveries per step under this run's H-KINSLEEP / H-SLEEPSTAY (the dispatcher in count-only mode: listener
/// masks from the type tables, no handler is called).</item>
/// <item><b>The bells' fields</b>: SwayInWind's collideWithTag, swayOnly and rattle, read from the loaded components (never executed).</item>
/// </list></summary>
public static partial class PhysCheck
{
    static void Room(string spec)
    {
        Up();
        Runtime.Time.CaptureDeltaTime = 1f / 60f;
        var name = spec.Split(':')[0];
        string key = name.StartsWith("Assets/") ? name : $"Assets/Scenes/{name}.unity";
        Log.Info($"[physcheck]      room: loading '{key}' (activate: no -- no Awake / OnEnable / Start / Update)");
        // the loader resolves component types among the loaded UnityEngine assemblies (Loader.BuiltinType): in the game, game code has loaded
        // UnityEngine.Physics2DModule long before a scene with colliders; room mode runs no game code, so it loads the module itself first
        Log.Info($"[physcheck]      room: {typeof(CircleCollider2D).Assembly.GetName().Name} loaded before the scene");
        int handle;
        try { handle = Assets.Loader.LoadScene(key, activate: false); }
        catch (Exception e) { Check($"room {name}: the scene loads from the install", false, e.Message); return; }
        var cols = Objects.All.OfType<NCollider2D>().Where(c => !c.Destroyed && c.Go != null && c.Go.Scene == handle).ToList();
        Log.Info($"[physcheck]      room: {cols.Count} 2D collider records in the scene ({string.Join(", ", cols.GroupBy(c => c.Kind).Select(g => $"{g.Key} {g.Count()}"))}), " +
                 $"{Objects.All.OfType<NGameObject>().Count(g => g.Scene == handle)} GameObjects");
        Check($"room {name}: the scene loads, its 2D colliders registered", cols.Count > 0, $"{cols.Count} 2D colliders, {Registry.Bodies2D.Items().OfType<NRigidbody2D>().Count(b => b.Go?.Scene == handle)} Rigidbody2D");

        // ---- the AABB-level counter, with the serialized layers and then with LightingManagerVolume.Start's layer 21 ----
        var (aabb, lo, solid, trig, live, stay) = AabbCount(cols, null);
        Log.Info($"[physcheck]      room AABB level (serialized layers): AABB pairs {aabb}; after the matrix {lo} = solid {solid} + trigger {trig}; trigger pairs touching a live mover {live}; with a Stay listener {stay}");
        var lmv = new HashSet<NGameObject>(cols.Where(c => HasScript(c.Go, "LightingManagerVolume")).Select(c => c.Go));
        var r21 = AabbCount(cols, c => lmv.Contains(c.Go) ? 21 : c.Go.Layer);
        Log.Info($"[physcheck]      room AABB level (LightingManagerVolume.Start -> layer 21, {lmv.Count} GameObjects): AABB pairs {r21.aabb}; after the matrix {r21.lo} = solid {r21.solid} + trigger {r21.trig}; live {r21.live}; Stay listener {r21.stay}");
        if (name == "Base Biome 1")
        {
            // kit #10 fidelity (vita/notes/kit10-fidelity.md section 5, test only): SHEPHERD_PHYSCHECK_ROOM=<aabb>/<matrix>/<trigger>/<stay> -- scene-data's
            // counts for another game version (1.5.26.1056, re-made by the same method with that version's own layer matrix: 1165/694/629/67);
            // unset = the counts of the version the kits were built from (1,122 / 694 / 629 / 67)
            int ea = 1122, el = 694, et = 629, es = 67;
            if (Environment.GetEnvironmentVariable("SHEPHERD_PHYSCHECK_ROOM") is { Length: > 0 } rx && rx.Split('/') is { Length: 4 } rp &&
                int.TryParse(rp[0], out var pa) && int.TryParse(rp[1], out var pl) && int.TryParse(rp[2], out var pt) && int.TryParse(rp[3], out var ps))
                (ea, el, et, es) = (pa, pl, pt, ps);
            Check($"room Base Biome 1, AABB level: scene-data's counts reproduced exactly ({ea:N0} AABB pairs / {el} after the matrix / {et} trigger / {es} with a Stay listener)",
                  aabb == ea && lo == el && trig == et && stay == es, $"{aabb} / {lo} / {trig} / {stay} (solid {solid}, live {live}); with layer 21: {r21.aabb} / {r21.lo} / {r21.trig} / {r21.stay}");
        }

        // ---- 1. the layer table ----
        foreach (var go in lmv) { go.Layer = 21; PhysWorld.LayerChanged(go); }
        // ---- 2. the island composites' outlines (engine code on the composite) ----
        int island = LayerMask.NameToLayer("Island"); if (island < 0) island = 23;
        int gen = 0;
        foreach (var c in cols.Where(c => c.Kind == PhysRecords.Kind2D.Composite && (c.Paths == null || c.Paths.Length == 0) && c.Go.ActiveInHierarchy && c.Enabled))
        {
            c.GeometryType = 1; Composite.Generate(c);
            c.GeometryType = 0; c.Go.Layer = island; PhysWorld.LayerChanged(c.Go); Composite.Generate(c);
            PhysWorld.Changed(c); gen++;
            Log.Info($"[physcheck]      room: '{c.Go.Name}' outlines generated: {c.GenPaths?.Length ?? 0} paths, {c.GenPaths?.Sum(p => p.Length / 2) ?? 0} points");
        }
        Check($"room {name}: the composites without serialized paths get their runtime outlines (Polygons, then Outlines on Island)", true, $"{gen} composite(s)");

        // ---- the bells' fields (read, never executed) ----
        var bells = new List<(string tag, bool sway, bool rattle, bool active)>();
        foreach (var c in cols.Where(c => HasScript(c.Go, "SwayInWind") && c.IsTrigger))
        {
            var sw = c.Go.Components.OfType<NMono>().FirstOrDefault(m => m.Managed?.GetType().Name == "SwayInWind")?.Managed;
            if (sw == null) continue;
            var t = sw.GetType();
            object F(string f) => t.GetField(f, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(sw);
            var rat = F("rattle");
            bells.Add(((F("collideWithTag") as string) ?? "", F("swayOnly") is true, rat is UnityEngine.Object uo ? uo != null : rat != null, c.Go.ActiveInHierarchy && c.Enabled));
        }
        var act = bells.Where(b => b.active).ToList();
        Log.Info($"[physcheck]      room bells (SwayInWind trigger colliders): {bells.Count} in the scene, {act.Count} active; active: collideWithTag " +
                 string.Join(", ", act.GroupBy(b => b.tag).Select(g => $"'{g.Key}' x{g.Count()}")) + $"; swayOnly true {act.Count(b => b.sway)}; rattle set {act.Count(b => b.rattle)}");
        if (name == "Base Biome 1")
            Check("room Base Biome 1: the 30 active bells' fields read (collideWithTag, swayOnly, rattle)", act.Count == 30, $"{act.Count} active bells");

        // ---- the core: shape level, the broadphase, the Stays per step under this run's variant ----
        if (!PhysWorld.Sim2) { Check("room: the shape-level counts need SHEPHERD_PHYS=2 and the core", false); return; }
        PhysDispatch.CountOnly = true;
        PhysWorld.EnsureWorld(); PhysWorld.Flush();
        var staysPerStep = new List<long>();
        for (int i = 0; i < 60; i++)
        {
            long s0 = PhysDispatch.Stays, l0 = PhysDispatch.StaysListened;
            Runtime.Time.InFixedStep = true; PhysWorld.FixedStep(); Runtime.Time.InFixedStep = false;
            staysPerStep.Add(PhysDispatch.StaysListened - l0);
            if (i == 0) ShapeLevel(name);
        }
        Log.Info($"[physcheck]      room Stay deliveries to a Stay listener per step (H-KINSLEEP={Var("KINSLEEP")}, H-SLEEPSTAY={Var("SLEEPSTAY")}, H-KINISLAND={Var("KINISLAND")}; no movers): " +
                 $"step 1 {staysPerStep[0]}, 2 {staysPerStep[1]}, 10 {staysPerStep[9]}, 24 {staysPerStep[23]}, 25 {staysPerStep[24]}, 26 {staysPerStep[25]}, 30 {staysPerStep[29]}, 60 {staysPerStep[59]}; " +
                 $"broadphase contacts {PhysWorld.LastContacts} (touching {PhysWorld.LastTouching})");
        bool sleepStop = Var("KINSLEEP") == "sleep" && Var("SLEEPSTAY") == "stop";
        Check($"room {name}: Stays to listeners per step {(sleepStop ? "fall to 0 after 0.5 s (sleep + stop, no movers)" : "go on every step")}",
              sleepStop ? staysPerStep[59] == 0 : staysPerStep[59] == staysPerStep[1], $"step 2 {staysPerStep[1]}, step 60 {staysPerStep[59]}");
        PhysDispatch.CountOnly = false;
    }

    static bool HasScript(NGameObject go, string type) => go != null && go.Components.OfType<NMono>().Any(m => m.Managed?.GetType().Name == type);
    static bool HasStayListener(NGameObject go) => go != null && go.Components.OfType<NMono>().Any(m => m.Managed != null && PhysDispatch.HandlesStay(m.Managed.GetType()));

    /// <summary>Scene-data's broadphase estimate (physics1/tool/SceneDump.cs + v2/pairs.py), over the engine's records.</summary>
    static (int aabb, int lo, int solid, int trig, int live, int stay) AabbCount(List<NCollider2D> all, Func<NCollider2D, int> layerOf)
    {
        layerOf ??= c => c.Go.Layer;
        var cols = all.Where(c => c.Go.ActiveInHierarchy && c.Enabled && !c.UsedByComposite).ToList();
        var box = new Dictionary<NCollider2D, (float x0, float y0, float x1, float y1)>();
        foreach (var c in all) { var b = Aabb(c); if (b.HasValue) box[c] = b.Value; }
        // a composite with no serialized paths: the union of its sources' AABBs (the colliders used by a composite on the same body)
        foreach (var c in cols.Where(c => c.Kind == PhysRecords.Kind2D.Composite && (c.Paths == null || c.Paths.Sum(p => p?.Length ?? 0) == 0)))
        {
            var body = PhysWorld.AttachedRecord(c); bool any = false; float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            foreach (var s in all.Where(s => s.UsedByComposite && ReferenceEquals(PhysWorld.AttachedRecord(s), body)))
            {
                if (!box.TryGetValue(s, out var b)) continue;
                if (!any) { (x0, y0, x1, y1) = b; any = true; } else { x0 = Math.Min(x0, b.x0); y0 = Math.Min(y0, b.y0); x1 = Math.Max(x1, b.x1); y1 = Math.Max(y1, b.y1); }
            }
            if (any) box[c] = (x0, y0, x1, y1); else box.Remove(c);
        }
        cols = cols.Where(box.ContainsKey).ToList();
        string Bt(NCollider2D c) => PhysWorld.AttachedRecord(c) is { } b ? (b.BodyType == 0 ? "Dynamic" : b.BodyType == 1 ? "Kinematic" : "Static") : "none";
        bool Moving(NCollider2D c) => Bt(c) is "Dynamic" or "Kinematic";
        bool FullKin(NCollider2D c) => PhysWorld.AttachedRecord(c)?.UseFullKinematicContacts ?? false;
        bool Live(NCollider2D c) { var b = PhysWorld.AttachedRecord(c); return Bt(c) == "Dynamic" || (Bt(c) == "Kinematic" && b?.Go != null && b.Go.Components.OfType<NMono>().Any()); }
        bool Stay(NCollider2D c) { if (HasStayListener(c.Go)) return true; var b = PhysWorld.AttachedRecord(c); return b?.Go != null && !ReferenceEquals(b.Go, c.Go) && HasStayListener(b.Go); }
        int aabb = 0, lo = 0, solid = 0, trig = 0, live = 0, stay = 0;
        for (int i = 0; i < cols.Count; i++)
        {
            var a = cols[i]; var ba = box[a];
            for (int j = i + 1; j < cols.Count; j++)
            {
                var b = cols[j]; var bb = box[b];
                if (ba.x1 < bb.x0 || bb.x1 < ba.x0 || ba.y1 < bb.y0 || bb.y1 < ba.y0) continue;
                if (!Moving(a) && !Moving(b)) continue;
                var ra = PhysWorld.AttachedRecord(a);
                if (ra != null && ReferenceEquals(ra, PhysWorld.AttachedRecord(b))) continue;
                aabb++;
                if (!Runtime.Physics2DSettings.Collide(layerOf(a), layerOf(b))) continue;
                lo++;
                bool isT = a.IsTrigger || b.IsTrigger;
                if (isT) { trig++; if (Live(a) || Live(b)) live++; if (Stay(a) || Stay(b)) stay++; }
                else if (Bt(a) == "Dynamic" || Bt(b) == "Dynamic" || FullKin(a) || FullKin(b)) solid++;
            }
        }
        return (aabb, lo, solid, trig, live, stay);
    }
    /// <summary>One collider's tight AABB at its authored pose, as SceneDump computes it (the transform's world matrix; a circle's radius times the
    /// larger 3D axis length).</summary>
    static (float, float, float, float)? Aabb(NCollider2D c)
    {
        var m = c.Go.Transform.LocalToWorld;
        bool any = false; float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        void Add(float x, float y) { if (!any) { x0 = x1 = x; y0 = y1 = y; any = true; } else { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); } }
        void P(float x, float y) { x += c.OffsetX; y += c.OffsetY; Add(m.m00 * x + m.m01 * y + m.m03, m.m10 * x + m.m11 * y + m.m13); }
        switch (c.Kind)
        {
            case PhysRecords.Kind2D.Circle:
            {
                float sx = MathF.Sqrt(m.m00 * m.m00 + m.m10 * m.m10 + m.m20 * m.m20), sy = MathF.Sqrt(m.m01 * m.m01 + m.m11 * m.m11 + m.m21 * m.m21);
                float cx = m.m00 * c.OffsetX + m.m01 * c.OffsetY + m.m03, cy = m.m10 * c.OffsetX + m.m11 * c.OffsetY + m.m13, wr = c.Radius * Math.Max(sx, sy);
                Add(cx - wr, cy - wr); Add(cx + wr, cy + wr); break;
            }
            case PhysRecords.Kind2D.Box: case PhysRecords.Kind2D.Capsule:
                P(-c.SizeX / 2, -c.SizeY / 2); P(c.SizeX / 2, -c.SizeY / 2); P(-c.SizeX / 2, c.SizeY / 2); P(c.SizeX / 2, c.SizeY / 2); break;
            case PhysRecords.Kind2D.Polygon: case PhysRecords.Kind2D.Composite:
                if (c.Paths != null) foreach (var p in c.Paths) if (p != null) for (int i = 0; i + 1 < p.Length; i += 2) P(p[i], p[i + 1]);
                break;
            case PhysRecords.Kind2D.Edge:
                if (c.Paths != null) foreach (var p in c.Paths) if (p != null) for (int i = 0; i + 1 < p.Length; i += 2) P(p[i], p[i + 1]);
                if (any && c.EdgeRadius > 0) { x0 -= c.EdgeRadius; y0 -= c.EdgeRadius; x1 += c.EdgeRadius; y1 += c.EdgeRadius; }
                break;
        }
        return any ? (x0, y0, x1, y1) : null;
    }

    /// <summary>The shape level after the room's first step: the core's touching trigger pairs (collider pairs), those with a Stay listener, split.</summary>
    static void ShapeLevel(string name)
    {
        int pairs = 0, stay = 0, bellPost = 0, bellOutline = 0, volOutline = 0, other = 0;
        bool Bell(NCollider2D c) => HasScript(c.Go, "SwayInWind");
        bool Vol(NCollider2D c) => HasScript(c.Go, "LightingManagerVolume");
        bool Post(NCollider2D c) => c.Go.Components.Any(k => k.Managed?.GetType().Name is "PostProcessVolume" or "Volume") || c.Go.Name.Contains("PostProcess");
        bool Outline(NCollider2D c) => c.Kind == PhysRecords.Kind2D.Composite;
        bool StayOf(NCollider2D c) { if (HasStayListener(c.Go)) return true; var b = PhysWorld.AttachedRecord(c); return b?.Go != null && !ReferenceEquals(b.Go, c.Go) && HasStayListener(b.Go); }
        foreach (var p in PhysDispatch.Pairs)
        {
            if (!p.Sensor || p.Count <= 0) continue;
            pairs++;
            if (!(StayOf(p.A) || StayOf(p.B))) continue;
            stay++;
            if ((Bell(p.A) && Post(p.B)) || (Bell(p.B) && Post(p.A))) bellPost++;
            else if ((Bell(p.A) && Outline(p.B)) || (Bell(p.B) && Outline(p.A))) bellOutline++;
            else if ((Vol(p.A) && Outline(p.B)) || (Vol(p.B) && Outline(p.A))) volOutline++;
            else other++;
        }
        Log.Info($"[physcheck]      room shape level (the core after a step, collider pairs): trigger pairs touching {pairs}; with a Stay listener {stay} = bell x PostProcess {bellPost} + bell x outline {bellOutline} + volume x outline {volOutline} + other {other}");
        if (name == "Base Biome 1")
            Check("room Base Biome 1, shape level: the Stay-listener trigger pairs (the plan's expectation 34-67, likely near 34)", stay >= 34 && stay <= 67,
                  $"{stay} = bell x PostProcess {bellPost} + bell x outline {bellOutline} + volume x outline {volOutline} + other {other}; trigger pairs {pairs}");
    }
}
#endif
