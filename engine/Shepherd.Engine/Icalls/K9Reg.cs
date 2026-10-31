using Shepherd.Hooks;
namespace Shepherd.Engine.Impl;

/// <summary>Kit #9, track runtime (lever 10 typed icall stubs; vita/notes/kit9-runtime.md): the list the Icalls classes fill with their typed
/// twins -- for each hot hook, the same expression without the object[] / boxes, as one of Shepherd.Hooks.TypedIcalls' delegate shapes -- and
/// the object[] hook each twin copies (Runtime/K9Typed.cs binds a twin only while that very hook is the one bound for its key).</summary>
sealed class K9Reg
{
    public readonly List<(string key, Delegate typed, IcallImpl slow)> List = new();
    public void Add(string key, Delegate typed, IcallImpl slow) => List.Add((key, typed, slow));
}
