using System.Linq.Expressions;
using System.Reflection;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using Shepherd.Engine.Impl;
using Shepherd.Engine.Render;
using UnityEngine;

namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>kit #7 spine track, "Spine package" (vita/notes/kit7-spine.md; roadmap kit #7: pose bake, stored compressed with a decode cache, per-clip
/// bounds; and "baked-pose store in read order"). Mac only, test-only: nothing here changes a frame. Three parts:
/// (1) BAKE -- SHEPHERD_SPINE_BAKE=&lt;dir&gt;: after a frame in which a SkeletonDataAsset built its SkeletonData, every animation is sampled with the
///     game's own Spine code (a fresh Skeleton with every skin's bones active; SetToSetupPose, Animation.Apply(..., MixBlend.Setup, In),
///     UpdateWorldTransform) at SHEPHERD_SPINE_BAKE_HZ (60): each bone's world transform (a, b, c, d, worldX, worldY, skeleton space, root at
///     0, scale 1), each slot's colour and attachment, the draw order when the animation keys it -- quantized to 16 bits per value against the
///     clip's own range per bone and component (this file's "SPBK" v1 format; the store laid out in the decode loop's read order: frame by
///     frame, bone by bone, then slot by slot). Per clip also its BOUNDS: the largest extent of every attachment any skin can put in any slot
///     (region corners, mesh vertices, weighted bind positions, every deform key), through the bones of each sample at 4x the bake rate -- the
///     box of every bone's local attachment envelope at those SAMPLED poses. NOT conservative (review fix R5, kit7-spine.md): the attachment
///     envelope covers every attachment / deform key, but the bone poses are samples of one unmixed track applied with MixBlend.Setup -- a bone
///     that rotates or moves along a curve between two samples, a crossfade of two clips (blended poses no sample produced), or a bone the game
///     writes after the animation (SkeletonUtilityBone, aim / look code) can put the mesh outside the box. The check's "bounds violated 0" over
///     the intro is a measurement, not a proof; nothing may cull with these bounds without an analytic bound (and game-written bones cannot be
///     bounded from the data at all). One file per asset, &lt;dir&gt;/&lt;asset&gt;.sbake: game-derived data, owner-local, never in git.
/// (2) CHECK -- SHEPHERD_SPINE_BAKE_CHECK=&lt;dir&gt;: every frame, every drawing skeleton whose data has a bake and whose AnimationState plays one
///     track with no mix: the baked pose at the track's animation time (two decoded frames, linear) against the live bones -- each bone's
///     origin, and every vertex of every attachment the live slots show computed by Spine's own ComputeWorldVertices with the live bones and
///     with the baked bones (a shadow skeleton; the live deform copied), projected to screen pixels through the camera that draws it -- and
///     the live mesh's box against the clip's baked bounds (violations counted). The acceptance (roadmap): &lt; 0.1 px every bone, every frame.
/// (3) READ ORDER -- SHEPHERD_SPINE_BAKE_CACHESIM=1: the decode loop's memory reads simulated on a Cortex-A9-like L1 data cache (32 KiB, 4-way,
///     32-byte lines, LRU) for the store in read order vs the same values stored curve by curve (bone, component, then frame): misses per
///     decoded frame (DERIVED: a model, not a device counter).</summary>
[ProfileOnly]
public static class SpineBake
{
    static readonly string BakeDir = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_BAKE");
    static readonly string CheckDir = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_BAKE_CHECK");
    static readonly float Hz = float.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_SPINE_BAKE_HZ"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hz) && hz > 0 ? hz : 60f;
    static readonly bool CacheSim = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_BAKE_CACHESIM") == "1";
    public static readonly bool On = BakeDir != null || CheckDir != null;
    const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly HashSet<object> _baked = new(ReferenceEqualityComparer.Instance);

    // ---------- reflection: compiled field getters / setters ----------
    static readonly Dictionary<(Type, string), Func<object, object>> _get = new();
    static readonly Dictionary<(Type, string), Func<object, float>> _getF = new();
    static readonly Dictionary<(Type, string), Action<object, float>> _setF = new();
    static FieldInfo Fi(Type t, string n) { for (var c = t; c != null; c = c.BaseType) { var f = c.GetField(n, I | BindingFlags.DeclaredOnly); if (f != null) return f; } throw new MissingFieldException(t.Name, n); }
    static object G(object o, string n)
    {
        var t = o.GetType();
        if (!_get.TryGetValue((t, n), out var g))
        {
            var p = Expression.Parameter(typeof(object)); var f = Fi(t, n);
            g = Expression.Lambda<Func<object, object>>(Expression.Convert(Expression.Field(Expression.Convert(p, f.DeclaringType), f), typeof(object)), p).Compile();
            _get[(t, n)] = g;
        }
        return g(o);
    }
    static float GF(object o, string n)
    {
        var t = o.GetType();
        if (!_getF.TryGetValue((t, n), out var g))
        {
            var p = Expression.Parameter(typeof(object)); var f = Fi(t, n);
            g = Expression.Lambda<Func<object, float>>(Expression.Field(Expression.Convert(p, f.DeclaringType), f), p).Compile();
            _getF[(t, n)] = g;
        }
        return g(o);
    }
    static void SF(object o, string n, float v)
    {
        var t = o.GetType();
        if (!_setF.TryGetValue((t, n), out var s))
        {
            var p = Expression.Parameter(typeof(object)); var vv = Expression.Parameter(typeof(float)); var f = Fi(t, n);
            s = Expression.Lambda<Action<object, float>>(Expression.Assign(Expression.Field(Expression.Convert(p, f.DeclaringType), f), vv), p, vv).Compile();
            _setF[(t, n)] = s;
        }
        s(o, v);
    }
    static int Count(object exposedList) => (int)G(exposedList, "Count");
    static Array Items(object exposedList) => (Array)G(exposedList, "Items");
    static bool Is(object o, string typeName) { for (var t = o?.GetType(); t != null; t = t.BaseType) if (t.Name == typeName) return true; return false; }

