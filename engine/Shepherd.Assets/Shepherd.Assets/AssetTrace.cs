namespace Shepherd.Assets;

/// <summary>Optional observers of the asset layer (V3 part 2, the Vita pack baker's boot recording: vita/notes/v3b-pack.md).
/// Every hook is null by default, so the cost is one static null check per access and nothing changes when unset.</summary>
public static class AssetTrace
{
    public enum Access : byte { Raw, Tree, Script }

    /// <summary>An object's payload was read: <see cref="Access.Tree"/> through <see cref="TypeTreeReader.Read(SerializedFile, ObjectInfo)"/>,
    /// <see cref="Access.Script"/> = the MonoBehaviour header read for m_Script, <see cref="Access.Raw"/> = anything else
    /// (TextAsset bytes, TagManager/ResourceManager, stripped MonoBehaviours read by reflection).</summary>
    public static Action<SerializedFile, ObjectInfo, Access> ObjectRead;
    /// <summary>A PathId lookup found an object (PPtr resolution), whether or not its payload is read afterwards.</summary>
    public static Action<SerializedFile, ObjectInfo> ObjectLookup;
    /// <summary>A PathId lookup in a loaded file found nothing (the PPtr target does not exist).</summary>
    public static Action<SerializedFile, long> ObjectAbsent;
    /// <summary>An external / named file could not be found anywhere.</summary>
    public static Action<string> FileAbsent;
    /// <summary>Streamed data (.resS / .resource) was read: (file, stream path, offset, size).</summary>
    public static Action<SerializedFile, string, long, long> StreamRead;
    /// <summary>A bundle's serialized files were loaded: (bundle path, its files).</summary>
    public static Action<string, IReadOnlyList<SerializedFile>> BundleLoaded;

    [ThreadStatic] internal static Access Current;
}
