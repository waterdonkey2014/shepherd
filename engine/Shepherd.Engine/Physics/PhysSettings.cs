using System.Buffers.Binary;
using Shepherd.Engine.Runtime;
namespace Shepherd.Engine.Physics;

/// <summary>Physics stage 0 (physics-plan-v2 sections 1.2, 1.4, 4): the project settings physics runs on, read at run time from the owner's
/// own <c>globalgamemanagers</c> -- TimeManager (class 5), Physics2DSettings (class 19, decoded by Runtime/Physics2DSettings.cs, the one place
/// that holds the 2D settings) and PhysicsManager (class 55, the 3D manager: gravity and query flags for Stage 6's island ray) -- and the
/// <b>settings self-test</b>: structural invariants of the decoded records (sizes, the 32-entry symmetric matrices, flag bytes, enum
/// ranges), the engine's own fixed-step constants against the install's TimeManager, and the decoded Physics2DSettings against Box2D
/// 2.3.1's compiled constants through the core (<c>sp_settings_check</c>: exactly one mismatch is expected, maxRotationSpeed, which only a
/// body spinning faster than 1 rev/s reaches -- H-MAXROT). No game value is written into the repository: everything compared is decoded
/// from the install or compiled into Box2D / the engine.
/// <para>When: SHEPHERD_PHYS stays lazy at level 0 exactly as kit #4 (Physics2DSettings is read at the first trigger candidate; a boot that
/// never has two colliders -- the menus -- reads nothing, and a pack need not carry the records); the self-test runs in
/// <c>physcheck</c> (and from Stage 1, at boot).</para></summary>
[ProfileOnly]   // decodes raw records; reads no property-bag key (Assets/Diet.cs)
public static class PhysSettings
{
    public struct Time5 { public float FixedTimestep, MaximumAllowedTimestep, TimeScale, MaximumParticleTimestep; public int Bytes; }
    public struct Manager3D
    {
        public float GravityX, GravityY, GravityZ;
        public bool QueriesHitBackfaces, QueriesHitTriggers, AutoSyncTransforms, ReuseCollisionCallbacks, InvokeCollisionCallbacks;
        public int SimulationMode, Bytes, AsymmetricEntries, BlockedPairs;
    }
    public static Time5 TimeManager; public static Manager3D PhysicsManager;
    public static string TimeSource = "not read", Manager3DSource = "not read";

    /// <summary>Read TimeManager and PhysicsManager (Physics2DSettings through its own lazy reader). Errors become "not this layout" lines.</summary>
    public static void Load()
    {
        Physics2DSettings.EnsureLoaded();
        var gm = Assets.Loader.Db?.GetFile("globalgamemanagers");
        try
        {
            var o = gm?.ObjectsOfClass(5).FirstOrDefault();
            if (o == null) TimeSource = "no TimeManager record";
            else
            {
                var b = gm.GetObjectBytes(o);
                if (b.Length != 16) TimeSource = $"TimeManager is {b.Length} B, not the 2022.3 layout (16 B)";
                else
                {
                    TimeManager = new Time5 { FixedTimestep = F(b, 0), MaximumAllowedTimestep = F(b, 4), TimeScale = F(b, 8), MaximumParticleTimestep = F(b, 12), Bytes = b.Length };
                    TimeSource = $"project (globalgamemanagers #{o.PathId})";
                }
            }
        }
        catch (Exception e) { TimeSource = "unreadable: " + e.Message; }
        try
        {
            var o = gm?.ObjectsOfClass(55).FirstOrDefault();
            if (o == null) Manager3DSource = "no PhysicsManager record";
            else
            {
                var b = gm.GetObjectBytes(o);
                Manager3DSource = Decode3D(b, out PhysicsManager) is { } why ? $"PhysicsManager ({b.Length} B) is not the 2022.3 layout: {why}" : $"project (globalgamemanagers #{o.PathId})";
            }
        }
        catch (Exception e) { Manager3DSource = "unreadable: " + e.Message; }
    }
    static float F(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b[at..]);

