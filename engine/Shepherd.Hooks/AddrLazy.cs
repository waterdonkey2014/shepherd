// Device kit #14, track small (vita/notes/kit14-small.md): the seam for the game catalog kept out of memory until asked for.
// tools/Rewriter --addr-lazy rewrites the owner's copy of Unity.Addressables.dll (owner-local; the game's own DLL is never touched):
//   - ContentCatalogData.CreateLocator first hands over the game's own pieces (Bind) and asks Create; a state object = the engine keeps the
//     catalog in a compact form and hands the game an empty ResourceLocationMap tied to it (Attached); null = the catalog is read exactly as before;
//   - ResourceLocationMap.Locate first calls Ensure(key): the key's entry, if the catalog holds it, is put into the map's dictionary (made
//     exactly as the original CreateLocator would have made it); the original lookup then runs unchanged;
//   - ResourceLocationMap.Keys / Locations / Add first call Full: every entry is put into the dictionary in the catalog's order (the
//     original's), after which the map is an ordinary one.
// With no engine handler (the Mac default outside the Vita profile, SHEPHERD_K14_CATALOG=0) Create answers null: every catalog is read at load exactly
// as before. A kit #13 Unity.Addressables.dll never calls this class, which is that same off state on the interpreted-game apps; the game-AOT apps are
// bound to the rewritten DLL's MVID, so there any other DLL stops at load ("doesn't match assembly"; SHEPHERD_GAME_AOT=0 or a rebuild is the way past it).
// The rewritten DLL needs this class in the eboot's Shepherd.Hooks: under one built without it (SHEPHERD_K14_SMALL_CODE=0, kit #13) CreateLocator fails on the type.
using System;
namespace Shepherd.Hooks
{
    // The game's own pieces, as the rewritten DLL hands them over (the engine holds no reference to Unity.Addressables and uses no reflection)
    public delegate object AddrRead(byte[] data, int offset);                              // SerializationUtilities.ReadObjectFromByteArray
    public delegate string AddrExpand(string[] prefixes, string id);                       // ContentCatalogData.ExpandInternalId
    public delegate string AddrResolve(string id);                                         // Addressables.ResolveInternalId
    public delegate Type AddrTypeValue(object serializedTypes, int index);                 // m_resourceTypes[index].Value, on the array's own element
    public delegate object[] AddrNewArray(int length);                                     // new IResourceLocation[length]
    public delegate object AddrNewLocation(object map, string internalId, string providerId, object dependencyKey, object data, int depHash, string primaryKey, Type type);   // new CompactLocation (...)
    public delegate object AddrCreate(object catalog, string bucketData, string keyData, string entryData, string extraData, string[] internalIds, string[] prefixes, string[] providerIds, object resourceTypes, int resourceTypeCount, string locatorId, string providerSuffix);

    public static class AddrLazy
    {
        /// <summary>The engine's handlers (Shepherd.Engine Runtime/K14Catalog.cs); null = no lazy catalog.</summary>
        public static AddrCreate OnCreate;
        public static Action<object, object, object> OnAttached;
        public static Action<object, object> OnEnsure;
        public static Action<object> OnFull;

        public static AddrRead Read; public static AddrExpand Expand; public static AddrResolve Resolve; public static AddrTypeValue TypeValue; public static AddrNewArray NewArray; public static AddrNewLocation NewLocation;
        /// <summary>CreateLocator's first step: the game's pieces.</summary>
        public static void Bind(AddrRead read, AddrExpand expand, AddrResolve resolve, AddrTypeValue typeValue, AddrNewArray newArray, AddrNewLocation newLocation)
        {
            Read = read; Expand = expand; Resolve = resolve; TypeValue = typeValue; NewArray = newArray; NewLocation = newLocation;
        }
        /// <summary>CreateLocator's next step: a state (the catalog is kept compact) or null (read it now, as before).</summary>
        public static object Create(object catalog, string bucketData, string keyData, string entryData, string extraData, string[] internalIds, string[] prefixes, string[] providerIds, object resourceTypes, int resourceTypeCount, string locatorId, string providerSuffix)
        {
            var f = OnCreate;
            return f == null ? null : f(catalog, bucketData, keyData, entryData, extraData, internalIds, prefixes, providerIds, resourceTypes, resourceTypeCount, locatorId, providerSuffix);
        }
        /// <summary>The map made for a state, and its dictionary (the original's private field).</summary>
        public static void Attached(object state, object map, object dictionary) { OnAttached?.Invoke(state, map, dictionary); }
        /// <summary>Locate's first step: the key's entry put into the dictionary when the catalog holds it.</summary>
        public static void Ensure(object state, object key) { OnEnsure?.Invoke(state, key); }
        /// <summary>Keys / Locations / Add's first step: every entry put into the dictionary in the catalog's order.</summary>
        public static void Full(object state) { OnFull?.Invoke(state); }
    }
}
