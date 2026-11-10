#if !SHEPHERD_EBOOT
using System.Reflection;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Diag;

/// <summary>Kit #12 boot track (Mac / tools builds only; never in the eboots): SHEPHERD_K12_REC_EMBLEM=&lt;sprite name&gt; -- for the boot screen's
/// recordings (vita/tools/bootscreen_record.sh), every slot of the game's own emblem list (MMTools.MMTransition.pentagramImages) holds the named
/// sprite, taken from the game's own lists (pentagramImages, pentagramImagesSOTF, pentagramImagesDLC1), so the game's loading screen draws that
/// emblem through its own Random.Range pick (the same random number is drawn: the generator's state is unchanged) and its own Image, material and
/// fade. Kit #11 recorded by trying seeds until each emblem came up; the Sins of the Flesh Snake pentagram joins the game's list only with a save
/// that has built the pleasure shrine, so it could not be caught that way. A recording aid: nothing in the game's logic or the frames otherwise
/// changes, and a run without the variable is untouched.</summary>
[ProfileOnly]
public static class K12BootRec
{
    static readonly string Want = Environment.GetEnvironmentVariable("SHEPHERD_K12_REC_EMBLEM") is { Length: > 0 } w ? w.Trim() : null;
    static readonly List<object> _done = new();   // (the MMTransition instances already set: by reference)
    static readonly BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>SHEPHERD_K12_REC_TEXT="&lt;frame&gt;=&lt;text&gt;|..." (the recommended plugins' name pictures, vita/tools/plugins_text.sh): at each frame the game's
    /// loading text (MMTransition.LoadingText: its own font, size and red) shows that text -- the pictures of the names are what the game's own
    /// text draws; "" clears it.</summary>
    static readonly (int frame, string text)[] Texts = (Environment.GetEnvironmentVariable("SHEPHERD_K12_REC_TEXT") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => { int e = x.IndexOf('='); return e > 0 && int.TryParse(x[..e], out var f) ? (f, x[(e + 1)..]) : (-1, null); }).Where(x => x.Item1 > 0).ToArray();
    static void RecText(int frame)
    {
        foreach (var (f, text) in Texts)
        {
            if (f != frame) continue;
            try
            {
                var t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MMTools.MMTransition")).FirstOrDefault(x => x != null);
                var tr = t?.GetField("mmTransition", I | BindingFlags.Static)?.GetValue(null);
                var lt = tr?.GetType().GetField("LoadingText", I)?.GetValue(tr);
                lt?.GetType().GetProperty("text")?.SetValue(lt, text);
                Log.Info($"[k12 rec] frame {frame}: the loading text = \"{text}\"{(lt == null ? " (NOT SET: no live transition)" : "")}");
            }
            catch (Exception e) { Log.Exception(e, "k12 rec text"); }
        }
    }

    public static void Before(int frame)
    {
        if (Texts.Length > 0) RecText(frame);
        if (Want == null) return;
        try
        {
            foreach (var m in Objects.All.OfType<NMono>())
            {
                if (m.Destroyed || m.Managed == null || m.Managed.GetType().FullName != "MMTools.MMTransition" || _done.Any(d => ReferenceEquals(d, m.Managed))) continue;
                var t = m.Managed.GetType();
                var lists = new[] { "pentagramImages", "pentagramImagesSOTF", "pentagramImagesDLC1" }.Select(n => t.GetField(n, I)?.GetValue(m.Managed) as List<UnityEngine.Sprite>).ToArray();
                if (lists[0] == null) continue;
                var sp = lists.Where(l => l != null).SelectMany(l => l).FirstOrDefault(s => s != null && s.name == Want);
                if (sp == null)
                {
                    Log.Warn($"[k12 rec] SHEPHERD_K12_REC_EMBLEM={Want}: no such sprite in the game's emblem lists (" + string.Join(", ", lists.Where(l => l != null).SelectMany(l => l).Where(s => s != null).Select(s => s.name).Distinct()) + ")");
                    _done.Add(m.Managed); continue;
                }
                var names = string.Join(", ", lists[0].Select(s => s?.name));
                for (int i = 0; i < lists[0].Count; i++) lists[0][i] = sp;
                _done.Add(m.Managed);
                Log.Info($"[k12 rec] frame {frame}: the game's emblem list ({lists[0].Count} slots: {names}) now holds '{Want}' in every slot (the game's own pick draws it)");
            }
        }
        catch (Exception e) { Log.Exception(e, "k12 rec"); }
    }
}
#endif
