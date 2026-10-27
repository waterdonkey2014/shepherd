using System.Reflection;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Impl;

/// <summary>Fidelity round 1 (survey item 8): the native half of UnityEngine.U2D.SpriteShapeRenderer. The 2D SpriteShape package
/// (managed, the game's own copy) generates a shape's geometry in a job (SpriteShapeGenerator) straight into buffers the renderer
/// hands out (GetSegments / GetChannels / GetBounds -> GetDataInfo / GetChannelInfo), then calls Prepare with the shape's parameters
/// and sprite array; Unity's renderer draws one submesh per segment: segment 0 is the fill (material 0, the fill texture), the rest
/// are edge / corner strips (material 1, the texture of spriteArray[spriteIndex]). Segment vertices and indices lie back to back in
/// the buffers; a segment's indices are relative to its first vertex. Before this the icalls were missing (SetLocalAABB ×18) and the
/// intro's island ground, stone path and back edge were never drawn. The buffers live on the process heap (one block per channel),
/// re-made when a bake asks for more; nothing here reads a property-bag key ([ProfileOnly]).</summary>
[ProfileOnly]
public static unsafe class SpriteShapeIcalls
{
    public sealed class Data
    {
        public IntPtr Pos, Uv, Tan, Nrm, Col, Idx, Seg, Bounds; public int VCap, ICap, SegCap, Segs;
        public Texture2D Fill; public Sprite[] Sprites = Array.Empty<Sprite>(); public bool Prepared; public int Version;
        public Bounds LocalAabb; public bool AabbSet;
        /// <summary>SceneRenderer's culling box of the generated geometry, as of Version CullVer.</summary>
        internal int CullVer = -1; internal Vector3 CullMin, CullMax; internal bool CullEmpty;
    }
    static readonly Dictionary<NObj, Data> _data = new();
    public static Data Of(NObj r) => r != null && _data.TryGetValue(r, out var d) ? d : null;
    static Data Get(object o)
    {
        var r = Objects.Get(o); if (r == null) return null;
        if (!_data.TryGetValue(r, out var d)) _data[r] = d = new Data();
        return d;
    }
    public static void Forget(NObj r) { if (r != null && _data.Remove(r, out var d)) FreeAll(d); }
    static void FreeAll(Data d)
    {
        foreach (var p in new[] { d.Pos, d.Uv, d.Tan, d.Nrm, d.Col, d.Idx, d.Seg, d.Bounds }) if (p != IntPtr.Zero) NativeMemory.Free((void*)p);
        d.Pos = d.Uv = d.Tan = d.Nrm = d.Col = d.Idx = d.Seg = d.Bounds = IntPtr.Zero; d.VCap = d.ICap = d.SegCap = 0;
    }
    static IntPtr Grow(IntPtr p, long bytes) { if (p != IntPtr.Zero) NativeMemory.Free((void*)p); var q = NativeMemory.AllocZeroed((nuint)Math.Max(16, bytes)); return (IntPtr)q; }

