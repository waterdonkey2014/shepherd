using System.Buffers.Binary;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Runtime;

/// <summary>fid1-fixup D3 (vita/notes/fidelity-round1.md, "Triggers2D"): the project's Physics 2D settings -- the layer collision
/// matrix and the callback / query flags -- read at run time from the owner's own <c>globalgamemanagers</c> (Physics2DSettings,
/// class 19). Nothing of the game's settings is written into the engine: they are data of the installed game, read like the
/// TagManager (Assets/Loader.cs), and Unity's defaults (every layer collides, the flags of a new project) apply when they cannot be
/// read. The file is stripped of type trees, so the record is parsed in Unity 2022.3's serialized layout and checked on the way
/// (vector length, flag bytes, padding, total size); anything else is "not this layout": the defaults, and one line says so.
///
/// Read on first use -- the first trigger pair candidate (Triggers2D) or a Physics2D settings icall -- so a boot that never has two
/// colliders (the menus) never reads it and its pack need not carry it. A strict pack without the record (the Vita) reports the
/// miss once and runs on the defaults.
///
/// The icalls below serve Physics2D's settings properties and its layer / collider ignore API from this state (the game reads
/// queriesHitTriggers and GetLayerCollisionMask for its queries; bosses call IgnoreCollision). Triggers2D applies the matrix, the
/// ignored pairs and callbacksOnDisable. Handover: the physics round's Box2D shim (vita/physics) should take its filter from here,
/// so exactly one place holds the project's settings.</summary>
[ProfileOnly]   // reads no property-bag keys (its literals are log text; Assets/Diet.cs)
public static class Physics2DSettings
{
    /// <summary>Per layer, the layers it collides with (bit b of [a]: a and b collide). Unity keeps it symmetric.</summary>
    static readonly uint[] _matrix = new uint[32];
    public static bool QueriesHitTriggers = true, QueriesStartInColliders = true, CallbacksOnDisable = true, ReuseCollisionCallbacks = true, AutoSyncTransforms;
    /// <summary>SimulationMode2D: 0 FixedUpdate, 1 Update, 2 Script.</summary>
    public static int SimulationMode;
    /// <summary>"project" (read from the game), "defaults" (not readable), or "not loaded".</summary>
    public static string Source { get; private set; } = "not loaded";
    static bool _loaded;

    public static void EnsureLoaded() { if (!_loaded) Load(); }
    /// <summary>Do layers a and b collide (Physics2D.GetIgnoreLayerCollision's negation)?</summary>
    public static bool Collide(int a, int b) { if (!_loaded) Load(); return ((_matrix[a & 31] >> (b & 31)) & 1) != 0; }
    /// <summary>Physics Stage 1: the 32 matrix rows for the core's Unity filter (Physics/PhysWorld.cs; re-sent when MatrixVersion moves).</summary>
    public static void CopyMatrix(uint[] dst) { if (!_loaded) Load(); Array.Copy(_matrix, dst, 32); }
    /// <summary>Bumped on every change of the matrix (Triggers2D re-tests its contacts then; nothing is cached on it today).</summary>
    public static int MatrixVersion { get; private set; }

    /// <summary>Physics stage 0 (physics-plan-v2 section 1.4, Physics/PhysSettings.cs): the rest of the record, decoded in the same pass (the
    /// floats Box2D's compiled constants are checked against, the iterations, gravity) -- read by the settings self-test and, from
    /// Stage 1, by the core. Unity's defaults until (and unless) the record is read.</summary>
    public struct Floats
    {
        public float GravityX, GravityY; public int VelocityIterations, PositionIterations;
        public float VelocityThreshold, MaxLinearCorrection, MaxAngularCorrection, MaxTranslationSpeed, MaxRotationSpeed;
        public float BaumgarteScale, BaumgarteTOIScale, TimeToSleep, LinearSleepTolerance, AngularSleepTolerance, DefaultContactOffset;
        public bool UseMultithreading, UseConsistencySorting;
        public int RecordBytes, AsymmetricEntries;
    }
    public static Floats Values = new()
    {
        GravityX = 0f, GravityY = -9.81f, VelocityIterations = 8, PositionIterations = 3, VelocityThreshold = 1f, MaxLinearCorrection = 0.2f,
        MaxAngularCorrection = 8f, MaxTranslationSpeed = 100f, MaxRotationSpeed = 360f, BaumgarteScale = 0.2f, BaumgarteTOIScale = 0.75f,
        TimeToSleep = 0.5f, LinearSleepTolerance = 0.01f, AngularSleepTolerance = 2f, DefaultContactOffset = 0.01f
    };   // (Unity 2022.3's project defaults, the documented ones -- not the game's)

