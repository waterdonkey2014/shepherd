// kit #9 fidelity, second review (vita/notes/kit9-fidelity.md section 13): what Unity's player answers for (1) Random.seed after draws /
// InitState / set_seed / set_state, against Random.state; (2) MaterialPropertyBlock reads across the colour / vector / float types
// (SetVector -> GetColor, SetColor -> GetVector, ...) and the player's colour space. Loaded by OracleMain -ilsuite (run.sh); floats as bits.
using System;
using System.Reflection;
using System.Text;
using UnityEngine;

public static class ShepherdConformance
{
    static string H(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("x8");
    static string V(Vector4 v) => H(v.x) + "," + H(v.y) + "," + H(v.z) + "," + H(v.w);
    static string C(Color c) => H(c.r) + "," + H(c.g) + "," + H(c.b) + "," + H(c.a);
    static readonly FieldInfo[] S = { F("s0"), F("s1"), F("s2"), F("s3") };
    static FieldInfo F(string n) => typeof(UnityEngine.Random.State).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    static string St() { object st = UnityEngine.Random.state; var sb = new StringBuilder(); foreach (var f in S) sb.Append(sb.Length > 0 ? "," : "").Append(((int)f.GetValue(st)).ToString("x8")); return sb.ToString(); }
#pragma warning disable 618
    static string Seed() => UnityEngine.Random.seed.ToString("x8");
#pragma warning restore 618
    static void Line(StringBuilder sb, string k, Func<string> f)
    {
        string v; try { v = f(); } catch (Exception e) { v = "!" + e.GetType().Name + ": " + e.Message; }
        sb.Append(k).Append('\t').Append(v).Append('\n');
    }
    public static int Run(string dir, StringBuilder sb)
    {
        int n = 0;
        // ---- Random.seed ----
        Line(sb, "start: seed==s0", () => (Seed() == St().Substring(0, 8)).ToString()); n++;
        UnityEngine.Random.InitState(12345);
        Line(sb, "InitState(12345): seed | state", () => Seed() + " | " + St()); n++;
        float x = UnityEngine.Random.value;
        Line(sb, "after 1 value: seed | state", () => Seed() + " | " + St()); n++;
        for (int i = 0; i < 5; i++) x += UnityEngine.Random.Range(0f, 1f);
        Line(sb, "after 6 draws: seed | state", () => Seed() + " | " + St()); n++;
        x += UnityEngine.Random.Range(0, 100);
        Line(sb, "after 7 draws (one RangeInt): seed | state", () => Seed() + " | " + St()); n++;
        var saved = UnityEngine.Random.state;
#pragma warning disable 618
        UnityEngine.Random.seed = 7;
#pragma warning restore 618
        Line(sb, "set_seed(7): seed | state", () => Seed() + " | " + St()); n++;
        UnityEngine.Random.InitState(-5);
        Line(sb, "InitState(-5): seed | state", () => Seed() + " | " + St()); n++;
        UnityEngine.Random.state = saved;
        Line(sb, "set_state(saved): seed | state", () => Seed() + " | " + St()); n++;
        Line(sb, "insideUnitSphere then seed | state", () => { var v = UnityEngine.Random.insideUnitSphere; return Seed() + " | " + St(); }); n++;
        Line(sb, "rotationUniform then seed | state", () => { var q = UnityEngine.Random.rotationUniform; return Seed() + " | " + St(); }); n++;
        Line(sb, "(sum, keeps the draws)", () => H(x)); n++;
        // ---- MaterialPropertyBlock across types ----
        Line(sb, "activeColorSpace", () => QualitySettings.activeColorSpace.ToString()); n++;
        var b = new MaterialPropertyBlock();
        var v4 = new Vector4(0.5f, 0.25f, 0.75f, 0.6f); var c4 = new Color(0.5f, 0.25f, 0.75f, 0.6f); var big = new Vector4(-3.5f, 2f, 1e-3f, 7f);
        b.SetVector("_SV", v4); b.SetVector("_SVB", big); b.SetColor("_SC", c4); b.SetFloat("_SF", 0.375f); b.SetInt("_SI", 9);
        Line(sb, "SetVector -> GetColor", () => C(b.GetColor("_SV"))); n++;
        Line(sb, "SetVector(big) -> GetColor", () => C(b.GetColor("_SVB"))); n++;
        Line(sb, "SetVector -> GetVector", () => V(b.GetVector("_SV"))); n++;
        Line(sb, "SetVector -> GetFloat", () => H(b.GetFloat("_SV"))); n++;
        Line(sb, "SetColor -> GetVector", () => V(b.GetVector("_SC"))); n++;
        Line(sb, "SetColor -> GetColor", () => C(b.GetColor("_SC"))); n++;
        Line(sb, "SetColor -> GetFloat", () => H(b.GetFloat("_SC"))); n++;
        Line(sb, "SetFloat -> GetVector", () => V(b.GetVector("_SF"))); n++;
        Line(sb, "SetFloat -> GetColor", () => C(b.GetColor("_SF"))); n++;
        Line(sb, "SetInt -> GetVector", () => V(b.GetVector("_SI"))); n++;
        Line(sb, "SetInt -> GetColor", () => C(b.GetColor("_SI"))); n++;
        Line(sb, "missing -> GetVector", () => V(b.GetVector("_None"))); n++;
        Line(sb, "missing -> GetColor", () => C(b.GetColor("_None"))); n++;
        Line(sb, "HasVector(SC) HasColor(SV) HasFloat(SV) HasVector(SF)", () => b.HasVector("_SC") + " " + b.HasColor("_SV") + " " + b.HasFloat("_SV") + " " + b.HasVector("_SF")); n++;
        Line(sb, "HasProperty(SV) HasProperty(SC)", () => b.HasProperty("_SV") + " " + b.HasProperty("_SC")); n++;
        b.SetColor("_SV", c4);
        Line(sb, "SetVector then SetColor same id -> GetVector", () => V(b.GetVector("_SV"))); n++;
        b.SetVector("_SC", big);
        Line(sb, "SetColor then SetVector same id -> GetColor", () => C(b.GetColor("_SC"))); n++;
        b.SetFloat("_SV", 0.125f);
        Line(sb, "vector then SetFloat same id -> GetVector | GetFloat", () => V(b.GetVector("_SV")) + " | " + H(b.GetFloat("_SV"))); n++;
        return n;
    }
}