    // ---------- the store, in memory (the file formats: below) ----------
    internal sealed class Clip
    {
        public string Name; public float Duration; public int Frames, Flags;
        public float[] Min, Max;   // bones * 6
        public ushort[] Q;          // frames * bones * 6, frame-major: the decode loop's read order
        public byte[] SlotCol; public short[] SlotAtt, DrawOrder;   // frames * slots * 4 / frames * slots / frames * slots (null: not keyed)
        public float MinX, MinY, MaxX, MaxY; public float QuantErr;
    }
    internal sealed class Baked { public string Asset; public int Bones, Slots; public float Rate; public string[] BoneNames; public readonly List<Clip> Clips = new(); public readonly Dictionary<string, Clip> ByName = new(); }
    const int FDeform = 1, FDrawOrder = 2, FConstraints = 4, FNonNormal = 8;
    static readonly Dictionary<string, Baked> _loaded = new();

    public static void After(int frame)
    {
        if (!On) return;
        try
        {
            if (BakeDir != null) BakeNew(frame);
            if (CheckDir != null) Check(frame);
        }
        catch (Exception e) { Log.Exception(e, "SpineBake"); }
    }

    // ================= (1) bake =================
    static void BakeNew(int frame)
    {
        foreach (var n in Objects.All.ToArray())
        {
            var m = n.Managed; if (m == null || m.GetType().Name != "SkeletonDataAsset") continue;
            var data = G(m, "skeletonData"); if (data == null || !_baked.Add(data)) continue;
            var name = ((UnityEngine.Object)m).name;
            var path = Path.Combine(BakeDir, Safe(name) + ".sbake");
            if (File.Exists(path)) { Log.Info($"[spinebake] f{frame} '{name}': {path} exists (a second copy of the data): not baked again"); continue; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var b = Bake(name, data);
            Directory.CreateDirectory(BakeDir); Write(b, path);
            long poses = b.Clips.Sum(c => (long)c.Frames), bytes = new FileInfo(path).Length, v1 = V1Bytes(b), rt = RoundTrip(b, path);
            _totBytes += bytes; _totV1 += v1; _totRt += rt; _totAssets++;
            Log.Info($"[spinebake] f{frame} '{name}': {b.Clips.Count} clips, {poses:N0} poses x {b.Bones} bones at {b.Rate} Hz -> {bytes / 1024.0:F0} KiB as v{Format} ({bytes * 8.0 / Math.Max(1, poses * b.Bones):F1} bits per bone pose; v1 {v1 / 1024.0:F0} KiB, float32 bones alone {poses * b.Bones * 24 / 1024.0:F0} KiB; round trip: {rt} values differ); worst quantization step {b.Clips.Max(c => c.QuantErr):G3} units; flags: deform {b.Clips.Count(c => (c.Flags & FDeform) != 0)}, draw order {b.Clips.Count(c => (c.Flags & FDrawOrder) != 0)}, constraints {b.Clips.Count(c => (c.Flags & FConstraints) != 0)}, non-Normal bone modes {((b.Clips.FirstOrDefault()?.Flags ?? 0) & FNonNormal) != 0}; {sw.ElapsedMilliseconds} ms");
            if (CacheSim) SimulateReadOrder(b);
        }
    }
    static long _totBytes, _totV1, _totRt, _totAssets;
    static string Safe(string s) { var c = s.ToCharArray(); for (int i = 0; i < c.Length; i++) if (!char.IsLetterOrDigit(c[i]) && c[i] != '_' && c[i] != '-') c[i] = '_'; return new string(c); }

    static Baked Bake(string asset, object data)
    {
        var asm = data.GetType().Assembly;
        Type T(string n) => asm.GetType(n, true);
        var skelT = T("Spine.Skeleton"); var skinT = T("Spine.Skin"); var animT = T("Spine.Animation");
        var mixBlendT = T("Spine.MixBlend"); var mixDirT = T("Spine.MixDirection");
        var apply = animT.GetMethod("Apply"); var setup = skelT.GetMethod("SetToSetupPose"); var update = skelT.GetMethod("UpdateWorldTransform", Type.EmptyTypes);
        var setSkin = skelT.GetMethod("SetSkin", new[] { skinT }); var addSkin = skinT.GetMethod("AddSkin");
        object blendSetup = Enum.Parse(mixBlendT, "Setup"), dirIn = Enum.Parse(mixDirT, "In");
        var sk = Activator.CreateInstance(skelT, data);
        // every skin's bones and constraints active (a skin-agnostic bake), and every slot's attachments known
        var all = Activator.CreateInstance(skinT, "<bake>");
        var skins = G(data, "skins"); int ns = Count(skins); var si = Items(skins);
        for (int k = 0; k < ns; k++) addSkin.Invoke(all, new[] { si.GetValue(k) });
        setSkin.Invoke(sk, new[] { all });
        var bones = G(sk, "bones"); int nb = Count(bones); var bi = Items(bones); var boneArr = new object[nb]; for (int i = 0; i < nb; i++) boneArr[i] = bi.GetValue(i);
        var slots = G(sk, "slots"); int nsl = Count(slots); var sli = Items(slots); var slotArr = new object[nsl]; for (int i = 0; i < nsl; i++) slotArr[i] = sli.GetValue(i);
        var b = new Baked { Asset = asset, Bones = nb, Slots = nsl, Rate = Hz, BoneNames = boneArr.Select(x => (string)G(G(x, "data"), "name")).ToArray() };
        int nonNormal = boneArr.Count(x => Convert.ToInt32(G(G(x, "data"), "transformMode")) != 0);
        bool constraints = Count(G(data, "ikConstraints")) + Count(G(data, "transformConstraints")) + Count(G(data, "pathConstraints")) > 0;
        // every slot's attachments over every skin: the local envelope per bone (bounds)
        var boxes = LocalBoxes(data, skins, ns, si, slotArr, boneArr);
        // the attachment of each slot as an index into the slot's own name table (the timeline's key, through the combined skin)
        var attIndex = new Dictionary<object, short>(ReferenceEqualityComparer.Instance);
        {
            var dict = (System.Collections.IDictionary)G(all, "attachments"); var perSlot = new int[nsl];
            foreach (var v in dict.Values) { var a = G(v, "attachment"); int s = (int)G(v, "slotIndex"); if (a != null && !attIndex.ContainsKey(a) && s < nsl) attIndex[a] = (short)perSlot[s]++; }
        }
        var anims = G(data, "animations"); int na = Count(anims); var ai = Items(anims);
        var vals = new float[nb * 6];
        for (int k = 0; k < na; k++)
        {
            var an = ai.GetValue(k); float dur = GF(an, "duration");
            var tls = G(an, "timelines"); int nt = Count(tls); var tli = Items(tls); int flags = (nonNormal > 0 ? FNonNormal : 0) | (constraints ? FConstraints : 0);
            for (int q = 0; q < nt; q++) { var tl = tli.GetValue(q); if (Is(tl, "DeformTimeline")) flags |= FDeform; else if (Is(tl, "DrawOrderTimeline")) flags |= FDrawOrder; }
            int frames = Math.Max(1, (int)MathF.Ceiling(dur * Hz)) + 1;
            var c = new Clip { Name = (string)G(an, "name"), Duration = dur, Frames = frames, Flags = flags, Min = new float[nb * 6], Max = new float[nb * 6], Q = new ushort[frames * nb * 6], SlotCol = new byte[frames * nsl * 4], SlotAtt = new short[frames * nsl], DrawOrder = (flags & FDrawOrder) != 0 ? new short[frames * nsl] : null };
            var raw = new float[frames * nb * 6];
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            const int Fine = 4;   // bounds sampled at 4x the bake rate (samples: not a bound between them -- see the summary, R5)
            int fineN = (frames - 1) * Fine + 1;
            for (int fi = 0; fi < fineN; fi++)
            {
                float t = Math.Min(fi / (Hz * Fine), dur);
                setup.Invoke(sk, null);
                apply.Invoke(an, new object[] { sk, 0f, t, false, null, 1f, blendSetup, dirIn });
                update.Invoke(sk, null);
                for (int i = 0; i < nb; i++)
                {
                    var bo = boneArr[i];
                    float ba = GF(bo, "a"), bb = GF(bo, "b"), bc = GF(bo, "c"), bd = GF(bo, "d"), bx = GF(bo, "worldX"), by = GF(bo, "worldY");
                    if (boxes[i] is { } bx4)
                        for (int corner = 0; corner < 4; corner++)
                        {
                            float lx = (corner & 1) == 0 ? bx4.x0 : bx4.x1, ly = (corner & 2) == 0 ? bx4.y0 : bx4.y1;
                            float wx = lx * ba + ly * bb + bx, wy = lx * bc + ly * bd + by;
                            if (wx < minX) minX = wx; if (wx > maxX) maxX = wx; if (wy < minY) minY = wy; if (wy > maxY) maxY = wy;
                        }
                    if (fi % Fine == 0) { int o = (fi / Fine * nb + i) * 6; raw[o] = ba; raw[o + 1] = bb; raw[o + 2] = bc; raw[o + 3] = bd; raw[o + 4] = bx; raw[o + 5] = by; }
                }
                if (fi % Fine != 0) continue;
                int f = fi / Fine;
                for (int s = 0; s < nsl; s++)
                {
                    var sl = slotArr[s]; int o = (f * nsl + s) * 4;
                    c.SlotCol[o] = B(GF(sl, "r")); c.SlotCol[o + 1] = B(GF(sl, "g")); c.SlotCol[o + 2] = B(GF(sl, "b")); c.SlotCol[o + 3] = B(GF(sl, "a"));
                    var att = G(sl, "attachment"); c.SlotAtt[f * nsl + s] = att != null && attIndex.TryGetValue(att, out var ix) ? ix : (short)-1;
                }
                if (c.DrawOrder != null) { var dro = G(sk, "drawOrder"); var di = Items(dro); for (int s = 0; s < nsl; s++) c.DrawOrder[f * nsl + s] = (short)(int)G(G(di.GetValue(s), "data"), "index"); }
            }
            // quantize against each bone component's range in this clip
            for (int j = 0; j < nb * 6; j++) { float mn = float.PositiveInfinity, mx = float.NegativeInfinity; for (int f = 0; f < frames; f++) { float v = raw[f * nb * 6 + j]; if (v < mn) mn = v; if (v > mx) mx = v; } c.Min[j] = mn; c.Max[j] = mx; c.QuantErr = Math.Max(c.QuantErr, (mx - mn) / 65535f * 0.5f); }
            for (int f = 0; f < frames; f++)
                for (int j = 0; j < nb * 6; j++)
                {
                    float mn = c.Min[j], rg = c.Max[j] - mn, v = raw[f * nb * 6 + j];
                    c.Q[f * nb * 6 + j] = rg > 0 ? (ushort)Math.Clamp((int)MathF.Round((v - mn) / rg * 65535f), 0, 65535) : (ushort)0;
                }
            c.MinX = minX; c.MinY = minY; c.MaxX = maxX; c.MaxY = maxY;
            b.Clips.Add(c); b.ByName[c.Name] = c;
        }
        return b;
    }
    static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    sealed class Box { public float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity; public void Add(float x, float y) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; } }
    /// <summary>Per bone, the box (in the bone's own space) of every vertex any attachment of any skin can put through it: region corners
    /// (offset), unweighted mesh vertices (and every deform key's vertices of that mesh: absolute local positions), weighted bind positions
    /// (each with every deform key's delta for that entry).</summary>
    static Box[] LocalBoxes(object data, object skins, int ns, Array si, object[] slotArr, object[] boneArr)
    {
        var boxes = new Box[boneArr.Length];
        var boneIndex = new Dictionary<object, int>(ReferenceEqualityComparer.Instance); for (int i = 0; i < boneArr.Length; i++) boneIndex[G(boneArr[i], "data")] = i;
        Box Of(int i) => boxes[i] ??= new Box();
        // deform keys by attachment
        var deform = new Dictionary<object, List<float[]>>(ReferenceEqualityComparer.Instance);
        var anims = G(data, "animations"); int na = Count(anims); var ai = Items(anims);
        for (int k = 0; k < na; k++) { var tls = G(ai.GetValue(k), "timelines"); int nt = Count(tls); var tli = Items(tls); for (int q = 0; q < nt; q++) { var tl = tli.GetValue(q); if (!Is(tl, "DeformTimeline")) continue; var a = G(tl, "attachment"); if (a == null) continue; if (!deform.TryGetValue(a, out var l)) deform[a] = l = new(); foreach (var fv in (float[][])G(tl, "frameVertices")) if (fv != null) l.Add(fv); } }
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (int k = 0; k < ns; k++)
        {
            var dict = (System.Collections.IDictionary)G(si.GetValue(k), "attachments");
            foreach (var v in dict.Values)
            {
                var a = G(v, "attachment"); int s = (int)G(v, "slotIndex"); if (a == null || s >= slotArr.Length || !seen.Add(a)) continue;
                int slotBone = boneIndex[G(G(slotArr[s], "data"), "boneData")];
                if (Is(a, "RegionAttachment")) { var off = (float[])G(a, "offset"); var bx = Of(slotBone); for (int i = 0; i < 8; i += 2) bx.Add(off[i], off[i + 1]); continue; }
                if (!Is(a, "MeshAttachment")) continue;   // bounding boxes, paths, points, clipping polygons draw nothing
                var verts = (float[])G(a, "vertices"); var bns = (int[])G(a, "bones");
                deform.TryGetValue(a, out var keys); deform.TryGetValue(G(a, "deformAttachment") ?? a, out var keys2); keys ??= keys2;
                if (bns == null)
                {
                    var bx = Of(slotBone); for (int i = 0; i + 1 < verts.Length; i += 2) bx.Add(verts[i], verts[i + 1]);
                    if (keys != null) foreach (var fv in keys) for (int i = 0; i + 1 < fv.Length; i += 2) bx.Add(fv[i], fv[i + 1]);
                }
                else
                {
                    // bones: count, index x count, ...; vertices: (x, y, weight) per entry; a deform key: (dx, dy) per entry
                    int e = 0;
                    for (int p = 0; p < bns.Length;)
                    {
                        int n = bns[p++];
                        for (int j = 0; j < n; j++, p++, e++)
                        {
                            int bi2 = bns[p]; if (bi2 >= boneArr.Length) continue; var bx = Of(bi2);
                            float x = verts[e * 3], y = verts[e * 3 + 1]; bx.Add(x, y);
                            if (keys != null) foreach (var fv in keys) if (e * 2 + 1 < fv.Length) bx.Add(x + fv[e * 2], y + fv[e * 2 + 1]);
                        }
                    }
                }
            }
        }
        return boxes;
    }

