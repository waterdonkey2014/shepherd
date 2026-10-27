using System.Reflection;
using System.Runtime.CompilerServices;
using Shepherd.Engine.Runtime;
using UnityEngine.Networking;
namespace Shepherd.Engine.Impl;

/// <summary>Network fail-fast (V4; every profile since polish round 1): UnityWebRequest is a complete, inert request object whose
/// send completes on the spot with Result.ConnectionError / CannotConnectToHost — no socket, no DNS: what an offline Unity player
/// sees. Until polish round 1 these hooks were Profile = "vita" only and the Mac default left them missing, so the request
/// object was unusable (isModifiable false) and MenuAdController's coroutine died on an InvalidOperationException from
/// UnityWebRequest.Get; the engine never contacts the network in either case. CotL's users at boot: MenuAdController (ads
/// JSON + textures); later I2 Google translation, YouTube/Twitch widgets. Still [ProfileOnly]: it reads no property-bag keys
/// (the diet scan's criterion), so the Mac default's readable-key set and assembly-load order are unchanged.</summary>
[ProfileOnly]
static class NetIcalls
{
    const string P = null;   // every profile (was "vita")
    sealed class Req { public string Url = "", CustomMethod; public object Method; public bool Sent; public int Timeout, Redirects = 32; public bool Chunked, Continue = true, Suppress; public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase); }
    static readonly Dictionary<IntPtr, Req> Reqs = new();
    static long _next = 0x5EB0000;
    public static int Created, Sent;
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_Ptr")] static extern ref IntPtr Ptr(UnityWebRequest r);
    static Req R(object self) => self is UnityWebRequest w && Reqs.TryGetValue(Ptr(w), out var r) ? r : null;
    static IntPtr NewHandle() => (IntPtr)Interlocked.Increment(ref _next);
    // UnityWebRequest's error/method enums are internal: box them through reflection (cached).
    static readonly Type ErrT = typeof(UnityWebRequest).GetNestedType("UnityWebRequestError", BindingFlags.NonPublic);
    static readonly Type MethodT = typeof(UnityWebRequest).GetNestedType("UnityWebRequestMethod", BindingFlags.NonPublic);
    const int ErrOK = 0, ErrCannotConnect = 8, ErrAlreadySent = 35;
    static object Err(int v) => Enum.ToObject(ErrT, v);
    static object Modify(object self, Action<Req> f) { var r = R(self); if (r == null) return null; if (r.Sent) return Err(ErrAlreadySent); f(r); return Err(ErrOK); }

    public static string Summary() => $"[net] UnityWebRequest fail-fast: {Created} created, {Sent} sent -> ConnectionError (no socket opened)";

    [Icall("UnityEngine.Networking.UnityWebRequest::Create", Profile = P)] static object Create(object[] a) { var h = NewHandle(); lock (Reqs) Reqs[h] = new Req { Method = Enum.ToObject(MethodT, 0) }; Created++; return h; }
    [Icall("UnityEngine.Networking.UnityWebRequest::Release", Profile = P)] static object Release(object[] a) { if (a[0] is UnityWebRequest w) lock (Reqs) Reqs.Remove(Ptr(w)); return null; }
    [Icall("UnityEngine.Networking.UnityWebRequest::Abort", Profile = P)] static object Abort(object[] a) => null;
    [Icall("UnityEngine.Networking.UnityWebRequest::get_isModifiable", Profile = P)] static object Modifiable(object[] a) => R(a[0]) is { Sent: false };
    [Icall("UnityEngine.Networking.UnityWebRequest::IsExecuting", Profile = P)] static object Executing(object[] a) => false;
    [Icall("UnityEngine.Networking.UnityWebRequest::SetUrl", Profile = P)] static object SetUrl(object[] a) => Modify(a[0], r => r.Url = (string)a[1] ?? "");
    [Icall("UnityEngine.Networking.UnityWebRequest::GetUrl", Profile = P)] static object GetUrl(object[] a) => R(a[0])?.Url ?? "";
    [Icall("UnityEngine.Networking.UnityWebRequest::SetMethod", Profile = P)] static object SetMethod(object[] a) => Modify(a[0], r => r.Method = a[1]);
    [Icall("UnityEngine.Networking.UnityWebRequest::GetMethod", Profile = P)] static object GetMethod(object[] a) => R(a[0])?.Method ?? Enum.ToObject(MethodT, 0);
    [Icall("UnityEngine.Networking.UnityWebRequest::SetCustomMethod", Profile = P)] static object SetCustom(object[] a) => Modify(a[0], r => { r.CustomMethod = (string)a[1]; r.Method = Enum.ToObject(MethodT, 4); });
    [Icall("UnityEngine.Networking.UnityWebRequest::GetCustomMethod", Profile = P)] static object GetCustom(object[] a) => R(a[0])?.CustomMethod ?? "";
    [Icall("UnityEngine.Networking.UnityWebRequest::InternalSetRequestHeader", Profile = P)] static object SetHeader(object[] a) => Modify(a[0], r => r.Headers[(string)a[1] ?? ""] = (string)a[2]);
    [Icall("UnityEngine.Networking.UnityWebRequest::GetRequestHeader", Profile = P)] static object GetHeader(object[] a) => R(a[0])?.Headers.GetValueOrDefault((string)a[1] ?? "");
    [Icall("UnityEngine.Networking.UnityWebRequest::SetUploadHandler", Profile = P)] [Icall("UnityEngine.Networking.UnityWebRequest::SetDownloadHandler", Profile = P)]
    [Icall("UnityEngine.Networking.UnityWebRequest::SetCertificateHandler", Profile = P)] static object SetHandler(object[] a) => Modify(a[0], _ => { });
    [Icall("UnityEngine.Networking.UnityWebRequest::SetTimeoutMsec", Profile = P)] static object SetTimeout(object[] a) => Modify(a[0], r => r.Timeout = (int)a[1]);
    [Icall("UnityEngine.Networking.UnityWebRequest::GetTimeoutMsec", Profile = P)] static object GetTimeout(object[] a) => R(a[0])?.Timeout ?? 0;
    [Icall("UnityEngine.Networking.UnityWebRequest::SetRedirectLimitFromScripting", Profile = P)] static object SetRedirects(object[] a) { Modify(a[0], r => r.Redirects = (int)a[1]); return null; }
    [Icall("UnityEngine.Networking.UnityWebRequest::GetRedirectLimit", Profile = P)] static object GetRedirects(object[] a) => R(a[0])?.Redirects ?? 32;
    [Icall("UnityEngine.Networking.UnityWebRequest::SetChunked", Profile = P)] static object SetChunked(object[] a) => Modify(a[0], r => r.Chunked = (bool)a[1]);
    [Icall("UnityEngine.Networking.UnityWebRequest::GetChunked", Profile = P)] static object GetChunked(object[] a) => R(a[0])?.Chunked ?? false;
    [Icall("UnityEngine.Networking.UnityWebRequest::set_use100Continue", Profile = P)] static object SetContinue(object[] a) { var r = R(a[0]); if (r != null) r.Continue = (bool)a[1]; return null; }
    [Icall("UnityEngine.Networking.UnityWebRequest::get_use100Continue", Profile = P)] static object GetContinue(object[] a) => R(a[0])?.Continue ?? true;
    [Icall("UnityEngine.Networking.UnityWebRequest::SetSuppressErrorsToConsole", Profile = P)] static object SetSuppress(object[] a) => Modify(a[0], r => r.Suppress = (bool)a[1]);
    [Icall("UnityEngine.Networking.UnityWebRequest::GetSuppressErrorsToConsole", Profile = P)] static object GetSuppress(object[] a) => R(a[0])?.Suppress ?? false;
    /// <summary>SendWebRequest: done on the spot (completed handlers added later fire at once, coroutines resume next frame).</summary>
    [Icall("UnityEngine.Networking.UnityWebRequest::BeginWebRequest", Profile = P)] static object Begin(object[] a)
    {
        var r = R(a[0]); if (r == null) return null;
        if (r.Sent) throw new InvalidOperationException("UnityWebRequest has already been sent; each request can only be used once");
        r.Sent = true; Sent++;
        var op = AsyncOps.Create<UnityWebRequestAsyncOperation>(poll: () => true);
        var o = AsyncOps.Of(op); o.Done = true; o.Progress = 1f;
        Log.Info($"[net] UnityWebRequest {(r.CustomMethod ?? r.Method?.ToString())?.ToUpperInvariant()} {r.Url} -> ConnectionError (network fail-fast)");
        return op;
    }
    [Icall("UnityEngine.Networking.UnityWebRequest::get_result", Profile = P)] static object Result(object[] a) => R(a[0]) is { Sent: true } ? UnityWebRequest.Result.ConnectionError : UnityWebRequest.Result.InProgress;
    [Icall("UnityEngine.Networking.UnityWebRequest::GetError", Profile = P)] static object GetError(object[] a) => Err(R(a[0]) is { Sent: true } ? ErrCannotConnect : ErrOK);
    [Icall("UnityEngine.Networking.UnityWebRequest::GetWebErrorString", Profile = P)] static object ErrString(object[] a) => Convert.ToInt32(a[0]) switch
    {
        ErrOK => "", ErrCannotConnect => "Cannot connect to destination host", ErrAlreadySent => "Request already sent", _ => "Unknown Error"
    };
    [Icall("UnityEngine.Networking.UnityWebRequest::GetHTTPStatusString", Profile = P)] static object HttpStatus(object[] a) => "";
    [Icall("UnityEngine.Networking.UnityWebRequest::get_responseCode", Profile = P)] static object ResponseCode(object[] a) => 0L;
    [Icall("UnityEngine.Networking.UnityWebRequest::GetResponseHeader", Profile = P)] static object RespHeader(object[] a) => null;
    [Icall("UnityEngine.Networking.UnityWebRequest::GetResponseHeaderKeys", Profile = P)] static object RespKeys(object[] a) => Array.Empty<string>();
    [Icall("UnityEngine.Networking.UnityWebRequest::get_downloadedBytes", Profile = P)] [Icall("UnityEngine.Networking.UnityWebRequest::get_uploadedBytes", Profile = P)] static object Bytes(object[] a) => 0UL;
    [Icall("UnityEngine.Networking.UnityWebRequest::GetDownloadProgress", Profile = P)] [Icall("UnityEngine.Networking.UnityWebRequest::GetUploadProgress", Profile = P)] static object Progress(object[] a) => 1f;
    [Icall("UnityEngine.Networking.UnityWebRequest::ClearCookieCache", Profile = P)] static object Cookies(object[] a) => null;

    // handlers: inert native halves (any non-zero handle); no data ever arrives
    [Icall("UnityEngine.Networking.DownloadHandlerBuffer::Create", Profile = P)] [Icall("UnityEngine.Networking.DownloadHandlerTexture::Create", Profile = P)]
    [Icall("UnityEngine.Networking.DownloadHandlerFile::Create", Profile = P)] [Icall("UnityEngine.Networking.DownloadHandlerScript::Create", Profile = P)]
    [Icall("UnityEngine.Networking.DownloadHandlerScript::CreatePreallocated", Profile = P)] [Icall("UnityEngine.Networking.DownloadHandlerAssetBundle::Create", Profile = P)]
    [Icall("UnityEngine.Networking.DownloadHandlerAudioClip::Create", Profile = P)] [Icall("UnityEngine.Networking.UploadHandlerRaw::Create", Profile = P)]
    [Icall("UnityEngine.Networking.UploadHandlerFile::Create", Profile = P)] [Icall("UnityEngine.Networking.CertificateHandler::Create", Profile = P)]
    static object HandlerCreate(object[] a) => NewHandle();
    [Icall("UnityEngine.Networking.DownloadHandler::Release", Profile = P)] [Icall("UnityEngine.Networking.UploadHandler::Release", Profile = P)]
    [Icall("UnityEngine.Networking.CertificateHandler::Release", Profile = P)] static object HandlerRelease(object[] a) => null;
    [Icall("UnityEngine.Networking.DownloadHandler::IsDone", Profile = P)] static object DhDone(object[] a) => true;
    [Icall("UnityEngine.Networking.DownloadHandler::GetErrorMsg", Profile = P)] static object DhError(object[] a) => null;
    [Icall("UnityEngine.Networking.DownloadHandler::GetContentType", Profile = P)] static object DhType(object[] a) => "";
    [Icall("UnityEngine.Networking.DownloadHandler::InternalGetByteArray", Profile = P)] static object DhBytes(object[] a) { a[1] = 0; return IntPtr.Zero; }
    [Icall("UnityEngine.Networking.DownloadHandlerTexture::InternalGetTextureNative", Profile = P)] static object DhTexture(object[] a) => null;
    [Icall("UnityEngine.Networking.DownloadHandlerAssetBundle::get_assetBundle", Profile = P)] static object DhBundle(object[] a) => null;
    [Icall("UnityEngine.Networking.DownloadHandlerAssetBundle::get_isDownloadComplete", Profile = P)] static object DhBundleDone(object[] a) => true;
    [Icall("UnityEngine.Networking.UploadHandler::InternalGetProgress", Profile = P)] static object UhProgress(object[] a) => 1f;
    [Icall("UnityEngine.Networking.UploadHandler::InternalGetContentType", Profile = P)] static object UhType(object[] a) => "";
    [Icall("UnityEngine.Networking.UploadHandler::InternalSetContentType", Profile = P)] static object UhSetType(object[] a) => null;
}
