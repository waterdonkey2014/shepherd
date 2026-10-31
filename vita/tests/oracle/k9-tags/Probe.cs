// kit #9 fidelity review: what does Unity's player answer for GameObject.FindGameObjectsWithTag("Untagged")? (loaded by OracleMain -ilsuite)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

public static class ShepherdConformance
{
    static string Names(GameObject[] a) => a == null ? "null" : a.Length + " [" + string.Join(",", a.Select(g => g.name).OrderBy(s => s, StringComparer.Ordinal).ToArray()) + "]";
    static void Line(StringBuilder sb, string k, Func<string> f)
    {
        string v; try { v = f(); } catch (Exception e) { v = "!" + e.GetType().Name + ": " + e.Message; }
        sb.Append(k).Append('\t').Append(v).Append('\n');
    }
    public static int Run(string dir, StringBuilder sb)
    {
        var made = new List<GameObject>();
        int n = 0;
        try
        {
            var all0 = UnityEngine.Object.FindObjectsOfType<GameObject>();
            Line(sb, "sceneActiveBefore", () => Names(all0)); n++;
            Line(sb, "sceneUntaggedActiveBefore", () => Names(all0.Where(g => g.CompareTag("Untagged")).ToArray())); n++;
            var u1 = new GameObject("k9u1"); made.Add(u1);
            var u2 = new GameObject("k9u2"); made.Add(u2); u2.tag = "Untagged";
            var u3 = new GameObject("k9u3inactive"); made.Add(u3); u3.SetActive(false);
            var ch = new GameObject("k9child"); made.Add(ch); ch.transform.SetParent(u1.transform, false);
            var p = new GameObject("k9player"); made.Add(p); p.tag = "Player";
            var q = new GameObject("k9wasPlayer"); made.Add(q); q.tag = "Player"; q.tag = "Untagged";
            Line(sb, "FindGameObjectsWithTag(Untagged)", () => Names(GameObject.FindGameObjectsWithTag("Untagged"))); n++;
            Line(sb, "FindWithTag(Untagged)", () => { var g = GameObject.FindWithTag("Untagged"); return g == null ? "null" : g.name; }); n++;
            Line(sb, "FindGameObjectWithTag(Untagged)", () => { var g = GameObject.FindGameObjectWithTag("Untagged"); return g == null ? "null" : g.name; }); n++;
            Line(sb, "FindGameObjectsWithTag(Player)", () => Names(GameObject.FindGameObjectsWithTag("Player"))); n++;
            Line(sb, "FindGameObjectsWithTag(MainCamera)", () => Names(GameObject.FindGameObjectsWithTag("MainCamera"))); n++;
            Line(sb, "FindGameObjectsWithTag(Respawn)", () => Names(GameObject.FindGameObjectsWithTag("Respawn"))); n++;
            Line(sb, "FindGameObjectsWithTag(NoSuchTag)", () => Names(GameObject.FindGameObjectsWithTag("NoSuchTag"))); n++;
            Line(sb, "FindGameObjectsWithTag(null)", () => Names(GameObject.FindGameObjectsWithTag(null))); n++;
        }
        finally { foreach (var g in made) if (g != null) UnityEngine.Object.DestroyImmediate(g); }
        return n;
    }
}