    const int SegBytes = 16;   // SpriteShapeSegment: geomIndex, indexCount, vertexCount, spriteIndex
    [Icall("UnityEngine.U2D.SpriteShapeRenderer::SetSegmentCount")] static object SetSegmentCount(object[] a)
    {
        var d = Get(a[0]); if (d == null) return null; int n = Math.Max(1, (int)a[1]);
        if (n > d.SegCap) { d.Seg = Grow(d.Seg, (long)n * SegBytes); d.SegCap = n; }
        NativeMemory.Clear((void*)d.Seg, (nuint)((long)d.SegCap * SegBytes));
        d.Segs = n; return null;
    }
    static void Vertices(Data d, int vc, int ic, int mask)
    {
        vc = Math.Max(1, vc); ic = Math.Max(1, ic);
        if (vc > d.VCap)
        {
            d.Pos = Grow(d.Pos, vc * 12L); d.Uv = Grow(d.Uv, vc * 8L);
            if (d.Tan != IntPtr.Zero) { NativeMemory.Free((void*)d.Tan); d.Tan = IntPtr.Zero; }
            if (d.Nrm != IntPtr.Zero) { NativeMemory.Free((void*)d.Nrm); d.Nrm = IntPtr.Zero; }
            if (d.Col != IntPtr.Zero) { NativeMemory.Free((void*)d.Col); d.Col = IntPtr.Zero; }
            d.VCap = vc;
        }
        if ((mask & 4) != 0 && d.Tan == IntPtr.Zero) d.Tan = Grow(IntPtr.Zero, d.VCap * 16L);
        if ((mask & 2) != 0 && d.Nrm == IntPtr.Zero) d.Nrm = Grow(IntPtr.Zero, d.VCap * 12L);
        if ((mask & 8) != 0 && d.Col == IntPtr.Zero) d.Col = Grow(IntPtr.Zero, d.VCap * 4L);
        if (ic > d.ICap) { d.Idx = Grow(d.Idx, ic * 2L); d.ICap = ic; }
        if (d.Bounds == IntPtr.Zero) d.Bounds = Grow(IntPtr.Zero, 24);
    }
    [Icall("UnityEngine.U2D.SpriteShapeRenderer::SetMeshDataCount")] static object SetMeshDataCount(object[] a) { var d = Get(a[0]); if (d != null) Vertices(d, (int)a[1], (int)a[2], 0); return null; }
    [Icall("UnityEngine.U2D.SpriteShapeRenderer::SetMeshChannelInfo")] static object SetMeshChannelInfo(object[] a) { var d = Get(a[0]); if (d != null) Vertices(d, (int)a[1], (int)a[2], (int)a[3]); return null; }

