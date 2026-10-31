using System.Collections;
using Shepherd.Assets;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Assets;

/// <summary>Unity 2022 Mesh: m_VertexData (channels × streams) + m_IndexBuffer + m_SubMeshes -> NMesh.</summary>
static class MeshDecode
{
    static object V(TypeTreeObject t, string k) => t != null && t.TryGet(k, out var v) ? v : null;
    static int I(TypeTreeObject t, string k) => V(t, k) is { } v ? Convert.ToInt32(v) : 0;
    static readonly int[] FmtSize = { 4, 2, 1, 1, 2, 2, 1, 1, 2, 2, 4, 4 };

    public static void Fill(NMesh m, TypeTreeObject d, SerializedFile f)
    {
        // kit #9 fidelity review (stand-in fix "mesh"): the asset's serialized index format, for Mesh.indexFormat -- the fix answers the record's
        // "indexFormat" key, else UInt16, so a 32-bit-index asset must carry its key (Unity: UInt32; kit #8 answered UInt32 for every mesh);
        // with the fix off nothing is stored (kit #8)
        if (Runtime.K9Fidelity.FixMesh && I(d, "m_IndexFormat") == 1) m.Set("indexFormat", UnityEngine.Rendering.IndexFormat.UInt32);
        var vd = V(d, "m_VertexData") as TypeTreeObject; if (vd == null) return;
        int vc = I(vd, "m_VertexCount"); var data = V(vd, "m_DataSize") as byte[];
        if ((data == null || data.Length == 0) && V(d, "m_StreamData") is TypeTreeObject sd && I(sd, "size") > 0)
            data = Loader.Db.ReadStreamedData(f, V(sd, "path") as string, Convert.ToInt64(V(sd, "offset")), I(sd, "size"));
        if (data == null || vc == 0) return;
        var chans = (V(vd, "m_Channels") as IList ?? Array.Empty<object>()).OfType<TypeTreeObject>()
            .Select(c => (stream: I(c, "stream"), offset: I(c, "offset"), format: I(c, "format"), dim: I(c, "dimension") & 0xF)).ToArray();
        int streams = chans.Length == 0 ? 0 : chans.Where(c => c.dim > 0).Select(c => c.stream).DefaultIfEmpty(0).Max() + 1;
        var stride = new int[Math.Max(1, streams)]; var start = new int[Math.Max(1, streams)];
        foreach (var c in chans) if (c.dim > 0) stride[c.stream] += c.dim * FmtSize[Math.Clamp(c.format, 0, 11)];
        int acc = 0; for (int s = 0; s < streams; s++) { start[s] = acc; acc += stride[s] * vc; acc = (acc + 15) & ~15; }
        float Read(int s, int vi, int off, int fmt, int comp)
        {
            int p = start[s] + vi * stride[s] + off + comp * FmtSize[fmt];
            if (p + FmtSize[fmt] > data.Length) return 0;
            return fmt switch { 0 => BitConverter.ToSingle(data, p), 1 => (float)BitConverter.ToHalf(data, p), 2 => data[p] / 255f, 3 => (sbyte)data[p] / 127f, 4 => BitConverter.ToUInt16(data, p) / 65535f, _ => 0 };
        }
        if (chans.Length > 0 && chans[0].dim > 0) { var c = chans[0]; m.Pos = new Vector3[vc]; for (int i = 0; i < vc; i++) m.Pos[i] = new Vector3(Read(c.stream, i, c.offset, c.format, 0), Read(c.stream, i, c.offset, c.format, 1), c.dim > 2 ? Read(c.stream, i, c.offset, c.format, 2) : 0); }
        if (chans.Length > 3 && chans[3].dim > 0) { var c = chans[3]; m.Col = new Color32[vc]; for (int i = 0; i < vc; i++) m.Col[i] = new Color(Read(c.stream, i, c.offset, c.format, 0), Read(c.stream, i, c.offset, c.format, 1), Read(c.stream, i, c.offset, c.format, 2), c.dim > 3 ? Read(c.stream, i, c.offset, c.format, 3) : 1); }
        if (chans.Length > 4 && chans[4].dim > 0) { var c = chans[4]; m.Uv0 = new Vector4[vc]; for (int i = 0; i < vc; i++) m.Uv0[i] = new Vector4(Read(c.stream, i, c.offset, c.format, 0), c.dim > 1 ? Read(c.stream, i, c.offset, c.format, 1) : 0, 0, 0); }
        var ib = V(d, "m_IndexBuffer") as byte[] ?? Array.Empty<byte>(); bool u32 = I(d, "m_IndexFormat") == 1;
        m.Sub.Clear(); m.SubTopology.Clear();
        foreach (var sm in (V(d, "m_SubMeshes") as IList ?? Array.Empty<object>()).OfType<TypeTreeObject>())
        {
            int first = I(sm, "firstByte"), count = I(sm, "indexCount"), bv = I(sm, "baseVertex"); var idx = new int[count];
            for (int i = 0; i < count; i++) { int p = first + i * (u32 ? 4 : 2); if (p + (u32 ? 4 : 2) > ib.Length) break; idx[i] = (u32 ? (int)BitConverter.ToUInt32(ib, p) : BitConverter.ToUInt16(ib, p)) + bv; }
            m.Sub.Add(idx); m.SubTopology.Add(I(sm, "topology"));
        }
        if (m.Sub.Count == 0) { m.Sub.Add(Array.Empty<int>()); m.SubTopology.Add(0); }
        m.Recalc(); m.Version++;
    }
}
