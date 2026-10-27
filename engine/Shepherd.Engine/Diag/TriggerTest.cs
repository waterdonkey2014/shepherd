using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Diag;

/// <summary>fid1-fixup D3-D5: `boot trigtest` -- Triggers2D's Unity rules on synthetic colliders, against the owner's own Physics 2D
/// settings (Runtime/Physics2DSettings.cs reads them from globalgamemanagers; nothing here names the game's values: the layer pairs
/// below are checked against what the project's matrix says, read through Physics2D's own API). Each case sits 100 units from the
/// next, so only its own colliders meet. Checks: the layer matrix (an allowed and a blocked pair, IgnoreLayerCollision both ways), a
/// Static body pairing like no body (static-static never; static trigger vs a dynamic body yes), simulated = false taking a body's
/// colliders out (and back in), Physics2D.IgnoreCollision, a destroyed collider (the survivor hears Exit at the destroy with a
/// valid argument, the dying side nothing), a disabled collider (Exit both sides at the next step), callbacksOnDisable off (no
/// Exit). Collider2D's own setters are not served yet (Physics2DModule loads after the auto-property hooks are bound), so the test
/// seeds isTrigger in the property bag the way scene data does. Exit code 0 = every check passed. Needs only the game's Managed
/// folder and globalgamemanagers (no scene, no renderer).</summary>
[ProfileOnly]   // test text only; it seeds a bag value but reads none (Assets/Diet.cs)
public static class TriggerTest
{
    public static bool Handles(string mode) => mode == "trigtest";
    internal static readonly List<(string to, string kind, string other, bool fixedStep)> Ev = new();
    static int _pass, _fail;

    static void Check(string what, bool ok, string detail = null)
    {
        if (ok) _pass++; else _fail++;
        Log.Info($"[trigtest] {(ok ? "PASS" : "FAIL")} {what}{(ok || detail == null ? "" : " -- " + detail)}");
    }
    static void Frames(int n) { for (int i = 0; i < n; i++) Lifecycle.Frame(); }
    static bool Has(string to, string kind, string other) => Ev.Any(e => e.to == to && e.kind == kind && e.other == other);
    static int Count(string to, string kind) => Ev.Count(e => e.to == to && e.kind == kind);
    static bool Quiet(string prefix) => !Ev.Any(e => e.to.StartsWith(prefix) || e.other.StartsWith(prefix));
    static bool QuietKind(string prefix, string kind) => !Ev.Any(e => e.kind == kind && (e.to.StartsWith(prefix) || e.other.StartsWith(prefix)));
    static string Dump(string prefix) => string.Join("; ", Ev.Where(e => e.to.StartsWith(prefix) || e.other.StartsWith(prefix)).Select(e => $"{e.to} {e.kind} {e.other}").Distinct().Take(12));

    /// <summary>body: -1 none, else RigidbodyType2D (0 Dynamic, 1 Kinematic, 2 Static). Returns the Collider2D typed as Component: no
    /// static method of the engine may name a Physics2D type in its signature -- IcallRegistry's scan of every static method would load
    /// UnityEngine.Physics2DModule at start-up, ahead of the game (it moved from 48th to 26th and cost ~0.2 MiB live at level0).</summary>
    static Component Make(string name, int layer, float x, bool circle, bool trigger, int body)
    {
        var go = new GameObject(name); go.layer = layer; go.transform.position = new Vector3(x, 0f, 0f);
        Collider2D c = circle ? go.AddComponent<CircleCollider2D>() : go.AddComponent<BoxCollider2D>();
        Objects.Get(c).Set("isTrigger", trigger);   // as scene data seeds it
        if (body >= 0) go.AddComponent<Rigidbody2D>().bodyType = (RigidbodyType2D)body;
        go.AddComponent<TrigProbe>();
        return c;
    }

