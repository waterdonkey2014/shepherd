namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #10, track cache -- INTEGRATION FILE (add as engine/Shepherd.Engine/Runtime/K10VerifyStop.cs once the io track's
/// verify core is merged: it implements io's Shepherd.Assets.IVerifyProgress, so it cannot build on k10-cache alone). The Verify Files row's
/// adapter (Runtime/K10Cache.cs Verify) finds this type by name and passes one to io's K10Io.VerifyFiles: io's core calls Step after every
/// chunk / block, and a false answer stops the check (io's Result.Stopped; the row shows "Stopped"). K10Cache sets Verify.StopWanted when the
/// window the check was started in closes -- a scene load starts, or the Settings menu is left -- so a check never keeps reading the card
/// into gameplay or a load (review fix 2026-10-28). Nothing else changes: without this file the row works as before (no stop).</summary>
public sealed class K10VerifyStop : Shepherd.Assets.IVerifyProgress
{
    public bool Step(long done, long total) => K10Cache.Verify.Step(done, total);
}