    /// <summary>Unity 2022.3's PhysicsManager, serialized order: m_Gravity (Vector3f) 0 | m_DefaultMaterial (PPtr, 12) 12 | m_BounceThreshold,
    /// m_DefaultMaxDepenetrationVelocity, m_SleepThreshold, m_DefaultContactOffset (4 floats) 24 | m_DefaultSolverIterations,
    /// m_DefaultSolverVelocityIterations 40 | m_QueriesHitBackfaces, m_QueriesHitTriggers, m_EnableAdaptiveForce (bools) 48, aligned |
    /// m_ClothInterCollisionDistance, m_ClothInterCollisionStiffness 52 | m_ContactsGeneration 60 | m_LayerCollisionMatrix (count 32 +
    /// masks) 64 | m_SimulationMode 196 | m_AutoSyncTransforms, m_ReuseCollisionCallbacks, m_InvokeCollisionCallbacks (bools) 200, aligned
    /// | ... 272 bytes in all. Returns null, or why it is not this layout.</summary>
    static string Decode3D(ReadOnlySpan<byte> b, out Manager3D m)
    {
        m = default;
        if (b.Length != 272) return $"size {b.Length}, expected 272";
        if (BinaryPrimitives.ReadInt32LittleEndian(b[64..]) != 32) return "matrix length " + BinaryPrimitives.ReadInt32LittleEndian(b[64..]);
        for (int i = 48; i < 51; i++) if (b[i] > 1) return $"flag byte {i} = {b[i]}";
        for (int i = 200; i < 203; i++) if (b[i] > 1) return $"flag byte {i} = {b[i]}";
        int mode = BinaryPrimitives.ReadInt32LittleEndian(b[196..]); if (mode is < 0 or > 2) return "simulation mode " + mode;
        var mx = new uint[32]; for (int i = 0; i < 32; i++) mx[i] = BinaryPrimitives.ReadUInt32LittleEndian(b[(68 + 4 * i)..]);
        int asym = 0, blocked = 0;
        for (int a = 0; a < 32; a++) for (int c = 0; c < 32; c++) { if (((mx[a] >> c) & 1) != ((mx[c] >> a) & 1)) asym++; if (c >= a && ((mx[a] >> c) & 1) == 0) blocked++; }
        m = new Manager3D
        {
            GravityX = F(b, 0), GravityY = F(b, 4), GravityZ = F(b, 8), QueriesHitBackfaces = b[48] != 0, QueriesHitTriggers = b[49] != 0,
            SimulationMode = mode, AutoSyncTransforms = b[200] != 0, ReuseCollisionCallbacks = b[201] != 0, InvokeCollisionCallbacks = b[202] != 0,
            Bytes = b.Length, AsymmetricEntries = asym, BlockedPairs = blocked
        };
        return null;
    }

    /// <summary>The Physics2DSettings values in the core's ABI struct (sp_settings), with the fixed step from TimeManager (else the engine's).</summary>
    public static ShepPhys.Settings ForCore()
    {
        var v = Physics2DSettings.Values;
        return new ShepPhys.Settings
        {
            GravityX = v.GravityX, GravityY = v.GravityY, VelocityIterations = v.VelocityIterations, PositionIterations = v.PositionIterations,
            VelocityThreshold = v.VelocityThreshold, MaxLinearCorrection = v.MaxLinearCorrection, MaxAngularCorrectionDeg = v.MaxAngularCorrection,
            MaxTranslationSpeed = v.MaxTranslationSpeed, MaxRotationSpeedDeg = v.MaxRotationSpeed, BaumgarteScale = v.BaumgarteScale,
            BaumgarteToiScale = v.BaumgarteTOIScale, TimeToSleep = v.TimeToSleep, LinearSleepTolerance = v.LinearSleepTolerance,
            AngularSleepToleranceDeg = v.AngularSleepTolerance, DefaultContactOffset = v.DefaultContactOffset,
            FixedDeltaTime = TimeManager.Bytes == 16 ? TimeManager.FixedTimestep : Runtime.Time.FixedDeltaTime
        };
    }

