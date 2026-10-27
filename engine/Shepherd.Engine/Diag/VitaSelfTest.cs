using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
namespace Shepherd.Engine.Diag;

/// <summary>`SHEPHERD_PROFILE=vita ... boot vitatest`: checks the Vita profile's stubs without booting the game.
/// FMOD semantics (Shepherd.Audio.FmodSelfTest) + network fail-fast: UnityWebRequest -> ConnectionError, BCL sockets/DNS/HTTP ->
/// immediate SocketException. Only loopback addresses are used, so nothing could leave the machine even if a block failed.
/// Exit code 0 = all PASS.</summary>
[ProfileOnly]
static class VitaSelfTest
{
    /// <summary>Boot mode name (the literal lives here, in a [ProfileOnly] type, so the Mac default's diet key scan is unchanged).</summary>
    public static bool Handles(string mode) => mode == "vitatest";
    public static int Run()
    {
        if (!Profile.Vita) { Log.Error("vitatest needs SHEPHERD_PROFILE=vita"); return 2; }
        int pass = 0, fail = 0, gaps = 0; bool netGap = false;
        // Mono (the Vita runtime) answers libSystem.Native from its ALC cache before any DllImportResolver, so the Mac-side BCL
        // network block (Diag/NativeCensus) can't act there: those checks are reported, not failed (the Vita's block is the
        // System.Native shim). Only loopback endpoints are used, so nothing leaves the machine either way.
        void Check(bool ok, string what)
        {
            if (ok) pass++; else if (netGap) gaps++; else fail++;
            Log.Info($"[vitatest] {(ok ? "PASS" : netGap ? "GAP " : "FAIL")} {what}");
        }
        void Guard(string what, Action a) { try { a(); } catch (Exception e) { Check(false, $"{what}: threw {e.GetType().Name}: {e.Message}"); } }

        Guard("FMOD", () => Shepherd.Audio.FmodSelfTest.Run(Check));

        Guard("UnityWebRequest", () =>
        {
            var w = UnityEngine.Networking.UnityWebRequest.Get("https://drive.google.com/uc?export=download");   // never sent anywhere
            var op = w.SendWebRequest();
            Check(op != null && op.isDone && w.isDone, "UnityWebRequest.SendWebRequest() is done on the spot");
            Check(w.result == UnityEngine.Networking.UnityWebRequest.Result.ConnectionError, $"result {w.result}, error '{w.error}'");
            Check(!w.isModifiable, "sent request is no longer modifiable");
            w.Dispose();
        });

        netGap = MonoCompat.MonoInterpCompat.IsMono;
        var sw = Stopwatch.StartNew();
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);   // local: allowed
            Check(Socket.OSSupportsIPv4, "new Socket() works (local) and the IPv4 probe type initializer is intact");
            sw.Restart(); s.Connect(new IPEndPoint(IPAddress.Loopback, 9)); Check(false, "Socket.Connect should fail under the profile");
        }
        catch (SocketException e) { Check(e.SocketErrorCode == SocketError.NetworkUnreachable, $"Socket.Connect(127.0.0.1:9) -> SocketException {e.SocketErrorCode} in {sw.ElapsedMilliseconds} ms"); }
        catch (Exception e) { Check(false, $"Socket.Connect -> {e.GetType().Name}: {e.Message}"); }
        sw.Restart();
        try { using var u = new UdpClient(); u.Send(new byte[1], 1, new IPEndPoint(IPAddress.Loopback, 9)); Check(false, "UdpClient.Send should fail under the profile"); }
        catch (SocketException e) { Check(e.SocketErrorCode == SocketError.NetworkUnreachable, $"UdpClient.Send(127.0.0.1:9) -> SocketException {e.SocketErrorCode} in {sw.ElapsedMilliseconds} ms"); }
        catch (Exception e) { Check(false, $"UdpClient.Send -> {e.GetType().Name}: {e.Message}"); }
        sw.Restart();
        try { using var t = new TcpClient(); t.Connect("localhost", 9); Check(false, "TcpClient.Connect should fail under the profile"); }
        catch (SocketException e) { Check(e.SocketErrorCode == SocketError.NetworkUnreachable, $"TcpClient.Connect(localhost:9) -> SocketException {e.SocketErrorCode} in {sw.ElapsedMilliseconds} ms"); }
        catch (Exception e) { Check(false, $"TcpClient.Connect -> {e.GetType().Name}: {e.Message}"); }
        sw.Restart();
        try { Dns.GetHostAddresses("localhost"); Check(false, "Dns.GetHostAddresses should fail under the profile"); }
        catch (SocketException e) { Check(true, $"Dns.GetHostAddresses -> SocketException {e.SocketErrorCode} in {sw.ElapsedMilliseconds} ms"); }
        catch (Exception e) { Check(false, $"Dns.GetHostAddresses -> {e.GetType().Name}: {e.Message}"); }
        sw.Restart();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.GetAsync("http://127.0.0.1:9/").GetAwaiter().GetResult(); Check(false, "HttpClient.GetAsync should fail under the profile");
        }
        catch (HttpRequestException e) { Check(sw.ElapsedMilliseconds < 1000 && e.InnerException is SocketException, $"HttpClient.GetAsync -> HttpRequestException ({e.InnerException?.GetType().Name}: {e.InnerException?.Message}) in {sw.ElapsedMilliseconds} ms"); }
        catch (Exception e) { Check(false, $"HttpClient.GetAsync -> {e.GetType().Name}: {e.Message}"); }

        Log.Info($"[vitatest] {pass} passed, {fail} failed{(gaps > 0 ? $", {gaps} known Mono gaps (BCL network block is CoreCLR-only)" : "")}");
        return fail == 0 ? 0 : 1;
    }
}
