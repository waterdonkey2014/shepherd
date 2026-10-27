// Kit #7 tests track: MACHINE-DRAFTED probe candidates (see D_Objects.cs). Area: UI components the game's menus lean on -- Canvas root /
// nesting / sorting properties, CanvasGroup alpha and interactable inheritance, RectTransform under a Canvas (no screen-size inputs).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShepherdOracle.Drafted
{
    /// <summary>Canvas: root canvas resolution through nesting, overrideSorting / sortingOrder / sortingLayerName / renderMode round trips,
    /// referencePixelsPerUnit, isRootCanvas after re-parenting.</summary>
    public static class D_Canvas
    {
        public static void Register() { Suites.All["d-ui-canvas"] = Case; }
        static void Case(Rng r, Out o)
        {
            var made = new List<GameObject>();
            try
            {
                var a = new GameObject("ca", typeof(RectTransform)); made.Add(a); var ca = a.AddComponent<Canvas>();
                var b = new GameObject("cb", typeof(RectTransform)); made.Add(b); b.transform.SetParent(a.transform, false); var cb = b.AddComponent<Canvas>();
                var c = new GameObject("cc", typeof(RectTransform)); made.Add(c); c.transform.SetParent(b.transform, false); var cc = c.AddComponent<Canvas>();
                ca.renderMode = r.Pick(new[] { RenderMode.ScreenSpaceOverlay, RenderMode.ScreenSpaceCamera, RenderMode.WorldSpace });
                cb.overrideSorting = r.Bool(); cb.sortingOrder = r.Int(200) - 100; cc.sortingOrder = r.Int(32768);
                ca.referencePixelsPerUnit = 1f + r.Unit() * 200f; ca.pixelPerfect = r.Bool();
                o.Raw("root", (ca.isRootCanvas ? 1 : 0) + "" + (cb.isRootCanvas ? 1 : 0) + (cc.isRootCanvas ? 1 : 0) + "|" + cc.rootCanvas.name + "|" + cb.rootCanvas.name);
                o.Raw("sorting", ca.sortingOrder + "," + cb.sortingOrder + "," + cc.sortingOrder + "|" + (cb.overrideSorting ? 1 : 0) + (cc.overrideSorting ? 1 : 0));
                o.Raw("mode", ((int)ca.renderMode) + "," + ((int)cb.renderMode) + "," + ((int)cc.renderMode));
                o.Raw("rppu", Out.H(ca.referencePixelsPerUnit) + "," + Out.H(cb.referencePixelsPerUnit) + "," + (ca.pixelPerfect ? 1 : 0) + (cb.pixelPerfect ? 1 : 0));
                o.S("layerName", cb.sortingLayerName); o.I("layerId", cb.sortingLayerID);
                c.transform.SetParent(null, false); o.Raw("rootAfter", (cc.isRootCanvas ? 1 : 0) + "|" + cc.rootCanvas.name);
                o.I("targetDisplay", ca.targetDisplay); o.Raw("planeDist", Out.H(ca.planeDistance));
            }
            catch (Exception e) { o.Exc("canvas", e); }
            finally { DUtil.Cleanup(made); }
        }
    }

    /// <summary>CanvasGroup: alpha / interactable / blocksRaycasts / ignoreParentGroups round trips, and Selectable's group-allowed
    /// interactability through nested groups (UnityEngine.UI walks the groups with GetComponents).</summary>
    public static class D_CanvasGroup
    {
        public static void Register() { Suites.All["d-ui-canvasgroup"] = Case; }
        static void Case(Rng r, Out o)
        {
            var made = new List<GameObject>();
            try
            {
                var chain = new List<CanvasGroup>(); GameObject prev = null;
                for (int i = 0; i < 3; i++)
                {
                    var g = new GameObject("g" + i, typeof(RectTransform)); made.Add(g); if (prev != null) g.transform.SetParent(prev.transform, false); prev = g;
                    var cg = g.AddComponent<CanvasGroup>(); cg.alpha = r.Chance(20) ? r.Pick(new[] { -0.5f, 1.5f, 0f }) : r.Unit(); cg.interactable = r.Chance(70); cg.blocksRaycasts = r.Bool(); cg.ignoreParentGroups = r.Chance(25);
                    chain.Add(cg);
                }
                foreach (var cg in chain) o.Raw("cg." + cg.name, Out.H(cg.alpha) + "|" + (cg.interactable ? 1 : 0) + (cg.blocksRaycasts ? 1 : 0) + (cg.ignoreParentGroups ? 1 : 0));
                var btnGo = new GameObject("btn", typeof(RectTransform)); made.Add(btnGo); btnGo.transform.SetParent(prev.transform, false);
                var btn = btnGo.AddComponent<UnityEngine.UI.Button>();
                o.B("IsInteractable", btn.IsInteractable());
                chain[r.Int(3)].interactable = !chain[0].interactable; o.B("IsInteractable2", btn.IsInteractable());
            }
            catch (Exception e) { o.Exc("canvasgroup", e); }
            finally { DUtil.Cleanup(made); }
        }
    }
}