    // ---------- file ----------
    // v1: every frame's values as stored in memory (uint16 per bone component, rgba + attachment per slot), frame-major.
    // v2 (the default; SHEPHERD_SPINE_BAKE_FORMAT=1 writes v1): "stored compressed" with random access per frame --
    //   (a) a component that does not move in the clip is its Min alone (no per-frame data);
    //   (b) the moving components in blocks of 16 frames: per component a uint16 base + a bit width, then the block's frames in read order,
    //       each frame a row of fixed-width fields (q - base), LSB first; frame f of a block starts at (f % 16) x (the sum of the widths) bits,
    //       so one frame decodes without its neighbours (a GPU / NEON unpack: base + bitfield per component);
    //   (c) slot colour / attachment and the draw order as change events (the frame and the new value; frame 0 always).
    //   The decoded values are the v1 values bit for bit (Read checks a round trip after every write).
    const uint Magic = 0x4B425053;   // "SPBK"
    const int Block = 16;
    static readonly int Format = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_BAKE_FORMAT") == "1" ? 1 : 2;
    /// <summary>The v1 size of a bake (bytes), for the log's comparison.</summary>
    static long V1Bytes(Baked b)
    {
        long n = 24;
        foreach (var c in b.Clips) n += 1 + System.Text.Encoding.UTF8.GetByteCount(c.Name) + 32 + b.Bones * 6 * 8 + (long)c.Frames * (b.Bones * 12 + b.Slots * 6 + (c.DrawOrder != null ? b.Slots * 2 : 0));
        return n;
    }
    static void Write(Baked b, string path)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Magic); w.Write(Format); w.Write(b.Bones); w.Write(b.Slots); w.Write(b.Rate); w.Write(b.Clips.Count);
        foreach (var c in b.Clips)
        {
            w.Write(c.Name); w.Write(c.Duration); w.Write(c.Frames); w.Write(c.Flags); w.Write(c.MinX); w.Write(c.MinY); w.Write(c.MaxX); w.Write(c.MaxY);
            if (Format == 1)
            {
                foreach (var x in c.Min) w.Write(x); foreach (var x in c.Max) w.Write(x);
                // the frames in read order: per frame the bones (6 x uint16 each), then the slots (rgba, attachment), then the draw order when keyed
                for (int f = 0; f < c.Frames; f++)
                {
                    for (int j = 0; j < b.Bones * 6; j++) w.Write(c.Q[f * b.Bones * 6 + j]);
                    for (int s = 0; s < b.Slots; s++) { w.Write(c.SlotCol, (f * b.Slots + s) * 4, 4); w.Write(c.SlotAtt[f * b.Slots + s]); }
                    if (c.DrawOrder != null) for (int s = 0; s < b.Slots; s++) w.Write(c.DrawOrder[f * b.Slots + s]);
                }
                continue;
            }
            if (c.Frames > 65535) throw new InvalidDataException($"{c.Name}: {c.Frames} frames");
            int n6 = b.Bones * 6;
            var cols = new List<int>(); for (int j = 0; j < n6; j++) if (c.Max[j] > c.Min[j]) cols.Add(j);
            foreach (var x in c.Min) w.Write(x);
            w.Write(cols.Count); foreach (int j in cols) { w.Write((ushort)j); w.Write(c.Max[j]); }
            var bas = new ushort[cols.Count]; var wid = new byte[cols.Count];
            for (int f0 = 0; f0 < c.Frames; f0 += Block)
            {
                int nf = Math.Min(Block, c.Frames - f0);
                for (int k = 0; k < cols.Count; k++)
                {
                    int lo = 65535, hi = 0; for (int f = f0; f < f0 + nf; f++) { int q = c.Q[f * n6 + cols[k]]; if (q < lo) lo = q; if (q > hi) hi = q; }
                    bas[k] = (ushort)lo; int d = hi - lo, bits = 0; while (d >> bits != 0) bits++; wid[k] = (byte)bits;
                    w.Write(bas[k]);
                }
                w.Write(wid);
                ulong acc = 0; int nacc = 0;
                for (int f = f0; f < f0 + nf; f++)
                    for (int k = 0; k < cols.Count; k++)
                    {
                        if (wid[k] == 0) continue;
                        acc |= (ulong)(c.Q[f * n6 + cols[k]] - bas[k]) << nacc; nacc += wid[k];
                        while (nacc >= 8) { w.Write((byte)acc); acc >>= 8; nacc -= 8; }
                    }
                if (nacc > 0) w.Write((byte)acc);
            }
            for (int s = 0; s < b.Slots; s++)
            {
                var ev = new List<int>(); for (int f = 0; f < c.Frames; f++) if (f == 0 || !c.SlotCol.AsSpan((f * b.Slots + s) * 4, 4).SequenceEqual(c.SlotCol.AsSpan(((f - 1) * b.Slots + s) * 4, 4))) ev.Add(f);
                w.Write((ushort)ev.Count); foreach (int f in ev) { w.Write((ushort)f); w.Write(c.SlotCol, (f * b.Slots + s) * 4, 4); }
                ev.Clear(); for (int f = 0; f < c.Frames; f++) if (f == 0 || c.SlotAtt[f * b.Slots + s] != c.SlotAtt[(f - 1) * b.Slots + s]) ev.Add(f);
                w.Write((ushort)ev.Count); foreach (int f in ev) { w.Write((ushort)f); w.Write(c.SlotAtt[f * b.Slots + s]); }
            }
            if (c.DrawOrder != null)
            {
                var ev = new List<int>(); for (int f = 0; f < c.Frames; f++) if (f == 0 || !c.DrawOrder.AsSpan(f * b.Slots, b.Slots).SequenceEqual(c.DrawOrder.AsSpan((f - 1) * b.Slots, b.Slots))) ev.Add(f);
                w.Write((ushort)ev.Count); foreach (int f in ev) { w.Write((ushort)f); for (int s = 0; s < b.Slots; s++) w.Write(c.DrawOrder[f * b.Slots + s]); }
            }
        }
    }
    static Baked Read(string path, string asset)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        int ver = r.ReadUInt32() == Magic ? r.ReadInt32() : 0; if (ver != 1 && ver != 2) throw new InvalidDataException(path);
        var b = new Baked { Asset = asset, Bones = r.ReadInt32(), Slots = r.ReadInt32(), Rate = r.ReadSingle() };
        int nc = r.ReadInt32();
        for (int k = 0; k < nc; k++)
        {
            var c = new Clip { Name = r.ReadString(), Duration = r.ReadSingle(), Frames = r.ReadInt32(), Flags = r.ReadInt32(), MinX = r.ReadSingle(), MinY = r.ReadSingle(), MaxX = r.ReadSingle(), MaxY = r.ReadSingle() };
            int n6 = b.Bones * 6; c.Min = new float[n6]; c.Max = new float[n6];
            c.Q = new ushort[c.Frames * n6]; c.SlotCol = new byte[c.Frames * b.Slots * 4]; c.SlotAtt = new short[c.Frames * b.Slots]; if ((c.Flags & FDrawOrder) != 0) c.DrawOrder = new short[c.Frames * b.Slots];
            if (ver == 1)
            {
                for (int j = 0; j < n6; j++) c.Min[j] = r.ReadSingle(); for (int j = 0; j < n6; j++) c.Max[j] = r.ReadSingle();
                for (int f = 0; f < c.Frames; f++)
                {
                    for (int j = 0; j < n6; j++) c.Q[f * n6 + j] = r.ReadUInt16();
                    for (int s = 0; s < b.Slots; s++) { r.Read(c.SlotCol, (f * b.Slots + s) * 4, 4); c.SlotAtt[f * b.Slots + s] = r.ReadInt16(); }
                    if (c.DrawOrder != null) for (int s = 0; s < b.Slots; s++) c.DrawOrder[f * b.Slots + s] = r.ReadInt16();
                }
            }
            else
            {
                for (int j = 0; j < n6; j++) c.Max[j] = c.Min[j] = r.ReadSingle();
                int ncols = r.ReadInt32(); var cols = new int[ncols];
                for (int q = 0; q < ncols; q++) { cols[q] = r.ReadUInt16(); c.Max[cols[q]] = r.ReadSingle(); }
                var bas = new ushort[ncols];
                for (int f0 = 0; f0 < c.Frames; f0 += Block)
                {
                    int nf = Math.Min(Block, c.Frames - f0);
                    for (int q = 0; q < ncols; q++) bas[q] = r.ReadUInt16();
                    var wid = r.ReadBytes(ncols);
                    long bits = 0; for (int q = 0; q < ncols; q++) bits += wid[q]; bits *= nf;
                    var data = r.ReadBytes((int)((bits + 7) / 8)); long pos = 0;
                    for (int f = f0; f < f0 + nf; f++)
                        for (int q = 0; q < ncols; q++)
                        {
                            int v = 0;
                            for (int t = 0; t < wid[q]; t++, pos++) v |= ((data[pos >> 3] >> (int)(pos & 7)) & 1) << t;
                            c.Q[f * n6 + cols[q]] = (ushort)(bas[q] + v);
                        }
                }
                for (int s = 0; s < b.Slots; s++)
                {
                    int n = r.ReadUInt16(); int prev = 0; var col = new byte[4];
                    for (int e = 0; e <= n; e++)
                    {
                        int f = e < n ? r.ReadUInt16() : c.Frames;
                        for (int g = prev; g < f && e > 0; g++) Array.Copy(col, 0, c.SlotCol, (g * b.Slots + s) * 4, 4);
                        if (e < n) { r.Read(col, 0, 4); prev = f; }
                    }
                    n = r.ReadUInt16(); prev = 0; short att = 0;
                    for (int e = 0; e <= n; e++)
                    {
                        int f = e < n ? r.ReadUInt16() : c.Frames;
                        for (int g = prev; g < f && e > 0; g++) c.SlotAtt[g * b.Slots + s] = att;
                        if (e < n) { att = r.ReadInt16(); prev = f; }
                    }
                }
                if (c.DrawOrder != null)
                {
                    int n = r.ReadUInt16(), prev = 0; var dor = new short[b.Slots];
                    for (int e = 0; e <= n; e++)
                    {
                        int f = e < n ? r.ReadUInt16() : c.Frames;
                        for (int g = prev; g < f && e > 0; g++) Array.Copy(dor, 0, c.DrawOrder, g * b.Slots, b.Slots);
                        if (e < n) { for (int s = 0; s < b.Slots; s++) dor[s] = r.ReadInt16(); prev = f; }
                    }
                }
            }
            b.Clips.Add(c); b.ByName[c.Name] = c;
        }
        return b;
    }
    /// <summary>Read a written bake back and compare every stored value with the baked one (the format is lossless on top of the 16-bit
    /// quantization): the number of values that differ (0 expected).</summary>
    static long RoundTrip(Baked b, string path)
    {
        var r = Read(path, b.Asset); long bad = 0;
        for (int k = 0; k < b.Clips.Count; k++)
        {
            var c = b.Clips[k]; var d = r.Clips[k];
            for (int j = 0; j < c.Min.Length; j++) { if (BitConverter.SingleToInt32Bits(c.Min[j]) != BitConverter.SingleToInt32Bits(d.Min[j])) bad++; if (BitConverter.SingleToInt32Bits(c.Max[j]) != BitConverter.SingleToInt32Bits(d.Max[j])) bad++; }
            for (int j = 0; j < c.Q.Length; j++) if (c.Q[j] != d.Q[j]) bad++;
            for (int j = 0; j < c.SlotCol.Length; j++) if (c.SlotCol[j] != d.SlotCol[j]) bad++;
            for (int j = 0; j < c.SlotAtt.Length; j++) if (c.SlotAtt[j] != d.SlotAtt[j]) bad++;
            if (c.DrawOrder != null) for (int j = 0; j < c.DrawOrder.Length; j++) if (c.DrawOrder[j] != d.DrawOrder[j]) bad++;
            if (c.Frames != d.Frames || c.Name != d.Name) bad++;
        }
        return bad;
    }

    // ---------- decode + decode cache ----------
    /// <summary>The decode cache: decoded frames (6 floats per bone) of recent (clip, frame) pairs, least recently used out (64 frames).</summary>
    sealed class DecodeCache
    {
        readonly Dictionary<(Clip, int), LinkedListNode<((Clip, int) k, float[] v)>> _map = new(); readonly LinkedList<((Clip, int) k, float[] v)> _lru = new();
        public long Hits, Misses;
        public float[] Get(Baked b, Clip c, int f)
        {
            if (_map.TryGetValue((c, f), out var node)) { Hits++; _lru.Remove(node); _lru.AddFirst(node); return node.Value.v; }
            Misses++;
            float[] v = null; if (_lru.Count >= 64) { var last = _lru.Last; _lru.RemoveLast(); _map.Remove(last.Value.k); v = last.Value.v; }
            if (v == null || v.Length != b.Bones * 6) v = new float[b.Bones * 6];   // (a recycled slot of another skeleton's size is dropped)
            Decode(b, c, f, v);
            var n = _lru.AddFirst(((c, f), v)); _map[(c, f)] = n; return v;
        }
    }
    /// <summary>One frame: the values in read order (bone by bone, component by component), each min + q / 65535 x (max - min).</summary>
    static void Decode(Baked b, Clip c, int f, float[] dst)
    {
        int n6 = b.Bones * 6, o = f * n6;
        for (int j = 0; j < n6; j++) dst[j] = c.Min[j] + c.Q[o + j] * (1f / 65535f) * (c.Max[j] - c.Min[j]);
    }

    // ================= (2) check =================
    sealed class Stat { public long Frames, Verts; public double MaxVertPx, MaxBonePx; public string WorstBone; public long BoundsBad; public double MaxOutPx; }
    static readonly Dictionary<(string, string), Stat> _stats = new();
    static readonly Dictionary<object, object> _shadow = new(ReferenceEqualityComparer.Instance);   // live skeleton -> a shadow skeleton of its data
    static readonly DecodeCache _cache = new();
    static long _notOne, _notBaked, _checked, _notApplied;
    static float[] _wa = new float[256], _wb = new float[256];
    static readonly string DbgAsset = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_BAKE_DBG"); static bool Dbg => DbgAsset != null; static int _dbg;

    static void Check(int frame)
    {
        var cam = FirstScreenCamera(); if (cam == null) return;
        var vp = M.Mul(CameraIcalls.Proj(cam), CameraIcalls.View(cam)); var pr = CameraIcalls.PixelRectF(cam); float gh = SceneRenderer.G?.H ?? 544;
        foreach (var n in Objects.All.ToArray())
        {
            if (n is not NComponent comp || comp.Destroyed || comp.Managed == null || !Is(comp.Managed, "SkeletonAnimation")) continue;
            if (comp.Go == null || comp.Go.Scene == 0 || !comp.Go.ActiveInHierarchy) continue;
            var m = comp.Managed; if (G(m, "valid") is not true) continue;
            var asset = G(m, "skeletonDataAsset") as UnityEngine.Object; if (asset == null) continue;
            var b = Load(asset.name); if (b == null) { _notBaked++; continue; }
            var st = G(m, "state"); var sk = G(m, "skeleton"); if (st == null || sk == null) continue;
            var tracks = G(st, "tracks"); int ntr = Count(tracks); var ti = Items(tracks); object entry = null; int live = 0;
            for (int k = 0; k < ntr; k++) if (ti.GetValue(k) is { } te) { live++; entry ??= te; }
            if (live != 1 || G(entry, "mixingFrom") != null || GF(entry, "alpha") != 1f || (int)G(entry, "trackIndex") != 0) { _notOne++; continue; }
            var an = G(entry, "animation"); var clipName = (string)G(an, "name");
            if (!b.ByName.TryGetValue(clipName, out var c)) { _notBaked++; continue; }
            // the time of the pose the bones hold: the animation time of the track's last Apply (AnimationState.Apply stores it in
            // nextAnimationLast; the game's LOD updater may apply on other frames than it advances the track)
            float t = GF(entry, "nextAnimationLast");
            if (t < 0) { _notApplied++; continue; }   // (-1: the entry has not posed the skeleton yet)
            if (G(entry, "loop") is true && c.Duration != 0) t %= c.Duration;   // Animation.Apply: a looping animation's time %= duration
            // the baked pose at t: two decoded frames, linear
            float u = Math.Clamp(t * b.Rate, 0, c.Frames - 1); int f0 = (int)MathF.Floor(u); int f1 = Math.Min(f0 + 1, c.Frames - 1); float w = u - f0;
            var p0 = _cache.Get(b, c, f0); var p1 = _cache.Get(b, c, f1);
            float sx = (float)sk.GetType().GetProperty("ScaleX").GetValue(sk), sy = (float)sk.GetType().GetProperty("ScaleY").GetValue(sk), skx = GF(sk, "x"), sky = GF(sk, "y");
            var lw = comp.Go.Transform.LocalToWorld;
            var bones = G(sk, "bones"); int nb = Count(bones); if (nb != b.Bones) { _notBaked++; continue; }
            var bi = Items(bones);
            if (!_shadow.TryGetValue(sk, out var sh)) { sh = Activator.CreateInstance(sk.GetType(), G(sk, "data")); _shadow[sk] = sh; }
            var sbi = Items(G(sh, "bones"));
            if (!_stats.TryGetValue((b.Asset, clipName), out var s)) _stats[(b.Asset, clipName)] = s = new Stat();
            s.Frames++; _checked++;
            for (int i = 0; i < nb; i++)
            {
                var bo = bi.GetValue(i); if (G(bo, "active") is not true) continue;
                int o = i * 6;
                float ba = (p0[o] + (p1[o] - p0[o]) * w) * sx, bb = (p0[o + 1] + (p1[o + 1] - p0[o + 1]) * w) * sx, bc = (p0[o + 2] + (p1[o + 2] - p0[o + 2]) * w) * sy, bd = (p0[o + 3] + (p1[o + 3] - p0[o + 3]) * w) * sy;
                float bx = (p0[o + 4] + (p1[o + 4] - p0[o + 4]) * w) * sx + skx, by = (p0[o + 5] + (p1[o + 5] - p0[o + 5]) * w) * sy + sky;
                var sbo = sbi.GetValue(i); SF(sbo, "a", ba); SF(sbo, "b", bb); SF(sbo, "c", bc); SF(sbo, "d", bd); SF(sbo, "worldX", bx); SF(sbo, "worldY", by);
                double e = PxDist(in lw, in vp, in pr, gh, GF(bo, "worldX"), GF(bo, "worldY"), bx, by);
                if (e > s.MaxBonePx) { s.MaxBonePx = e; s.WorstBone = (string)G(G(bo, "data"), "name"); }
            }
            // every vertex the live slots show: live bones vs baked bones (the shadow skeleton, same attachment, same deform)
            var slots = G(sk, "slots"); int nsl = Count(slots); var sli = Items(slots); var ssli = Items(G(sh, "slots"));
            for (int si = 0; si < nsl; si++)
            {
                var sl = sli.GetValue(si); var att = G(sl, "attachment"); if (att == null || G(G(sl, "bone"), "active") is not true) continue;
                var ssl = ssli.GetValue(si);
                int nv;
                if (Is(att, "RegionAttachment"))
                {
                    var cwv = att.GetType().GetMethod("ComputeWorldVertices", new[] { G(sl, "bone").GetType(), typeof(float[]), typeof(int), typeof(int) });
                    nv = 8; cwv.Invoke(att, new object[] { G(sl, "bone"), _wa, 0, 2 }); cwv.Invoke(att, new object[] { G(ssl, "bone"), _wb, 0, 2 });
                }
                else if (Is(att, "MeshAttachment"))
                {
                    nv = (int)G(att, "worldVerticesLength"); if (_wa.Length < nv) { _wa = new float[nv * 2]; _wb = new float[nv * 2]; }
                    // the shadow slot shows the same attachment with the live deform
                    var sdef = G(ssl, "deform"); var ldef = G(sl, "deform"); CopyDeform(ldef, sdef); SetAttachment(ssl, att);
                    var cwv = att.GetType().GetMethod("ComputeWorldVertices", new[] { sl.GetType(), typeof(int), typeof(int), typeof(float[]), typeof(int), typeof(int) });
                    cwv.Invoke(att, new object[] { sl, 0, nv, _wa, 0, 2 }); cwv.Invoke(att, new object[] { ssl, 0, nv, _wb, 0, 2 });
                }
                else continue;
                for (int k = 0; k + 1 < nv; k += 2)
                {
                    double e = PxDist(in lw, in vp, in pr, gh, _wa[k], _wa[k + 1], _wb[k], _wb[k + 1]);
                    if (e > s.MaxVertPx) s.MaxVertPx = e;
                    s.Verts++;
                    if (Dbg && e > 1 && (DbgAsset == "1" || DbgAsset == b.Asset) && _dbg++ < 30)
                    {
                        var bo = G(sl, "bone"); var sbo = G(ssl, "bone");
                        Log.Info($"[spinebake-dbg] f{frame} '{b.Asset}'/'{clipName}' t={t:F4} slot {si} '{G(G(sl, "data"), "name")}' att {att.GetType().Name} '{att.GetType().GetProperty("Name")?.GetValue(att)}' v{k / 2}: live ({_wa[k]:F4},{_wa[k + 1]:F4}) baked ({_wb[k]:F4},{_wb[k + 1]:F4}) {e:F2} px; bone '{G(G(bo, "data"), "name")}' live a{GF(bo, "a"):F4} b{GF(bo, "b"):F4} c{GF(bo, "c"):F4} d{GF(bo, "d"):F4} x{GF(bo, "worldX"):F4} y{GF(bo, "worldY"):F4} | shadow a{GF(sbo, "a"):F4} b{GF(sbo, "b"):F4} c{GF(sbo, "c"):F4} d{GF(sbo, "d"):F4} x{GF(sbo, "worldX"):F4} y{GF(sbo, "worldY"):F4}; sx {sx} sy {sy} skel ({skx},{sky})");
                    }
                }
            }
            // bounds: the live mesh's box inside the clip's (flipped / scaled / moved like the bones)
            var mf = SceneRenderer.FirstMeshFilter(comp.Go);
            var mesh = mf == null ? null : Objects.Get<NMesh>(Conv.To(mf.Get("sharedMesh") ?? mf.Get("mesh"), typeof(Mesh)));
            if (mesh != null && mesh.Pos.Length > 0)
            {
                float bx0 = Math.Min(c.MinX * sx, c.MaxX * sx) + skx, bx1 = Math.Max(c.MinX * sx, c.MaxX * sx) + skx, by0 = Math.Min(c.MinY * sy, c.MaxY * sy) + sky, by1 = Math.Max(c.MinY * sy, c.MaxY * sy) + sky;
                double outPx = 0;
                foreach (var p in mesh.Pos)
                {
                    float ox = p.x < bx0 ? bx0 : p.x > bx1 ? bx1 : p.x, oy = p.y < by0 ? by0 : p.y > by1 ? by1 : p.y;
                    if (ox != p.x || oy != p.y) outPx = Math.Max(outPx, PxDist(in lw, in vp, in pr, gh, p.x, p.y, ox, oy));
                }
                if (outPx > 0) { s.BoundsBad++; s.MaxOutPx = Math.Max(s.MaxOutPx, outPx); }
            }
        }
    }
    static void CopyDeform(object from, object to)
    {
        int n = Count(from); var resize = to.GetType().GetMethod("Resize"); resize.Invoke(to, new object[] { n });
        Array.Copy(Items(from), Items(to), n);
    }
    static void SetAttachment(object slot, object att)
    {
        var f = Fi(slot.GetType(), "attachment"); f.SetValue(slot, att);
    }
    /// <summary>The screen distance of two skeleton-space points as the world pass projects them; 0 when either is off the screen (outside
    /// the frustum: behind the camera the projection's clamp magnifies any difference without bound, and nothing there is seen).</summary>
    static double PxDist(in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, float x0, float y0, float x1, float y1)
    {
        Vector4 ca = default, cb = default; bool outside = false;
        SceneRenderer.Project(in lw, in vp, in pr, gh, x0, y0, 0, out var ax, out var ay, ref ca, ref outside);
        SceneRenderer.Project(in lw, in vp, in pr, gh, x1, y1, 0, out var bx, out var by, ref cb, ref outside);
        if (outside || !OnScreen(ca) || !OnScreen(cb)) { _offScreen++; return 0; }
        double dx = ax - bx, dy = ay - by; return Math.Sqrt(dx * dx + dy * dy);
    }
    static bool OnScreen(in Vector4 c) => c.w > 1e-5f && c.x >= -c.w && c.x <= c.w && c.y >= -c.w && c.y <= c.w;
    static long _offScreen;
    static NBehaviour FirstScreenCamera()
    {
        foreach (var c in CameraIcalls.AllCameras()) if (CameraIcalls.TargetOf(c) == null) return c;
        return null;
    }
    static Baked Load(string asset)
    {
        if (_loaded.TryGetValue(asset, out var b)) return b;
        var p = Path.Combine(CheckDir, Safe(asset) + ".sbake");
        b = File.Exists(p) ? Read(p, asset) : null; _loaded[asset] = b; return b;
    }

    public static void Summary()
    {
        if (_totAssets > 0) Log.Info($"[spinebake] bake totals: {_totAssets} assets, {_totBytes / 1048576.0:F2} MiB as v{Format} (v1 {_totV1 / 1048576.0:F2} MiB, {(double)_totV1 / Math.Max(1, _totBytes):F1}x); round trip: {_totRt} values differ");
        if (CheckDir == null || _stats.Count == 0) { if (CheckDir != null) Log.Info($"[spinebake] check: nothing checked ({_notOne} skeleton-frames with mixing / several tracks, {_notBaked} without a bake)"); return; }
        Log.Info($"[spinebake] check at {_loaded.Values.FirstOrDefault(x => x != null)?.Rate} Hz: {_checked:N0} skeleton-frames compared ({_notOne:N0} skipped: mixing or several tracks; {_notApplied:N0} before the entry's first apply; {_notBaked:N0} without a bake; {_offScreen:N0} point pairs off the screen not measured); decode cache {_cache.Hits:N0} hits / {_cache.Misses:N0} misses");
        double worstV = _stats.Values.Max(x => x.MaxVertPx), worstB = _stats.Values.Max(x => x.MaxBonePx);
        Log.Info($"[spinebake]   worst vertex error {worstV:F4} px, worst bone origin error {worstB:F4} px (acceptance < 0.1 px); clips over 0.1 px: {_stats.Count(x => x.Value.MaxVertPx >= 0.1)} of {_stats.Count}; bounds violated in {_stats.Values.Sum(x => x.BoundsBad):N0} skeleton-frames (worst {_stats.Values.Max(x => x.MaxOutPx):F2} px outside)");
        foreach (var ((a, cl), s) in _stats.OrderByDescending(x => x.Value.MaxVertPx).Take(40))
            Log.Info($"[spinebake]   '{a}' / '{cl}': {s.Frames} frames, {s.Verts:N0} vertices; max vertex {s.MaxVertPx:F4} px, max bone {s.MaxBonePx:F4} px ({s.WorstBone}); bounds out {s.BoundsBad} ({s.MaxOutPx:F2} px)");
    }

    // ================= (3) read order: a cache model of the decode loop =================
    static void SimulateReadOrder(Baked b)
    {
        // each clip decoded frame after frame (the playback order); the store's bytes at their offsets in (a) read order: [frame][bone][6]
        // uint16, (b) curve order: [bone][component][frame]; the decoded floats written to a 6 x bones float buffer (the decode cache's slot)
        long missA = 0, missB = 0, frames = 0;
        var la = new L1(); var lb = new L1();
        foreach (var c in b.Clips)
        {
            long baseQ = 1L << 20, baseMin = 1L << 28, baseOut = 1L << 30; int n6 = b.Bones * 6;
            for (int f = 0; f < c.Frames; f++, frames++)
                for (int j = 0; j < n6; j++)
                {
                    la.Access(baseMin + j * 4); la.Access(baseMin + (n6 + j) * 4); la.Access(baseQ + ((long)f * n6 + j) * 2); la.Access(baseOut + j * 4);
                    lb.Access(baseMin + j * 4); lb.Access(baseMin + (n6 + j) * 4); lb.Access(baseQ + ((long)j * c.Frames + f) * 2); lb.Access(baseOut + j * 4);
                }
            baseQ += (long)c.Frames * n6 * 2;
        }
        missA = la.Misses; missB = lb.Misses;
        Log.Info($"[spinebake]   '{b.Asset}' decode loop, L1 model (32 KiB, 4-way, 32 B lines): read order {missA:N0} misses ({missA / (double)Math.Max(1, frames):F1} per frame of {b.Bones} bones), curve order {missB:N0} ({missB / (double)Math.Max(1, frames):F1} per frame) over {frames:N0} frames");
    }
    sealed class L1
    {
        const int Ways = 4, Line = 32, Sets = 32 * 1024 / Line / Ways;
        readonly long[,] _tag = new long[Sets, Ways]; readonly long[,] _use = new long[Sets, Ways]; long _clock; public long Misses;
        public L1() { for (int s = 0; s < Sets; s++) for (int w = 0; w < Ways; w++) _tag[s, w] = -1; }
        public void Access(long addr)
        {
            long line = addr / Line; int set = (int)(line % Sets); _clock++;
            int lru = 0;
            for (int w = 0; w < Ways; w++) { if (_tag[set, w] == line) { _use[set, w] = _clock; return; } if (_use[set, w] < _use[set, lru]) lru = w; }
            Misses++; _tag[set, lru] = line; _use[set, lru] = _clock;
        }
    }
}
#else
[ProfileOnly]
public static class SpineBake { public static readonly bool On = false; public static void After(int frame) { } public static void Summary() { } }
#endif