    /// <summary>The settings self-test (plan section 4 Stage 0 acceptance: "the settings self-test passes; the matrix is symmetric;
    /// sp_settings_check reports exactly one mismatch (maxRotation, H-MAXROT)"). Writes its lines through <paramref name="say"/>; returns the
    /// number of failed checks. A core that cannot bind is a failure of the native part only (logged as such).</summary>
    public static int SelfTest(Action<bool, string> say) => SelfTest(say, true);
    /// <summary>Stage 1's boot form (the world's creation): <paramref name="managers"/> false = only the Physics2DSettings record the game run already
    /// read (a strict pack need not carry TimeManager / PhysicsManager: no extra object is loaded), the matrix and sp_settings_check.</summary>
    public static int SelfTest(Action<bool, string> say, bool managers)
    {
        if (managers) Load(); else Physics2DSettings.EnsureLoaded();
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; say(ok, what); }
        var v = Physics2DSettings.Values;
        Check(Physics2DSettings.Source is "project" or "project (file)", $"Physics2DSettings decoded from the install at run time (source: {Physics2DSettings.Source}; kit #8: \"project (file)\" = the install's record exported to the owner folder, SHEPHERD_PHYS2D_SETTINGS)");
        Check(v.RecordBytes == 280, $"Physics2DSettings record is 280 B in the 2022.3 layout ({v.RecordBytes} B; the reader checks the vector length, flag bytes, padding and size)");
        Check(v.AsymmetricEntries == 0, $"the 2D layer collision matrix is symmetric ({v.AsymmetricEntries} asymmetric entries)");
        Check(v.VelocityIterations > 0 && v.PositionIterations > 0, $"iterations decoded: velocity {v.VelocityIterations}, position {v.PositionIterations}");
        if (managers) Check(TimeSource.StartsWith("project"), $"TimeManager decoded ({TimeSource}, {TimeManager.Bytes} B)");
        if (TimeManager.Bytes == 16)
        {
            Check(BitConverter.SingleToInt32Bits(TimeManager.FixedTimestep) == BitConverter.SingleToInt32Bits(Runtime.Time.FixedDeltaTime),
                $"the engine's fixedDeltaTime (Runtime/Time.cs) equals the install's Fixed Timestep bit for bit ({Runtime.Time.FixedDeltaTime:R} vs {TimeManager.FixedTimestep:R})");
            Check(BitConverter.SingleToInt32Bits(TimeManager.MaximumAllowedTimestep) == BitConverter.SingleToInt32Bits(Runtime.Time.MaximumDeltaTime),
                $"the engine's maximumDeltaTime equals the install's Maximum Allowed Timestep bit for bit ({Runtime.Time.MaximumDeltaTime:R} vs {TimeManager.MaximumAllowedTimestep:R})");
        }
        if (managers) Check(Manager3DSource.StartsWith("project"), $"PhysicsManager (3D) decoded ({Manager3DSource})");
        if (PhysicsManager.Bytes == 272)
            Check(PhysicsManager.AsymmetricEntries == 0, $"the 3D layer collision matrix is symmetric ({PhysicsManager.AsymmetricEntries} asymmetric entries); 3D gravity ({PhysicsManager.GravityX:R}, " +
                $"{PhysicsManager.GravityY:R}, {PhysicsManager.GravityZ:R}), queriesHitBackfaces {PhysicsManager.QueriesHitBackfaces}, queriesHitTriggers {PhysicsManager.QueriesHitTriggers}, " +
                $"autoSyncTransforms {PhysicsManager.AutoSyncTransforms}, simulationMode {PhysicsManager.SimulationMode}");
        // the decoded 2D settings against Box2D 2.3.1's compiled constants (the plan's section 1.2 table), through the core
        var core = ShepPhys.Core;
        if (core == null) { Check(false, $"sp_settings_check: the core did not bind ({ShepPhys.Status})"); return fails; }
        var rows = new ShepPhys.SettingsRow[ShepPhys.SettingsRows];
        int differs = core.SettingsCheck(ForCore(), rows);
        foreach (var r in rows)
        {
            string verdict = r.Verdict switch { 0 => "EXACT", 1 => "close", _ => "DIFFERS" };
            say(true, $"  sp_settings_check {ShepPhys.RowNames[r.Id],-40} decoded {r.Unity,12:R} vs Box2D {r.Box2D,12:R}  {verdict}" +
                (r.Verdict == 2 ? $"   FAIL phys settings {ShepPhys.RowNames[r.Id]}: {(ShepPhys.RowSwitch[r.Id].Length > 0 ? "affects " + ShepPhys.RowSwitch[r.Id] : "no switch")}" : ""));
        }
        bool onlyMaxRot = differs == 1 && rows[10].Verdict == 2;
        Check(onlyMaxRot, $"sp_settings_check: {differs} mismatch(es) with Box2D 2.3.1's compiled constants under the check's per-second reading -- expected exactly one, maxRotationSpeed. " +
                          $"(E1 measured both clamps per step -- 100 u and 360 deg a step, H-MAXTRANS / H-MAXROT=per-step -- and patch P2 gives the world those values at run time in " +
                          $"place of b2_maxTranslation / b2_maxRotation: PhysWorld's world creation)");
        return fails;
    }
}