    public static int Run()
    {
        Runtime.Time.CaptureDeltaTime = 1f / 60f;
        Assets.Loader.Init();
        // the project's matrix through Unity's API (read from the owner's globalgamemanagers on first use)
        int player = LayerMask.NameToLayer("Player"), volume = LayerMask.NameToLayer("Only Collider With Player");
        bool okLayers = player >= 0 && volume >= 0;
        Check("layer names resolve (TagManager)", okLayers, $"Player={player} 'Only Collider With Player'={volume}");
        if (!okLayers) { player = 14; volume = 21; }
        int blockedWith = -1;   // a layer the project's matrix keeps away from the volume layer
        for (int l = 0; l < 32 && blockedWith < 0; l++) if (Physics2D.GetIgnoreLayerCollision(l, volume)) blockedWith = l;
        Check($"settings read from the project ({Physics2DSettings.Source})", Physics2DSettings.Source == "project");
        Check($"matrix: layer {player} collides with {volume}, layer {blockedWith} does not", !Physics2D.GetIgnoreLayerCollision(player, volume) && blockedWith >= 0);
        Check("matrix symmetric for those", Physics2D.GetIgnoreLayerCollision(volume, player) == Physics2D.GetIgnoreLayerCollision(player, volume) && Physics2D.GetIgnoreLayerCollision(volume, blockedWith));
        Check("GetLayerCollisionMask agrees with GetIgnoreLayerCollision", ((Physics2D.GetLayerCollisionMask(player) >> volume) & 1) == 1 && ((Physics2D.GetLayerCollisionMask(volume) >> blockedWith) & 1) == 0);

        var mPlayer = (Collider2D)Make("M-player", player, 0, true, false, 0); var mVol = (Collider2D)Make("M-volume", volume, 0, false, true, -1); Make("M-blocked", blockedWith, 0, true, false, 0);
        var sTrig = (Collider2D)Make("S-statictrig", 0, 100, false, true, 2); Make("S-plain", 0, 100, false, false, -1); Make("S-dyn", 0, 100, true, false, 0);
        var uDyn = (Collider2D)Make("U-dyn", 0, 200, true, false, 0); Make("U-trig", 0, 200, false, true, -1); uDyn.GetComponent<Rigidbody2D>().simulated = false;
        var iDyn = (Collider2D)Make("I-dyn", 0, 300, true, false, 0); var iTrig = (Collider2D)Make("I-trig", 0, 300, false, true, -1);
        var dDyn = (Collider2D)Make("D-dyn", 0, 400, true, false, 0); Make("D-trig", 0, 400, false, true, -1);
        Make("X-dyn", 0, 500, true, false, 0); var xTrig = (Collider2D)Make("X-trig", 0, 500, false, true, -1);
        Make("C-dyn", 0, 600, true, false, 0); var cTrig = (Collider2D)Make("C-trig", 0, 600, false, true, -1);
        Make("K-kin", 0, 700, true, false, 1); Make("K-statictrig", 0, 700, false, true, 2);

        Ev.Clear(); Frames(4);
        Check("matrix: allowed pair enters", Has("M-player", "enter", "M-volume") && Has("M-volume", "enter", "M-player"), Dump("M-"));
        Check("matrix: blocked pair is never considered", Quiet("M-blocked"), Dump("M-blocked"));
        Check("callbacks run inside the fixed step", Ev.Where(e => e.to.StartsWith("M-")).All(e => e.fixedStep));
        Check("static body + static collider: no pair", Quiet("S-plain"), Dump("S-plain"));
        Check("static-body trigger + dynamic body: enter", Has("S-statictrig", "enter", "S-dyn") && Has("S-dyn", "enter", "S-statictrig"), Dump("S-"));
        Check("kinematic body + static-body trigger: enter", Has("K-kin", "enter", "K-statictrig"), Dump("K-"));
        Check("simulated = false: the body's collider takes no part", Quiet("U-"), Dump("U-"));
        Check("Stay follows Enter", Count("I-trig", "stay") >= 1 && Count("I-trig", "enter") == 1, Dump("I-"));

        Ev.Clear(); uDyn.GetComponent<Rigidbody2D>().simulated = true; Frames(4);
        Check("simulated = true: enters", Has("U-dyn", "enter", "U-trig") && Has("U-trig", "enter", "U-dyn"), Dump("U-"));
        Ev.Clear(); uDyn.GetComponent<Rigidbody2D>().simulated = false; Frames(4);
        Check("simulated = false again: Exit both sides, then nothing", Has("U-dyn", "exit", "U-trig") && Has("U-trig", "exit", "U-dyn") && Count("U-trig", "stay") == 0 && Count("U-trig", "exit") == 1, Dump("U-"));

        Ev.Clear(); Physics2D.IgnoreCollision(iDyn, iTrig); Frames(4);
        Check("IgnoreCollision: Exit both sides, then nothing", Has("I-dyn", "exit", "I-trig") && Has("I-trig", "exit", "I-dyn") && Count("I-trig", "stay") == 0, Dump("I-"));
        Check("GetIgnoreCollision", Physics2D.GetIgnoreCollision(iDyn, iTrig) && Physics2D.GetIgnoreCollision(iTrig, iDyn));
        Ev.Clear(); Physics2D.IgnoreCollision(iDyn, iTrig, false); Frames(4);
        Check("IgnoreCollision(false): enters again", Has("I-trig", "enter", "I-dyn") && !Physics2D.GetIgnoreCollision(iDyn, iTrig), Dump("I-"));

        Ev.Clear(); UObject.Destroy(dDyn.gameObject); Frames(1);
        var dEv = Ev.Where(e => e.to.StartsWith("D-")).ToList();
        Check("destroy: the survivor hears Exit once, with the dying collider still valid", dEv.Count(e => e.to == "D-trig" && e.kind == "exit" && e.other == "D-dyn") == 1, Dump("D-"));
        Check("destroy: the dying side hears nothing", !dEv.Any(e => e.to == "D-dyn" && e.kind == "exit"), Dump("D-"));
        Check("destroy: Exit is sent at the destroy (outside the fixed step)", dEv.Where(e => e.kind == "exit").All(e => !e.fixedStep));
        Ev.Clear(); Frames(4);
        Check("destroy: nothing after", Quiet("D-"), Dump("D-"));

        Ev.Clear(); xTrig.enabled = false; Frames(4);
        Check("disable: Exit both sides at the next step", Has("X-dyn", "exit", "X-trig") && Has("X-trig", "exit", "X-dyn") && QuietKind("X-", "stay"), Dump("X-"));
        Ev.Clear(); xTrig.enabled = true; Frames(4);
        Check("re-enable: enters again", Has("X-trig", "enter", "X-dyn"), Dump("X-"));

        Ev.Clear(); Physics2D.callbacksOnDisable = false; cTrig.enabled = false; Frames(4);
        Check("callbacksOnDisable off: a disabled collider's pair ends without Exit", QuietKind("C-", "exit") && QuietKind("C-", "stay"), Dump("C-"));
        Physics2D.callbacksOnDisable = true; cTrig.enabled = true;

        Ev.Clear(); Physics2D.IgnoreLayerCollision(player, volume, true); Frames(4);
        Check("IgnoreLayerCollision: the touching pair exits, symmetric", Has("M-player", "exit", "M-volume") && Physics2D.GetIgnoreLayerCollision(volume, player), Dump("M-"));
        Ev.Clear(); Physics2D.IgnoreLayerCollision(player, volume, false); Frames(4);
        Check("IgnoreLayerCollision(false): enters again", Has("M-volume", "enter", "M-player"), Dump("M-"));

        Log.Info($"[trigtest] {_pass} passed, {_fail} failed (Triggers2D steps {Triggers2D.Steps}, pair-steps {Triggers2D.PairSteps}, layer-matrix skips {Triggers2D.MatrixSkips})");
        return _fail == 0 ? 0 : 1;
    }
}

/// <summary>TriggerTest's recorder: every trigger message this GameObject hears.</summary>
[ProfileOnly]
public sealed class TrigProbe : MonoBehaviour
{
    void Rec(string kind, Collider2D other) => TriggerTest.Ev.Add((name, kind, other != null ? other.gameObject.name : "null", Runtime.Time.InFixedStep));
    void OnTriggerEnter2D(Collider2D other) => Rec("enter", other);
    void OnTriggerStay2D(Collider2D other) => Rec("stay", other);
    void OnTriggerExit2D(Collider2D other) => Rec("exit", other);
}
