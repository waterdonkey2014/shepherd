namespace Shepherd.Engine.Runtime;

/// <summary>Kit #14 newlib (vita/notes/kit14-newlib.md): the engine's rows of the track that takes memory off the C heap. Switches (env file):
/// SHEPHERD_K14_NEWLIB=0 (the whole track = kit #13; the host reads it too, for its runtime rows), SHEPHERD_K14_BLOCKS=0 (each record's native block
/// a malloc again; Native/Objects.cs).</summary>
[ProfileOnly]
public static class K14Newlib
{
    public static readonly bool Master = System.Environment.GetEnvironmentVariable("SHEPHERD_K14_NEWLIB") != "0";
    public static readonly bool Blocks = Master && System.Environment.GetEnvironmentVariable("SHEPHERD_K14_BLOCKS") != "0";
}