    /// <summary>Kit #8 (k8-physics; vita/notes/kit8-physics.md): SHEPHERD_PHYS2D_SETTINGS=&lt;file&gt; -- the record's bytes exported from the owner's
    /// install (owner-local, never in git: engine/Shepherd.Assets/Shepherd.Assets.Cli phys2d &lt;file&gt;, staged by vita/shepherd/stage-owner.sh as data/physics2d-settings.bin).
    /// The Vita play apps' host sets it at physics level 1+ (vita/shepherd/phys_host.inc.c): their strict card pack, recorded with physics off,
    /// carries no Physics2DSettings (the menus never read it at level 0), and the core needs the project's gravity / iterations / matrix /
    /// clamps from its first step. Read first when the file is there and decodes (the same bytes, the same decoder: "project (file)"); absent
    /// or not in the layout: the pack / install as before (unset: exactly as before).</summary>
    static bool FromFile()
    {
        var path = Environment.GetEnvironmentVariable("SHEPHERD_PHYS2D_SETTINGS");
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            if (!File.Exists(path)) { Log.Warn($"[physics2d] SHEPHERD_PHYS2D_SETTINGS={path}: no such file (stage-owner.sh writes data/physics2d-settings.bin): the pack / install record"); return false; }
            var b = File.ReadAllBytes(path);
            if (!Decode(b, out var why)) { for (int i = 0; i < 32; i++) _matrix[i] = uint.MaxValue; Log.Warn($"[physics2d] {path} ({b.Length} B) is not a Physics2DSettings record in the 2022.3 layout ({why}): the pack / install record"); return false; }
            Source = "project (file)";
            int blocked = 0, asym = 0;
            for (int a = 0; a < 32; a++) for (int c = 0; c < 32; c++) { if (((_matrix[a] >> c) & 1) != ((_matrix[c] >> a) & 1)) asym++; if (c >= a && ((_matrix[a] >> c) & 1) == 0) blocked++; }
            Values.AsymmetricEntries = asym;
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b))[..16].ToLowerInvariant();
            Log.Info($"[physics2d] project settings from {path} ({b.Length} B, sha256 {sha}: the owner's install's record, exported): layer matrix {blocked} of 528 layer pairs blocked{(asym > 0 ? $", {asym} asymmetric entries" : "")}; " +
                     $"queriesHitTriggers {QueriesHitTriggers}, queriesStartInColliders {QueriesStartInColliders}, callbacksOnDisable {CallbacksOnDisable}, reuseCollisionCallbacks {ReuseCollisionCallbacks}, autoSyncTransforms {AutoSyncTransforms}, simulationMode {SimulationMode}");
            return true;
        }
        catch (Exception e) { for (int i = 0; i < 32; i++) _matrix[i] = uint.MaxValue; Log.Warn($"[physics2d] {path} unreadable ({e.Message}): the pack / install record"); return false; }
    }
    static bool _fileTried;
    /// <summary>Kit #8 review fix (vita/notes/kit8-physics.md "Review fixes"): the Vita host's start guard (Physics/PhysWorld.Stage1.cs
    /// <c>Sim</c>). Under SHEPHERD_PHYS_HOST=1 the core may start only on the project's own record, and on the card that is the owner file (the
    /// strict pack carries none): this reads SHEPHERD_PHYS2D_SETTINGS now -- the same <see cref="FromFile"/>, bytes and decoder, its log line
    /// earlier in engine.log -- and answers whether it decoded. Not decoded (unset, absent, another layout): false and nothing kept; the lazy
    /// Load later reads the pack / install exactly as before (without trying the file a second time).</summary>
    public static bool LoadFileNow()
    {
        if (_loaded) return Source == "project (file)";
        _fileTried = true;
        for (int i = 0; i < 32; i++) _matrix[i] = uint.MaxValue;
        if (!FromFile()) return false;
        _loaded = true;
        return true;
    }
    static void Load()
    {
        _loaded = true;
        for (int i = 0; i < 32; i++) _matrix[i] = uint.MaxValue;
        Source = "defaults";
        if (!_fileTried && FromFile()) return;   // (kit #8 review: LoadFileNow tried it already -- the same answer, one log line)
        try
        {
            var gm = Assets.Loader.Db?.GetFile("globalgamemanagers");
            var o = gm?.ObjectsOfClass(19).FirstOrDefault();
            if (o == null) { Log.Warn("[physics2d] no Physics2DSettings record in globalgamemanagers: Unity's defaults (every layer collides with every layer, callbacksOnDisable on)"); return; }
            var b = gm.GetObjectBytes(o);
            if (!Decode(b, out var why)) { Log.Warn($"[physics2d] Physics2DSettings ({b.Length} B, Unity {gm.UnityVersion}) is not in the 2022.3 layout ({why}): Unity's defaults (every layer collides, callbacksOnDisable on)"); return; }
            Source = "project";
            int asym = 0, blocked = 0;
            for (int a = 0; a < 32; a++) for (int c = 0; c < 32; c++) { if (((_matrix[a] >> c) & 1) != ((_matrix[c] >> a) & 1)) asym++; if (c >= a && ((_matrix[a] >> c) & 1) == 0) blocked++; }
            Values.AsymmetricEntries = asym;
            Log.Info($"[physics2d] project settings (globalgamemanagers #{o.PathId}, Unity {gm.UnityVersion}): layer matrix {blocked} of 528 layer pairs blocked{(asym > 0 ? $", {asym} asymmetric entries" : "")}; " +
                     $"queriesHitTriggers {QueriesHitTriggers}, queriesStartInColliders {QueriesStartInColliders}, callbacksOnDisable {CallbacksOnDisable}, reuseCollisionCallbacks {ReuseCollisionCallbacks}, autoSyncTransforms {AutoSyncTransforms}, simulationMode {SimulationMode}");
        }
        catch (Exception e) { for (int i = 0; i < 32; i++) _matrix[i] = uint.MaxValue; Source = "defaults"; Log.Warn($"[physics2d] Physics2DSettings unreadable ({e.Message}): Unity's defaults"); }
    }

    /// <summary>Unity 2022.3's Physics2DSettings, in serialized order: m_Gravity (Vector2f, 8) | m_DefaultMaterial (PPtr: int32 + int64,
    /// 12) | m_VelocityIterations, m_PositionIterations (int32 x2) | 11 floats (m_VelocityThreshold .. m_DefaultContactOffset) |
    /// m_JobOptions (2 bools, aligned to 4, then 15 int32) | m_SimulationMode (int32) at 136 | m_QueriesHitTriggers,
    /// m_QueriesStartInColliders, m_CallbacksOnDisable, m_ReuseCollisionCallbacks, m_AutoSyncTransforms (5 bools) at 140, aligned to 4 |
    /// m_LayerCollisionMatrix (vector&lt;UInt32&gt;: int32 count = 32, then the masks) at 148; 280 bytes in all. Little-endian.</summary>
    static bool Decode(ReadOnlySpan<byte> b, out string why)
    {
        const int Mode = 136, Flags = 140, Vec = 148;
        if (b.Length < Vec + 4) { why = "too short"; return false; }
        int n = BinaryPrimitives.ReadInt32LittleEndian(b[Vec..]);
        if (n != 32) { why = $"matrix length {n}"; return false; }
        if (b.Length != Vec + 4 + 4 * n) { why = $"size {b.Length}, expected {Vec + 4 + 4 * n}"; return false; }
        for (int i = 0; i < 5; i++) if (b[Flags + i] > 1) { why = $"flag byte {i} = {b[Flags + i]}"; return false; }
        for (int i = 5; i < 8; i++) if (b[Flags + i] != 0) { why = "flag padding not zero"; return false; }
        int mode = BinaryPrimitives.ReadInt32LittleEndian(b[Mode..]);
        if (mode is < 0 or > 2) { why = $"simulation mode {mode}"; return false; }
        SimulationMode = mode;
        QueriesHitTriggers = b[Flags] != 0; QueriesStartInColliders = b[Flags + 1] != 0; CallbacksOnDisable = b[Flags + 2] != 0;
        ReuseCollisionCallbacks = b[Flags + 3] != 0; AutoSyncTransforms = b[Flags + 4] != 0;
        for (int i = 0; i < 32; i++) _matrix[i] = BinaryPrimitives.ReadUInt32LittleEndian(b[(Vec + 4 + 4 * i)..]);
        // physics stage 0: the head of the record -- m_Gravity at 0, m_DefaultMaterial (PPtr, 12 B) at 8, the iterations at 20 / 24,
        // the 11 floats from 28 (velocityThreshold .. defaultContactOffset, serialized order), m_JobOptions' two bools at 72
        static float Fl(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b[at..]);
        Values.GravityX = Fl(b, 0); Values.GravityY = Fl(b, 4);
        Values.VelocityIterations = BinaryPrimitives.ReadInt32LittleEndian(b[20..]); Values.PositionIterations = BinaryPrimitives.ReadInt32LittleEndian(b[24..]);
        Values.VelocityThreshold = Fl(b, 28); Values.MaxLinearCorrection = Fl(b, 32); Values.MaxAngularCorrection = Fl(b, 36);
        Values.MaxTranslationSpeed = Fl(b, 40); Values.MaxRotationSpeed = Fl(b, 44); Values.BaumgarteScale = Fl(b, 48); Values.BaumgarteTOIScale = Fl(b, 52);
        Values.TimeToSleep = Fl(b, 56); Values.LinearSleepTolerance = Fl(b, 60); Values.AngularSleepTolerance = Fl(b, 64); Values.DefaultContactOffset = Fl(b, 68);
        Values.UseMultithreading = b[72] != 0; Values.UseConsistencySorting = b[73] != 0;
        Values.RecordBytes = b.Length;
        why = null; return true;
    }

    static void SetPair(int a, int b, bool collide)
    {
        a &= 31; b &= 31;
        if (collide) { _matrix[a] |= 1u << b; _matrix[b] |= 1u << a; } else { _matrix[a] &= ~(1u << b); _matrix[b] &= ~(1u << a); }
        MatrixVersion++;
    }

    // ---- Physics2D's settings properties and layer API (static icalls: a[] are the arguments) ----
    [Icall("UnityEngine.Physics2D::get_queriesHitTriggers")] static object GetQht(object[] a) { EnsureLoaded(); return QueriesHitTriggers; }
    [Icall("UnityEngine.Physics2D::set_queriesHitTriggers")] static object SetQht(object[] a) { EnsureLoaded(); QueriesHitTriggers = (bool)a[0]; return null; }
    [Icall("UnityEngine.Physics2D::get_queriesStartInColliders")] static object GetQsic(object[] a) { EnsureLoaded(); return QueriesStartInColliders; }
    [Icall("UnityEngine.Physics2D::set_queriesStartInColliders")] static object SetQsic(object[] a) { EnsureLoaded(); QueriesStartInColliders = (bool)a[0]; return null; }
    [Icall("UnityEngine.Physics2D::get_callbacksOnDisable")] static object GetCod(object[] a) { EnsureLoaded(); return CallbacksOnDisable; }
    [Icall("UnityEngine.Physics2D::set_callbacksOnDisable")] static object SetCod(object[] a) { EnsureLoaded(); CallbacksOnDisable = (bool)a[0]; return null; }
    [Icall("UnityEngine.Physics2D::get_reuseCollisionCallbacks")] static object GetRcc(object[] a) { EnsureLoaded(); return ReuseCollisionCallbacks; }
    [Icall("UnityEngine.Physics2D::set_reuseCollisionCallbacks")] static object SetRcc(object[] a) { EnsureLoaded(); ReuseCollisionCallbacks = (bool)a[0]; return null; }
    [Icall("UnityEngine.Physics2D::get_autoSyncTransforms")] static object GetAst(object[] a) { EnsureLoaded(); return AutoSyncTransforms; }
    [Icall("UnityEngine.Physics2D::set_autoSyncTransforms")] static object SetAst(object[] a) { EnsureLoaded(); AutoSyncTransforms = (bool)a[0]; return null; }
    [Icall("UnityEngine.Physics2D::get_simulationMode")] static object GetMode(object[] a) { EnsureLoaded(); return Enum.ToObject(typeof(SimulationMode2D), SimulationMode); }
    [Icall("UnityEngine.Physics2D::set_simulationMode")] static object SetMode(object[] a) { EnsureLoaded(); SimulationMode = Convert.ToInt32(a[0]); return null; }
    [Icall("UnityEngine.Physics2D::GetLayerCollisionMask_Internal")] static object GetMask(object[] a) { EnsureLoaded(); return unchecked((int)_matrix[Convert.ToInt32(a[0]) & 31]); }
    /// <summary>Unity keeps the matrix symmetric: setting layer L's mask also sets bit L of every other layer's.</summary>
    [Icall("UnityEngine.Physics2D::SetLayerCollisionMask_Internal")] static object SetMask(object[] a)
    {
        EnsureLoaded(); int l = Convert.ToInt32(a[0]) & 31; uint m = unchecked((uint)Convert.ToInt32(a[1]));
        for (int i = 0; i < 32; i++) SetPair(l, i, ((m >> i) & 1) != 0);
        return null;
    }
    [Icall("UnityEngine.Physics2D::GetIgnoreLayerCollision_Internal")] static object GetIgnoreLayer(object[] a) => !Collide(Convert.ToInt32(a[0]), Convert.ToInt32(a[1]));
    [Icall("UnityEngine.Physics2D::IgnoreLayerCollision_Internal")] static object IgnoreLayer(object[] a) { EnsureLoaded(); SetPair(Convert.ToInt32(a[0]), Convert.ToInt32(a[1]), !(bool)a[2]); return null; }
    [Icall("UnityEngine.Physics2D::IgnoreCollision")] static object IgnoreCollision(object[] a)
    {
        if (Objects.Get(a[0]) is NBehaviour c1 && Objects.Get(a[1]) is NBehaviour c2) Triggers2D.Ignore(c1, c2, (bool)a[2]);
        return null;
    }
    [Icall("UnityEngine.Physics2D::GetIgnoreCollision")] static object GetIgnoreCollision(object[] a) =>
        Objects.Get(a[0]) is NBehaviour c1 && Objects.Get(a[1]) is NBehaviour c2 && Triggers2D.Ignored(c1, c2);
}
