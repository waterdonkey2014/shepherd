using System.Globalization;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Impl;

/// <summary>Small icalls that gate game flow when left at their default: splash state (Unify engagement waits on it),
/// frustum tests (culling), layer names, html colors, particle liveness.</summary>
public static class GateIcalls
{
    // No Unity splash in Shepherd: it is always finished. (Default false blocked Unify.Engagement forever.)
    [Icall("UnityEngine.Rendering.SplashScreen::get_isFinished")] static object SplashDone(object[] a) => true;
    [Icall("UnityEngine.SystemInfo::GetPhysicalMemoryMB")] static object PhysMem(object[] a) => 16384;

    // Unity: AABB is outside if it lies entirely behind any plane (normal points inward).
    // menu-speed hop fix: the bounds' centre / extents and each plane's normal / distance are read as fields (Um's mirrors) and
    // Vector3.Dot(n, c) is our own n.x * c.x + n.y * c.y + n.z * c.z (Dot's order); the expression and the test are unchanged.
    [Icall("UnityEngine.GeometryUtility::TestPlanesAABB_Injected")]
    static object TestAABB(object[] a)
    {
        if (a[0] is not Plane[] planes || a[1] is not Bounds b) return Runtime.Boxes.True;
        var bf = Um.Of(b); var c = bf.C; var e = bf.E;
        bool chk = HopCheck.On;
        if (chk) { HopCheck.V3(HopCheck.Aabb, c, b.center); HopCheck.V3(HopCheck.Aabb, e, b.extents); }
        for (int i = 0; i < planes.Length; i++)   // (was foreach: the same order)
        {
            var pf = Um.Of(planes[i]); var n = pf.N; float r = e.x * Math.Abs(n.x) + e.y * Math.Abs(n.y) + e.z * Math.Abs(n.z);
            float v = n.x * c.x + n.y * c.y + n.z * c.z + pf.D + r;   // Vector3.Dot(n, c) + p.distance + r
            if (chk)
            {   // the expression as it was, through Unity's getters and Vector3.Dot
                var p = planes[i]; var un = p.normal; var uc = b.center; var ue = b.extents; float ur = ue.x * Math.Abs(un.x) + ue.y * Math.Abs(un.y) + ue.z * Math.Abs(un.z);
                HopCheck.F(HopCheck.Aabb, v, Vector3.Dot(un, uc) + p.distance + ur);
            }
            if (v < 0) return Runtime.Boxes.False;
        }
        return Runtime.Boxes.True;
    }

    [Icall("UnityEngine.LayerMask::NameToLayer")] static object NameToLayer(object[] a) { var n = a[0] as string; var i = Assets.Loader.LayerNames.IndexOf(n); return string.IsNullOrEmpty(n) ? -1 : i; }
    [Icall("UnityEngine.LayerMask::LayerToName")] static object LayerToName(object[] a) { int i = (int)a[0]; return i >= 0 && i < Assets.Loader.LayerNames.Count ? Assets.Loader.LayerNames[i] : ""; }

    static readonly Dictionary<string, Color32> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = new(255, 0, 0, 255), ["cyan"] = new(0, 255, 255, 255), ["blue"] = new(0, 0, 255, 255), ["darkblue"] = new(0, 0, 160, 255),
        ["lightblue"] = new(173, 216, 230, 255), ["purple"] = new(128, 0, 128, 255), ["yellow"] = new(255, 255, 0, 255), ["lime"] = new(0, 255, 0, 255),
        ["fuchsia"] = new(255, 0, 255, 255), ["white"] = new(255, 255, 255, 255), ["silver"] = new(192, 192, 192, 255), ["grey"] = new(128, 128, 128, 255),
        ["gray"] = new(128, 128, 128, 255), ["black"] = new(0, 0, 0, 255), ["orange"] = new(255, 165, 0, 255), ["brown"] = new(165, 42, 42, 255),
        ["maroon"] = new(128, 0, 0, 255), ["green"] = new(0, 128, 0, 255), ["olive"] = new(128, 128, 0, 255), ["navy"] = new(0, 0, 128, 255),
        ["teal"] = new(0, 128, 128, 255), ["aqua"] = new(0, 255, 255, 255), ["magenta"] = new(255, 0, 255, 255),
    };
    // Unity accepts #RGB #RGBA #RRGGBB #RRGGBBAA and named colors; returns false (color = default) otherwise.
    [Icall("UnityEngine.ColorUtility::DoTryParseHtmlColor")]
    static object ParseHtml(object[] a)
    {
        a[1] = default(Color32); var s = (a[0] as string)?.Trim(); if (string.IsNullOrEmpty(s)) return false;
        if (s[0] != '#') { if (Named.TryGetValue(s, out var nc)) { a[1] = nc; return true; } return false; }
        var h = s[1..]; if (!uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        byte X(uint n) => (byte)(n * 17);
        switch (h.Length)
        {
            case 3: a[1] = new Color32(X(v >> 8 & 15), X(v >> 4 & 15), X(v & 15), 255); return true;
            case 4: a[1] = new Color32(X(v >> 12 & 15), X(v >> 8 & 15), X(v >> 4 & 15), X(v & 15)); return true;
            case 6: a[1] = new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255); return true;
            case 8: a[1] = new Color32((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v); return true;
        }
        return false;
    }

    // No particle simulation yet: systems are never alive/playing (lets "wait until particles finish" code proceed).
    [Icall("UnityEngine.ParticleSystem::IsAlive")] static object PAlive(object[] a) => false;
    [Icall("UnityEngine.ParticleSystem::get_isPlaying")] static object PPlaying(object[] a) => false;
}
