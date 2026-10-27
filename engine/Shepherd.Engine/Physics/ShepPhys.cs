using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
namespace Shepherd.Engine.Physics;

/// <summary>Physics stage 0 (physics-plan-v2 sections 1.1, 1.3): the C-shaped seam between the engine and the native core. Int handles and
/// POD structs only (the libshepphys ABI, vita/physics/shim/shepphys.h): no pointer crosses it except the pool's one base, floats travel
/// through memory. Stage 0 uses the runtime part (ABI handshake, pool, settings check, nop, trace); the world calls come with Stage 1.</summary>
public interface IPhysCore
{
    /// <summary>The ABI handshake: version, the struct sizes (declaration order) and the float probe (51.75).</summary>
    int Abi(int[] sizes, out float probe);
    string BuildInfo { get; }
    int Init(int poolBytes, int flags);
    int Shutdown();
    IntPtr PoolBase { get; }
    int PoolAlloc(int bytes);
    int PoolFree(int offset);
    int PoolStats(out ShepPhys.PoolInfo s);
    int Nop();
    int SettingsCheck(in ShepPhys.Settings s, ShepPhys.SettingsRow[] rows);
    int TraceBegin(string path, int flags);
    int TraceEnd();
    int TraceNote(int tag, ReadOnlySpan<int> words);
    // ---- Stage 1: the Unity layer (shim/sp_unity.cpp; ShepPhys's structs) ----
    int UWorldCreate(in ShepPhys.UWorldDef d);
    int UWorldDestroy(int world);
    int UWorldGround(int world);
    int SetMatrix(int world, uint[] rows32);
    int WorldSetClamps(int world, float maxTranslation, float maxRotationRad);   // since E1: patch P2's per-step clamps (H-MAXTRANS / H-MAXROT=per-step)
    int UBodyCreate(int world, in ShepPhys.UBodyDef d);
    int UBodyDestroy(int world, int body);
    int UBodyMirror(int world, int body);
    int UBodySet(int world, int body, in ShepPhys.UBodyDef d);
    int UBodyMass(int world, int body);
    int UShapeCreate(int world, int body, in ShepPhys.UShapeDef d, ReadOnlySpan<float> xy, Span<int> outHandles);
    int ShapeDestroy(int world, int shape);
    int IgnorePair(int world, int a, int b, bool ignore);
    int USync(int world, ReadOnlySpan<ShepPhys.USyncRec> recs, int mode);
    int UFlush(int world, int flags);
    int UStep(int world, in ShepPhys.UStepDef d, out ShepPhys.UStepOut o);
    int UInterp(int world, float alpha, float fdt, int flags);
    int UEarly(int world, int flags);
    int USnapshot(int world);
    int RenderPoses(int world, in ShepPhys.RenderDef d, Span<ShepPhys.RPose> o);
    int B2Stats(out ShepPhys.PoolInfo s);
    float Atan2(float y, float x);
    float Sin(float x);
    float Cos(float x);
    // ---- Stage 2: events with contact serials, the Unity queries (shim/sp_query.cpp) ----
    int Events2(int world, Span<ShepPhys.Event2> o);
    int Query(int world, in ShepPhys.UQuery q, Span<ShepPhys.UHit> o);
    int UBounds(int world, ReadOnlySpan<int> shapes, out float x0, out float y0, out float x1, out float y1);
    int UClosest(int world, ReadOnlySpan<int> shapes, float px, float py, out float x, out float y, out float dist);
    int UTestPoint(int world, ReadOnlySpan<int> shapes, float px, float py);
    int ShapeGetPolygon(int world, int shape, Span<float> xy);   // Stage 4b (ABI 9): a polygon shape's body-space vertices (physcheck)
    int UContactsOf(int world, ReadOnlySpan<int> shapes, int flags, Span<int> other);
    int UDistance(int world, ReadOnlySpan<int> a, ReadOnlySpan<int> b, out float dist);
    int UOverlapShapes(int world, ReadOnlySpan<int> shapes, in ShepPhys.UQuery q, Span<ShepPhys.UHit> o);
    int UBrutePairs(int world, Span<int> out6);
    // ---- Stage 3: forces / impulses in the mirror, contact snapshots (Collision2D) ----
    int UApply(int world, int body, int kind, float x, float y);
    int UContactsBetween(int world, ReadOnlySpan<int> a, ReadOnlySpan<int> b, int flags, Span<ShepPhys.UContact> o, out float relX, out float relY);
    // ---- Stage 6: the 3D island-ground ray (shim/sp_mesh3d.cpp) ----
    int Scene3DCreate(int flags);
    int Scene3DDestroy(int scene);
    int Mesh3DCreate(int scene, ReadOnlySpan<float> xyz, ReadOnlySpan<int> tri, int flags);
    int Mesh3DDestroy(int scene, int mesh);
    int Col3DCreate(int scene, in ShepPhys.Col3Def d);
    int Col3DSet(int scene, int col, in ShepPhys.Col3Def d);
    int Col3DDestroy(int scene, int col);
    int Ray3D(int scene, in ShepPhys.Ray3Query q, Span<ShepPhys.Ray3Hit> o);
    int Scene3DStats(int scene, Span<int> out16);
}

