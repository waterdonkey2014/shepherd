namespace Shepherd.Engine.Physics;

/// <summary>Physics Stage 3 (physics-plan-v2 section 3 "Time fixes" and the step rule, D7): the two frame-clock decisions Runtime/Lifecycle.cs and
/// Icalls/AppIcalls.cs read, decided once. <see cref="FrameFixed"/>: inside the fixed step and its callbacks Time.time / unscaledTime /
/// unscaledDeltaTime are the fixed clock's, fixedUnscaledDeltaTime = fdt / timeScale, fixedUnscaledTime its own clock (the wall clock has had
/// this since kit #3). <see cref="FrameStepCap"/>: the frame clock's steps per frame (5 as before; Unity's rule = no cap, only
/// Time.maximumDeltaTime bounds a frame). Both are Stage 3 behaviour: on where Stage 3 runs (level 3 with a core), off elsewhere -- kit #4's
/// frame clock exactly -- unless SHEPHERD_FIXEDTIME=1|0 / SHEPHERD_PHYS_STEPCAP=unity|N force them (the A/Bs); <c>none</c>: off.</summary>
public static class PhysTime
{
    public static readonly bool FrameFixed = PhysConfig.On && PhysWorld.FixedTimeOn;
    public static readonly int FrameStepCap = PhysConfig.On ? PhysWorld.StepCap : 5;
}