    static readonly Type ChannelInfoType = typeof(UnityEngine.U2D.SpriteShapeRenderer).Assembly.GetType("UnityEngine.U2D.SpriteChannelInfo")
        ?? typeof(Sprite).Assembly.GetType("UnityEngine.U2D.SpriteChannelInfo");
    static readonly FieldInfo CiBuf = ChannelInfoType?.GetField("m_Buffer", BindingFlags.Instance | BindingFlags.NonPublic), CiCount = ChannelInfoType?.GetField("m_Count", BindingFlags.Instance | BindingFlags.NonPublic),
        CiOff = ChannelInfoType?.GetField("m_Offset", BindingFlags.Instance | BindingFlags.NonPublic), CiStride = ChannelInfoType?.GetField("m_Stride", BindingFlags.Instance | BindingFlags.NonPublic);
    static object Info(IntPtr buf, int count, int stride)
    {
        var o = Activator.CreateInstance(ChannelInfoType);
        CiBuf.SetValue(o, buf); CiCount.SetValue(o, count); CiOff.SetValue(o, 0); CiStride.SetValue(o, stride);
        return o;
    }
    /// <summary>GetDataInfo_Injected(SpriteShapeDataType type, out SpriteChannelInfo ret): Index, Segment, BoundingBox (+ the channels).</summary>
    [Icall("UnityEngine.U2D.SpriteShapeRenderer::GetDataInfo_Injected")] static object GetDataInfo(object[] a)
    {
        var d = Get(a[0]); int type = Convert.ToInt32(a[1]);
        if (d == null) { a[2] = Info(IntPtr.Zero, 0, 0); return null; }
        if (d.Bounds == IntPtr.Zero) d.Bounds = Grow(IntPtr.Zero, 24);
        a[2] = type switch
        {
            0 => Info(d.Idx, d.ICap, 2), 1 => Info(d.Seg, d.Segs, SegBytes), 2 => Info(d.Bounds, 1, 24),
            3 => Info(d.Pos, d.VCap, 12), 4 => Info(d.Uv, d.VCap, 8), 5 => Info(d.Nrm, d.Nrm == IntPtr.Zero ? 0 : d.VCap, 12),
            6 => Info(d.Tan, d.Tan == IntPtr.Zero ? 0 : d.VCap, 16), 7 => Info(d.Col, d.Col == IntPtr.Zero ? 0 : d.VCap, 4), _ => Info(IntPtr.Zero, 0, 0),
        };
        return null;
    }
    /// <summary>GetChannelInfo_Injected(VertexAttribute channel, out SpriteChannelInfo ret): one tightly packed block per channel.</summary>
    [Icall("UnityEngine.U2D.SpriteShapeRenderer::GetChannelInfo_Injected")] static object GetChannelInfo(object[] a)
    {
        var d = Get(a[0]); var ch = (UnityEngine.Rendering.VertexAttribute)Convert.ToInt32(a[1]);
        if (d == null) { a[2] = Info(IntPtr.Zero, 0, 0); return null; }
        a[2] = ch switch
        {
            UnityEngine.Rendering.VertexAttribute.Position => Info(d.Pos, d.VCap, 12),
            UnityEngine.Rendering.VertexAttribute.TexCoord0 => Info(d.Uv, d.VCap, 8),
            UnityEngine.Rendering.VertexAttribute.Tangent => Info(d.Tan, d.Tan == IntPtr.Zero ? 0 : d.VCap, 16),
            UnityEngine.Rendering.VertexAttribute.Normal => Info(d.Nrm, d.Nrm == IntPtr.Zero ? 0 : d.VCap, 12),
            UnityEngine.Rendering.VertexAttribute.Color => Info(d.Col, d.Col == IntPtr.Zero ? 0 : d.VCap, 4),
            _ => Info(IntPtr.Zero, 0, 0),
        };
        return null;
    }
    /// <summary>Prepare_Injected(ref JobHandle handle, ref SpriteShapeParameters shapeParams, Sprite[] sprites): the job is already
    /// complete (JobIcalls runs jobs at schedule time); keep the fill texture and the sprites the segments index, and bump the
    /// geometry version so the renderer re-reads the buffers.</summary>
    [Icall("UnityEngine.U2D.SpriteShapeRenderer::Prepare_Injected")] static object Prepare(object[] a)
    {
        var d = Get(a[0]); if (d == null) return null;
        var p = (UnityEngine.U2D.SpriteShapeParameters)a[2];
        d.Fill = p.fillTexture; d.Sprites = a[3] as Sprite[] ?? Array.Empty<Sprite>(); d.Prepared = true; d.Version++;
        return null;
    }
    [Icall("UnityEngine.U2D.SpriteShapeRenderer::SetLocalAABB_Injected")] static object SetLocalAabb(object[] a)
    {
        var d = Get(a[0]); if (d != null) { d.LocalAabb = (Bounds)a[1]; d.AabbSet = true; }
        return null;
    }

    // ---------------- reading the geometry (SceneRenderer) ----------------
    public readonly struct Segment { public readonly int V0, Vn, I0, In, Sprite, Geom; public Segment(int v0, int vn, int i0, int n, int s, int g) { V0 = v0; Vn = vn; I0 = i0; In = n; Sprite = s; Geom = g; } }
    /// <summary>The generated segments in buffer order, with their vertex and index offsets; empty until Prepare ran.</summary>
    public static int Segments(Data d, List<Segment> into)
    {
        into.Clear(); if (d == null || !d.Prepared || d.Seg == IntPtr.Zero) return 0;
        int* s = (int*)d.Seg; int v0 = 0, i0 = 0;
        for (int k = 0; k < d.Segs; k++)
        {
            int geom = s[k * 4], ic = s[k * 4 + 1], vc = s[k * 4 + 2], spr = s[k * 4 + 3];
            if (ic <= 0 || vc <= 0) continue;
            if (v0 + vc > d.VCap || i0 + ic > d.ICap) break;   // a bake that ran out of room (the generator reports it)
            into.Add(new Segment(v0, vc, i0, ic, spr, geom));
            v0 += vc; i0 += ic;
        }
        return into.Count;
    }
    public static Vector3* Positions(Data d) => (Vector3*)d.Pos;
    public static Vector2* Uvs(Data d) => (Vector2*)d.Uv;
    public static ushort* Indices(Data d) => (ushort*)d.Idx;
}