/// <summary>The binding of libshepphys, modelled on Audio/ShepAudio.cs: every entry point is an unmanaged function pointer, resolved from
/// the native host (SHEPHERD_PHYS_HOST=1: the Vita GL eboot exports <c>shepphys_proc</c> from its static P/Invoke table -- an empty table
/// until physics Stage 7 links libshepphys_vita.a, so the lookup finds nothing there yet) or, without a host, from the Mac dylib
/// (SHEPHERD_PHYS_LIB, default vita/physics/build/mac/libshepphys.dylib under the repo; `zsh vita/physics/build.sh mac` makes it). A missing
/// library is the loud line "FAIL phys: libshepphys missing" and <see cref="Core"/> null: the engine then runs Stage-0 behaviour (plan 4,
/// the bar's rule 4). Stage 0 binds it only in the physcheck / physbench modes and for SHEPHERD_PHYS_TRACE; nothing in a normal boot
/// loads it.</summary>
[ProfileOnly]   // log text only: no property-bag key is read here (Assets/Diet.cs)
public sealed unsafe class ShepPhys : IPhysCore
{
    // ---- the ABI's structs (shim/shepphys.h; sizes checked against sp_abi in Bind) ----
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct InitDef { public int PoolBytes, Workers, Flags, Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct Header { public uint Magic, AbiVersion, DirtyPending, RingPending, WorldVersion, BaseGen, R0, R1; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct PoolInfo { public uint Bytes, Used, HighWater, LiveBlocks, Allocs, Frees, Grows, Failures, BaseGen, Top; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Settings
    {
        public float GravityX, GravityY; public int VelocityIterations, PositionIterations;
        public float VelocityThreshold, MaxLinearCorrection, MaxAngularCorrectionDeg, MaxTranslationSpeed, MaxRotationSpeedDeg;
        public float BaumgarteScale, BaumgarteToiScale, TimeToSleep, LinearSleepTolerance, AngularSleepToleranceDeg, DefaultContactOffset;
        public float FixedDeltaTime; public int Reserved;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct SettingsRow { public int Id, Verdict; public float Unity, Box2D; }
    // ---- Stage 1 (shim/shepphys.h "Stage 1"): the Unity layer ----
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct ShapeUnity { public int Layer; public uint Include, Exclude; public int Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct UWorldDef { public float GravityX, GravityY; public int Flags, UseMatrix; public fixed uint Matrix[32]; }
    /// <summary>The per-body mirror record in the pool (sp_mirror, 160 B; 128 before Stage 3): read and written through the pool base, 0 native calls.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Mirror
    {
        public float X, Y, Angle, Vx, Vy, W, Tx, Ty, Ta; public int Flags;
        public fixed float Hx[3], Hy[3], Ha[3]; public fixed int HStep[3];
        public float Ix, Iy, Ia, Wx, Wy, Wa; public int Body, Serial, Mode, User;
        // Stage 3: the force accumulators (MfForce), the inverse mass for the impulse modes, the velocity the last step started from (H-RELVEL)
        public float Fx, Fy, Torque, InvMass, InvI, Pvx, Pvy, Pw;
    }
    public const int MfMovePos = 1, MfMoveRot = 2, MfTeleport = 4, MfSetVel = 8, MfWrite = 16, MfAngle = 32, MfForceWrite = 64, MfPose = 128, MfAwake = 256, MfForce = 512;
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct UBodyDef
    {
        public int Type, Flags; public float X, Y, M00, M10, Mass, LinearDrag, AngularDrag, GravityScale; public int Interpolation, Serial, User, Reserved;
    }
    public const int UbSimulated = 1, UbNeverSleep = 2, UbStartAsleep = 4, UbFreezeRotation = 8, UbAutoMass = 16, UbFreezeX = 32, UbFreezeY = 64;
    public const int BodyStatic = 0, BodyKinematic = 1, BodyDynamic = 2;
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct UShapeDef
    {
        public int Kind, Flags; public fixed float Body2[4]; public fixed float Rel[6]; public float Ox, Oy, Radius, Sx, Sy; public int Direction;
        public float Density, Friction, Restitution; public int Layer; public uint Include, Exclude; public fixed float Adj[4]; public int VertexCount, User;
    }
    public const int UsCircle = 0, UsBox = 1, UsPolygon = 2, UsEdge = 3, UsCapsule = 4, UsLoop = 5;
    public const int UsfSensor = 1, UsfAdjStart = 2, UsfAdjEnd = 4, UsfOneSided = 8;   // Stage 3: H-CHAIN one-sided
    public const int UsfChainWeld = 16;   // Stage 4a (ABI 8): Unity's chain vertex filter on an edge / loop (shim/shepphys.h SP_USF_CHAIN_WELD)
    /// <summary>Stage 4b (ABI 9): the points are one piece of Unity's convex decomposition in body space -- Unity's polygon build natively (its weld,
    /// Box2D's hull, its collinear hull-vertex removal; patch P5 in the fixture's Set). shim/shepphys.h SP_USF_UNITY_POLY.</summary>
    public const int UsfUnityPoly = 32;
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct USyncRec { public int Body, Flags; public float X, Y, M00, M10; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct UStepDef { public float Dt; public int VelocityIterations, PositionIterations, StepIndex, Flags, Reserved; }
    public const int UstepMoveVelKeep = 1, UstepMoveVelZero = 2, UstepMoveDragOn = 4, UstepWakeTpStock = 8, UstepDampExp = 16, UstepDampLinear = 32;   // Stage 3: H-DAMP
    /// <summary>Since E1 (H-MOVEDRAG=drag-off-gravity-on, the engine's only setting): a MovePosition step keeps the body's gravity, drag off. The other
    /// Ustep variant bits stay in the native ABI for its self-tests; the engine passes none of them since E1.</summary>
    public const int UstepMoveGravOn = 64;
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct UStepOut { public int Step, Moved, Teleported, Written, Awake, Bodies, Contacts, Touching; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct RenderDef { public int JRel; public float Alpha, Fdt; public int Flags; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct RPose { public int Body, Serial; public float X, Y, Angle; public int Mode; }
    // ---- Stage 2 (shim/shepphys.h "Stage 2"): P1 world flags, contact serials, events, the Unity queries ----
    public const int WorldAllowSleep = 1, WorldWarmStart = 4, WorldTriggerPhase = 16, WorldKinematicTriggers = 32, WorldContactSerials = 64;
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct Event2 { public int Kind, Flags, ShapeA, ShapeB, Step, Serial, ChildA, ChildB; }
    public const int EvBegin = 1, EvEnd = 2, EvfSensor = 1, EvfInStep = 2;
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct UQuery
    {
        public int Kind, Flags; public uint LayerMask; public int ExcludeUser; public float Ox, Oy, Dx, Dy, Distance, Radius, Sx, Sy, Angle; public int Direction;
    }
    public const int UqRay = 0, UqCircleCast = 1, UqBoxCast = 2, UqCapsuleCast = 3, UqPoint = 4, UqCircle = 5, UqBox = 6, UqCapsule = 7;
    public const int UqfAll = 1, UqfTriggers = 2, UqfStartIn = 4, UqfAny = 8, UqfSort = 16, UqfBrute = 32;
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct UHit { public int Shape, User; public float Cx, Cy, Px, Py, Nx, Ny, Distance, Fraction; }
    public const int ContactsNoSensors = 2;
    // ---- Stage 3 (shim/shepphys.h "Stage 3"): forces, contact snapshots ----
    public const int ApplyForce = 0, ApplyImpulse = 1, ApplyTorque = 2, ApplyAngularImpulse = 3;
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct UContact { public int ShapeA, ShapeB; public float Px, Py, Nx, Ny, Separation, NormalImpulse, TangentImpulse, Rvx, Rvy; public int Flags; }
    public const int UcpfEnabled = 1, UcfPostSolve = 1;
    // ---- Stage 6 (shim/shepphys.h "Stage 6"): the 3D scene ----
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct Mesh3Def { public int VertexCount, TriangleCount, Flags, Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Col3Def
    {
        public int Kind, Flags, Layer, User, Mesh, Direction;
        public float Px, Py, Pz, Qx, Qy, Qz, Qw, Sx, Sy, Sz, Cx, Cy, Cz, Ex, Ey, Ez;
    }
    public const int C3Box = 1, C3Sphere = 2, C3Capsule = 3, C3Mesh = 4, C3fTrigger = 1;
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct Ray3Query { public float Ox, Oy, Oz, Dx, Dy, Dz, MaxDistance; public uint LayerMask; public int Flags, Reserved; }
    public const int R3fTriggers = 1, R3fBackfaces = 2, R3fAll = 4, R3fSort = 8, R3fAny = 16, R3fPointRay = 32, R3fGeneral = 64, R3fBrute = 128;
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct Ray3Hit { public float Px, Py, Pz, Nx, Ny, Nz, Distance, U, V; public int Face, User, Col; }
    public const int InitB2Malloc = 2;
    public const int AbiVersion = 9, AbiSizes = 34, SettingsRows = 11;   // ABI 9 (Stage 4b): SP_SHAPE_UNITY_WELD (patch P5), SP_USF_UNITY_POLY; ABI 8 (Stage 4a): SP_USF_CHAIN_WELD; ABI 7 (E1): sp_world_set_clamps, SP_USTEP_MOVEGRAV_ON
    public const uint HeaderMagic = 0x53504844u;
    public const int InitPoolGrow = 1;
    /// <summary>The sizes this binding was written for, in sp_abi's order (the 11 stage-0a structs, then stage 0's).</summary>
    static readonly int[] Expected = { 16, 48, 64, 36, 16, 36, 20, 88, 24, 36, 24, sizeof(InitDef), sizeof(Header), sizeof(PoolInfo), sizeof(Settings), sizeof(SettingsRow),
        sizeof(ShapeUnity), sizeof(UWorldDef), sizeof(Mirror), sizeof(UBodyDef), sizeof(UShapeDef), sizeof(USyncRec), sizeof(UStepDef), sizeof(UStepOut), sizeof(RenderDef), sizeof(RPose),
        sizeof(Event2), sizeof(UQuery), sizeof(UHit), sizeof(UContact), sizeof(Mesh3Def), sizeof(Col3Def), sizeof(Ray3Query), sizeof(Ray3Hit) };
    /// <summary>sp_settings_check's row names and the hypothesis switch each one bears on (plan 1.2 table).</summary>
    public static readonly string[] RowNames = { "velocityThreshold", "maxLinearCorrection", "maxAngularCorrection", "baumgarteScale", "baumgarteTOIScale",
        "timeToSleep", "linearSleepTolerance", "angularSleepTolerance", "defaultContactOffset (b2_polygonRadius)", "maxTranslationSpeed (per step)", "maxRotationSpeed (per step)" };
    public static readonly string[] RowSwitch = { "", "", "", "", "", "H-KINSLEEP", "", "", "H-SLOP", "H-MAXTRANS", "H-MAXROT" };

    // ---- the binding ----
    static ShepPhys _core; static bool _tried;
    public static string Status { get; private set; } = "not bound";
    /// <summary>The bound core, or null (library missing or the ABI handshake failed: logged once).</summary>
    public static IPhysCore Core { get { if (!_tried) { _tried = true; _core = Bind(); } return _core; } }

    static IntPtr _lib;
    [MethodImpl(MethodImplOptions.NoInlining)] static IntPtr HostLookup(string name) => shepphys_proc(name);
    [DllImport("__Internal")] static extern IntPtr shepphys_proc([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    public static string LibPath() => Environment.GetEnvironmentVariable("SHEPHERD_PHYS_LIB") is { Length: > 0 } p ? p : Path.Combine(Program.Root, "vita", "physics", "build", "mac", "libshepphys.dylib");
    static IntPtr Proc(string name)
    {
        IntPtr p;
        if (PhysConfig.HostProc) p = HostLookup(name);
        else
        {
            if (_lib == IntPtr.Zero) _lib = NativeLibrary.Load(LibPath());
            p = NativeLibrary.TryGetExport(_lib, name, out var q) ? q : IntPtr.Zero;
        }
        if (p == IntPtr.Zero) throw new EntryPointNotFoundException($"shepphys: {name} not found ({(PhysConfig.HostProc ? "host shepphys_proc" : LibPath())})");
        return p;
    }

    delegate* unmanaged<int*, int, float*, int> _abi;
    delegate* unmanaged<IntPtr> _buildInfo;
    delegate* unmanaged<InitDef*, int> _init;
    delegate* unmanaged<int> _shutdown, _nop, _traceEnd;
    delegate* unmanaged<IntPtr> _poolBase;
    delegate* unmanaged<int, int> _poolAlloc, _poolFree;
    delegate* unmanaged<PoolInfo*, int> _poolStats;
    delegate* unmanaged<Settings*, SettingsRow*, int, int> _settings;
    delegate* unmanaged<byte*, int, int> _traceBegin;
    delegate* unmanaged<int, int*, int, int> _traceNote;
    // Stage 1
    delegate* unmanaged<UWorldDef*, int> _uworldCreate;
    delegate* unmanaged<int, int> _uworldDestroy, _uworldGround;
    delegate* unmanaged<int, uint*, int> _setMatrix;
    delegate* unmanaged<int, float*, int> _setClamps;
    delegate* unmanaged<int, UBodyDef*, int> _ubodyCreate;
    delegate* unmanaged<int, int, int> _ubodyDestroy, _ubodyMirror, _ubodyMass, _shapeDestroy, _uflush, _uearly;
    delegate* unmanaged<int, int, UBodyDef*, int> _ubodySet;
    delegate* unmanaged<int, int, UShapeDef*, float*, int*, int, int> _ushapeCreate;
    delegate* unmanaged<int, int, int, int, int> _ignorePair;
    delegate* unmanaged<int, USyncRec*, int, int, int> _usync;
    delegate* unmanaged<int, UStepDef*, UStepOut*, int> _ustep;
    delegate* unmanaged<int, float*, int, int> _uinterp;
    delegate* unmanaged<int, int> _usnapshot;
    delegate* unmanaged<int, RenderDef*, RPose*, int, int> _renderPoses;
    delegate* unmanaged<PoolInfo*, int> _b2Stats;
    delegate* unmanaged<float, float, float> _atan2;
    delegate* unmanaged<float, float> _sin, _cos;
    // Stage 2
    delegate* unmanaged<int, Event2*, int, int> _events2;
    delegate* unmanaged<int, UQuery*, UHit*, int, int> _uquery;
    delegate* unmanaged<int, int*, int, float*, int> _ubounds;
    delegate* unmanaged<int, int*, int, float*, float*, int> _uclosest;
    delegate* unmanaged<int, int*, int, float*, int> _utestPoint;
    delegate* unmanaged<int, int, float*, int, int> _shapeGetPolygon;
    delegate* unmanaged<int, int*, int, int, int*, int, int> _ucontactsOf;
    delegate* unmanaged<int, int*, int, int*, int, float*, int> _udistance;
    delegate* unmanaged<int, int*, int, UQuery*, UHit*, int, int> _uoverlapShapes;
    delegate* unmanaged<int, int*, int> _ubrutePairs;
    // Stage 3
    delegate* unmanaged<int, int, int, float*, int> _uapply;
    delegate* unmanaged<int, int*, int, int*, int, int, UContact*, int, float*, int> _ucontactsBetween;
    // Stage 6
    delegate* unmanaged<int, int> _scene3dCreate, _scene3dDestroy;
    delegate* unmanaged<int, Mesh3Def*, float*, int*, int> _mesh3dCreate;
    delegate* unmanaged<int, int, int> _mesh3dDestroy, _col3dDestroy;
    delegate* unmanaged<int, Col3Def*, int> _col3dCreate;
    delegate* unmanaged<int, int, Col3Def*, int> _col3dSet;
    delegate* unmanaged<int, Ray3Query*, Ray3Hit*, int, int> _ray3d;
    delegate* unmanaged<int, int*, int> _scene3dStats;

    static ShepPhys Bind()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!PhysConfig.HostProc && !File.Exists(LibPath())) { Status = $"FAIL phys: libshepphys missing ({LibPath()}: run `zsh vita/physics/build.sh mac`); stage 0 behaviour"; Log.Error("[phys] " + Status); return null; }
            var c = new ShepPhys();
            c._abi = (delegate* unmanaged<int*, int, float*, int>)Proc("sp_abi");
            c._buildInfo = (delegate* unmanaged<IntPtr>)Proc("sp_build_info");
            c._init = (delegate* unmanaged<InitDef*, int>)Proc("sp_init");
            c._shutdown = (delegate* unmanaged<int>)Proc("sp_shutdown");
            c._nop = (delegate* unmanaged<int>)Proc("sp_nop");
            c._poolBase = (delegate* unmanaged<IntPtr>)Proc("sp_pool_base");
            c._poolAlloc = (delegate* unmanaged<int, int>)Proc("sp_pool_alloc");
            c._poolFree = (delegate* unmanaged<int, int>)Proc("sp_pool_free");
            c._poolStats = (delegate* unmanaged<PoolInfo*, int>)Proc("sp_pool_get_stats");
            c._settings = (delegate* unmanaged<Settings*, SettingsRow*, int, int>)Proc("sp_settings_check");
            c._traceBegin = (delegate* unmanaged<byte*, int, int>)Proc("sp_trace_begin");
            c._traceEnd = (delegate* unmanaged<int>)Proc("sp_trace_end");
            c._traceNote = (delegate* unmanaged<int, int*, int, int>)Proc("sp_trace_note");
            c._uworldCreate = (delegate* unmanaged<UWorldDef*, int>)Proc("sp_uworld_create");
            c._uworldDestroy = (delegate* unmanaged<int, int>)Proc("sp_uworld_destroy");
            c._uworldGround = (delegate* unmanaged<int, int>)Proc("sp_uworld_ground");
            c._setMatrix = (delegate* unmanaged<int, uint*, int>)Proc("sp_world_set_matrix");
            c._setClamps = (delegate* unmanaged<int, float*, int>)Proc("sp_world_set_clamps");
            c._ubodyCreate = (delegate* unmanaged<int, UBodyDef*, int>)Proc("sp_ubody_create");
            c._ubodyDestroy = (delegate* unmanaged<int, int, int>)Proc("sp_ubody_destroy");
            c._ubodyMirror = (delegate* unmanaged<int, int, int>)Proc("sp_ubody_mirror");
            c._ubodyMass = (delegate* unmanaged<int, int, int>)Proc("sp_ubody_mass");
            c._ubodySet = (delegate* unmanaged<int, int, UBodyDef*, int>)Proc("sp_ubody_set");
            c._ushapeCreate = (delegate* unmanaged<int, int, UShapeDef*, float*, int*, int, int>)Proc("sp_ushape_create");
            c._shapeDestroy = (delegate* unmanaged<int, int, int>)Proc("sp_shape_destroy");
            c._ignorePair = (delegate* unmanaged<int, int, int, int, int>)Proc("sp_world_ignore_pair");
            c._usync = (delegate* unmanaged<int, USyncRec*, int, int, int>)Proc("sp_u_sync");
            c._uflush = (delegate* unmanaged<int, int, int>)Proc("sp_u_flush");
            c._ustep = (delegate* unmanaged<int, UStepDef*, UStepOut*, int>)Proc("sp_u_step");
            c._uinterp = (delegate* unmanaged<int, float*, int, int>)Proc("sp_u_interp");
            c._uearly = (delegate* unmanaged<int, int, int>)Proc("sp_u_early");
            c._usnapshot = (delegate* unmanaged<int, int>)Proc("sp_u_snapshot");
            c._renderPoses = (delegate* unmanaged<int, RenderDef*, RPose*, int, int>)Proc("sp_render_poses");
            c._b2Stats = (delegate* unmanaged<PoolInfo*, int>)Proc("sp_b2_get_stats");
            c._atan2 = (delegate* unmanaged<float, float, float>)Proc("sp_atan2f");
            c._sin = (delegate* unmanaged<float, float>)Proc("sp_sinf");
            c._cos = (delegate* unmanaged<float, float>)Proc("sp_cosf");
            c._events2 = (delegate* unmanaged<int, Event2*, int, int>)Proc("sp_events2");
            c._uquery = (delegate* unmanaged<int, UQuery*, UHit*, int, int>)Proc("sp_u_query");
            c._ubounds = (delegate* unmanaged<int, int*, int, float*, int>)Proc("sp_u_bounds");
            c._uclosest = (delegate* unmanaged<int, int*, int, float*, float*, int>)Proc("sp_u_closest");
            c._utestPoint = (delegate* unmanaged<int, int*, int, float*, int>)Proc("sp_u_test_point");
            c._shapeGetPolygon = (delegate* unmanaged<int, int, float*, int, int>)Proc("sp_shape_get_polygon");
            c._ucontactsOf = (delegate* unmanaged<int, int*, int, int, int*, int, int>)Proc("sp_u_contacts_of");
            c._udistance = (delegate* unmanaged<int, int*, int, int*, int, float*, int>)Proc("sp_u_distance");
            c._uoverlapShapes = (delegate* unmanaged<int, int*, int, UQuery*, UHit*, int, int>)Proc("sp_u_overlap_shapes");
            c._ubrutePairs = (delegate* unmanaged<int, int*, int>)Proc("sp_u_brute_pairs");
            c._uapply = (delegate* unmanaged<int, int, int, float*, int>)Proc("sp_u_apply");
            c._ucontactsBetween = (delegate* unmanaged<int, int*, int, int*, int, int, UContact*, int, float*, int>)Proc("sp_u_contacts_between");
            c._scene3dCreate = (delegate* unmanaged<int, int>)Proc("sp_scene3d_create");
            c._scene3dDestroy = (delegate* unmanaged<int, int>)Proc("sp_scene3d_destroy");
            c._mesh3dCreate = (delegate* unmanaged<int, Mesh3Def*, float*, int*, int>)Proc("sp_mesh3d_create");
            c._mesh3dDestroy = (delegate* unmanaged<int, int, int>)Proc("sp_mesh3d_destroy");
            c._col3dCreate = (delegate* unmanaged<int, Col3Def*, int>)Proc("sp_col3d_create");
            c._col3dSet = (delegate* unmanaged<int, int, Col3Def*, int>)Proc("sp_col3d_set");
            c._col3dDestroy = (delegate* unmanaged<int, int, int>)Proc("sp_col3d_destroy");
            c._ray3d = (delegate* unmanaged<int, Ray3Query*, Ray3Hit*, int, int>)Proc("sp_ray3d");
            c._scene3dStats = (delegate* unmanaged<int, int*, int>)Proc("sp_scene3d_stats");
            var sizes = new int[AbiSizes];
            int v = c.Abi(sizes, out var probe);
            var bad = new List<string>();
            if (v != AbiVersion) bad.Add($"ABI version {v}, this binding is {AbiVersion}");
            if (BitConverter.SingleToInt32Bits(probe) != BitConverter.SingleToInt32Bits(51.75f)) bad.Add($"float probe {probe} (0x{BitConverter.SingleToInt32Bits(probe):x8}), expected 51.75");
            for (int i = 0; i < AbiSizes; i++) if (sizes[i] != Expected[i]) bad.Add($"struct {i} is {sizes[i]} B, the binding's {Expected[i]} B");
            if (bad.Count > 0) { Status = "FAIL phys: ABI handshake: " + string.Join("; ", bad); Log.Error("[phys] " + Status); return null; }
            Status = $"bound ({(PhysConfig.HostProc ? "host shepphys_proc" : "dylib " + LibPath())}): {c.BuildInfo}; ABI {v}, {AbiSizes} struct sizes and the float probe 51.75 match ({IntPtr.Size * 8}-bit engine), {sw.Elapsed.TotalMilliseconds:F1} ms";
            Log.Info("[phys] " + Status);
            return c;
        }
        catch (Exception e) { Status = $"FAIL phys: libshepphys missing or incomplete ({e.GetBaseException().Message}); stage 0 behaviour"; Log.Error("[phys] " + Status); return null; }
    }

    // ---- IPhysCore: each native call in its own non-inlined method (Render/ShepGl.cs's rule) ----
    [MethodImpl(MethodImplOptions.NoInlining)] public int Abi(int[] sizes, out float probe) { float p = 0; int v; fixed (int* s = sizes) v = _abi(s, sizes.Length, &p); probe = p; return v; }
    public string BuildInfo { [MethodImpl(MethodImplOptions.NoInlining)] get => Marshal.PtrToStringUTF8(_buildInfo()) ?? "?"; }
    [MethodImpl(MethodImplOptions.NoInlining)] public int Init(int poolBytes, int flags) { var d = new InitDef { PoolBytes = poolBytes, Flags = flags }; return _init(&d); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int Shutdown() => _shutdown();
    public IntPtr PoolBase { [MethodImpl(MethodImplOptions.NoInlining)] get => _poolBase(); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int PoolAlloc(int bytes) => _poolAlloc(bytes);
    [MethodImpl(MethodImplOptions.NoInlining)] public int PoolFree(int offset) => _poolFree(offset);
    [MethodImpl(MethodImplOptions.NoInlining)] public int PoolStats(out PoolInfo s) { PoolInfo t; int r = _poolStats(&t); s = t; return r; }
    [MethodImpl(MethodImplOptions.NoInlining)] public int Nop() => _nop();
    [MethodImpl(MethodImplOptions.NoInlining)] public int SettingsCheck(in Settings s, SettingsRow[] rows) { var c = s; fixed (SettingsRow* r = rows) return _settings(&c, r, rows?.Length ?? 0); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int TraceBegin(string path, int flags) { var b = Encoding.UTF8.GetBytes(path + "\0"); fixed (byte* p = b) return _traceBegin(p, flags); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int TraceEnd() => _traceEnd();
    [MethodImpl(MethodImplOptions.NoInlining)] public int TraceNote(int tag, ReadOnlySpan<int> words) { fixed (int* w = words) return _traceNote(tag, w, words.Length); }

    // ---- Stage 1 ----
    [MethodImpl(MethodImplOptions.NoInlining)] public int UWorldCreate(in UWorldDef d) { var c = d; return _uworldCreate(&c); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int UWorldDestroy(int world) => _uworldDestroy(world);
    [MethodImpl(MethodImplOptions.NoInlining)] public int UWorldGround(int world) => _uworldGround(world);
    [MethodImpl(MethodImplOptions.NoInlining)] public int SetMatrix(int world, uint[] rows32) { fixed (uint* r = rows32) return _setMatrix(world, r); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int WorldSetClamps(int world, float maxTranslation, float maxRotationRad) { float* tr = stackalloc float[2]; tr[0] = maxTranslation; tr[1] = maxRotationRad; return _setClamps(world, tr); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int UBodyCreate(int world, in UBodyDef d) { var c = d; return _ubodyCreate(world, &c); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int UBodyDestroy(int world, int body) => _ubodyDestroy(world, body);
    [MethodImpl(MethodImplOptions.NoInlining)] public int UBodyMirror(int world, int body) => _ubodyMirror(world, body);
    [MethodImpl(MethodImplOptions.NoInlining)] public int UBodySet(int world, int body, in UBodyDef d) { var c = d; return _ubodySet(world, body, &c); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int UBodyMass(int world, int body) => _ubodyMass(world, body);
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int UShapeCreate(int world, int body, in UShapeDef d, ReadOnlySpan<float> xy, Span<int> outHandles)
    {
        var c = d;
        fixed (float* p = xy) fixed (int* o = outHandles) return _ushapeCreate(world, body, &c, xy.Length > 0 ? p : null, o, outHandles.Length);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public int ShapeDestroy(int world, int shape) => _shapeDestroy(world, shape);
    [MethodImpl(MethodImplOptions.NoInlining)] public int IgnorePair(int world, int a, int b, bool ignore) => _ignorePair(world, a, b, ignore ? 1 : 0);
    [MethodImpl(MethodImplOptions.NoInlining)] public int USync(int world, ReadOnlySpan<USyncRec> recs, int mode) { fixed (USyncRec* r = recs) return _usync(world, r, recs.Length, mode); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int UFlush(int world, int flags) => _uflush(world, flags);
    [MethodImpl(MethodImplOptions.NoInlining)] public int UStep(int world, in UStepDef d, out UStepOut o) { var c = d; UStepOut t; int r = _ustep(world, &c, &t); o = t; return r; }
    [MethodImpl(MethodImplOptions.NoInlining)] public int UInterp(int world, float alpha, float fdt, int flags) { float* a = stackalloc float[2]; a[0] = alpha; a[1] = fdt; return _uinterp(world, a, flags); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int UEarly(int world, int flags) => _uearly(world, flags);
    [MethodImpl(MethodImplOptions.NoInlining)] public int USnapshot(int world) => _usnapshot(world);
    [MethodImpl(MethodImplOptions.NoInlining)] public int RenderPoses(int world, in RenderDef d, Span<RPose> o) { var c = d; fixed (RPose* p = o) return _renderPoses(world, &c, p, o.Length); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int B2Stats(out PoolInfo s) { PoolInfo t; int r = _b2Stats(&t); s = t; return r; }
    [MethodImpl(MethodImplOptions.NoInlining)] public float Atan2(float y, float x) => _atan2(y, x);
    [MethodImpl(MethodImplOptions.NoInlining)] public float Sin(float x) => _sin(x);
    [MethodImpl(MethodImplOptions.NoInlining)] public float Cos(float x) => _cos(x);
    // ---- Stage 2 ----
    [MethodImpl(MethodImplOptions.NoInlining)] public int Events2(int world, Span<Event2> o) { fixed (Event2* p = o) return _events2(world, p, o.Length); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int Query(int world, in UQuery q, Span<UHit> o) { var c = q; fixed (UHit* p = o) return _uquery(world, &c, p, o.Length); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int UBounds(int world, ReadOnlySpan<int> shapes, out float x0, out float y0, out float x1, out float y1)
    {
        float* b = stackalloc float[4]; int r; fixed (int* s = shapes) r = _ubounds(world, s, shapes.Length, b);
        x0 = b[0]; y0 = b[1]; x1 = b[2]; y1 = b[3]; return r;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int UClosest(int world, ReadOnlySpan<int> shapes, float px, float py, out float x, out float y, out float dist)
    {
        float* p = stackalloc float[2]; float* o = stackalloc float[3]; p[0] = px; p[1] = py; int r; fixed (int* s = shapes) r = _uclosest(world, s, shapes.Length, p, o);
        x = o[0]; y = o[1]; dist = o[2]; return r;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int ShapeGetPolygon(int world, int shape, Span<float> xy) { fixed (float* p = xy) return _shapeGetPolygon(world, shape, p, xy.Length / 2); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int UTestPoint(int world, ReadOnlySpan<int> shapes, float px, float py) { float* p = stackalloc float[2]; p[0] = px; p[1] = py; fixed (int* s = shapes) return _utestPoint(world, s, shapes.Length, p); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int UContactsOf(int world, ReadOnlySpan<int> shapes, int flags, Span<int> other) { fixed (int* s = shapes) fixed (int* o = other) return _ucontactsOf(world, s, shapes.Length, flags, o, other.Length); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int UDistance(int world, ReadOnlySpan<int> a, ReadOnlySpan<int> b, out float dist) { float d = 0; int r; fixed (int* pa = a) fixed (int* pb = b) r = _udistance(world, pa, a.Length, pb, b.Length, &d); dist = d; return r; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int UOverlapShapes(int world, ReadOnlySpan<int> shapes, in UQuery q, Span<UHit> o) { var c = q; fixed (int* s = shapes) fixed (UHit* p = o) return _uoverlapShapes(world, s, shapes.Length, &c, p, o.Length); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int UBrutePairs(int world, Span<int> out6) { fixed (int* o = out6) return _ubrutePairs(world, o); }
    // ---- Stage 3 ----
    [MethodImpl(MethodImplOptions.NoInlining)] public int UApply(int world, int body, int kind, float x, float y) { float* a = stackalloc float[2]; a[0] = x; a[1] = y; return _uapply(world, body, kind, a); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int UContactsBetween(int world, ReadOnlySpan<int> a, ReadOnlySpan<int> b, int flags, Span<UContact> o, out float relX, out float relY)
    {
        float* r = stackalloc float[2]; int n;
        fixed (int* pa = a) fixed (int* pb = b) fixed (UContact* po = o) n = _ucontactsBetween(world, pa, a.Length, pb, b.Length, flags, po, o.Length, r);
        relX = r[0]; relY = r[1]; return n;
    }

    // ---- Stage 6 ----
    [MethodImpl(MethodImplOptions.NoInlining)] public int Scene3DCreate(int flags) => _scene3dCreate(flags);
    [MethodImpl(MethodImplOptions.NoInlining)] public int Scene3DDestroy(int scene) => _scene3dDestroy(scene);
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Mesh3DCreate(int scene, ReadOnlySpan<float> xyz, ReadOnlySpan<int> tri, int flags)
    {
        var d = new Mesh3Def { VertexCount = xyz.Length / 3, TriangleCount = tri.Length / 3, Flags = flags };
        fixed (float* v = xyz) fixed (int* t = tri) return _mesh3dCreate(scene, &d, v, t);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public int Mesh3DDestroy(int scene, int mesh) => _mesh3dDestroy(scene, mesh);
    [MethodImpl(MethodImplOptions.NoInlining)] public int Col3DCreate(int scene, in Col3Def d) { var c = d; return _col3dCreate(scene, &c); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int Col3DSet(int scene, int col, in Col3Def d) { var c = d; return _col3dSet(scene, col, &c); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int Col3DDestroy(int scene, int col) => _col3dDestroy(scene, col);
    [MethodImpl(MethodImplOptions.NoInlining)] public int Ray3D(int scene, in Ray3Query q, Span<Ray3Hit> o) { var c = q; fixed (Ray3Hit* p = o) return _ray3d(scene, &c, p, o.Length); }
    [MethodImpl(MethodImplOptions.NoInlining)] public int Scene3DStats(int scene, Span<int> out16) { fixed (int* o = out16) return _scene3dStats(scene, o); }

    /// <summary>The mirror record at a pool offset (valid until the pool moves: a Mac grow bumps base_gen; callers re-take it per use).</summary>
    public Mirror* MirrorAt(int offset) => (Mirror*)(PoolBase + offset);

    /// <summary>The pool's header block (offset 16), read through the base: what the Stage 7 fast path reads without a call.</summary>
    public Header ReadHeader() { var b = PoolBase; return b == IntPtr.Zero ? default : *(Header*)(b + 16); }
}
