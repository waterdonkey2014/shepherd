namespace Shepherd.Assets;

/// <summary>Unity built-in class IDs (the subset that matters here) and their names.</summary>
public static class ClassIds
{
    public const int GameObject = 1, Transform = 4, Camera = 20, Material = 21, MeshRenderer = 23, Texture2D = 28,
        OcclusionCullingSettings = 29, MeshFilter = 33, Mesh = 43, Shader = 48, TextAsset = 49, Rigidbody2D = 50,
        Collider2D = 53, AudioClip = 83, AudioSource = 82, AudioListener = 81, Animator = 95, RenderTexture = 84,
        AnimationClip = 74, MonoBehaviour = 114, MonoScript = 115, RenderSettings = 104, Light = 108, Animation = 111,
        LightmapSettings = 157, ParticleSystem = 198, ParticleSystemRenderer = 199, SpriteRenderer = 212, Sprite = 213,
        CanvasRenderer = 222, Canvas = 223, RectTransform = 224, CanvasGroup = 225, AssetBundle = 142, NavMeshSettings = 196,
        PrefabInstance = 1001, SpriteAtlas = 687078895, Cubemap = 89, Font = 128, SortingGroup = 210,
        BoxCollider2D = 61, CircleCollider2D = 58, PolygonCollider2D = 60, EdgeCollider2D = 68, CapsuleCollider2D = 70,
        PlayableDirector = 320, SpriteMask = 331, SceneRoots = 1660057539;

    private static readonly Dictionary<int, string> s_names = new()
    {
        [1] = "GameObject", [2] = "Component", [3] = "LevelGameManager", [4] = "Transform", [5] = "TimeManager",
        [6] = "GlobalGameManager", [8] = "Behaviour", [9] = "GameManager", [11] = "AudioManager", [13] = "InputManager",
        [18] = "EditorExtension", [19] = "Physics2DSettings", [20] = "Camera", [21] = "Material", [23] = "MeshRenderer",
        [25] = "Renderer", [27] = "Texture", [28] = "Texture2D", [29] = "OcclusionCullingSettings", [30] = "GraphicsSettings",
        [33] = "MeshFilter", [41] = "OcclusionPortal", [43] = "Mesh", [45] = "Skybox", [47] = "QualitySettings",
        [48] = "Shader", [49] = "TextAsset", [50] = "Rigidbody2D", [53] = "Collider2D", [54] = "Rigidbody", [55] = "PhysicsManager",
        [56] = "Collider", [57] = "Joint", [58] = "CircleCollider2D", [59] = "HingeJoint", [60] = "PolygonCollider2D",
        [61] = "BoxCollider2D", [62] = "PhysicsMaterial2D", [64] = "MeshCollider", [65] = "BoxCollider", [66] = "CompositeCollider2D",
        [68] = "EdgeCollider2D", [70] = "CapsuleCollider2D", [72] = "ComputeShader", [74] = "AnimationClip", [78] = "TagManager",
        [81] = "AudioListener", [82] = "AudioSource", [83] = "AudioClip", [84] = "RenderTexture", [86] = "CustomRenderTexture",
        [89] = "Cubemap", [90] = "Avatar", [91] = "AnimatorController", [93] = "RuntimeAnimatorController", [94] = "ScriptMapper",
        [95] = "Animator", [96] = "TrailRenderer", [98] = "DelayedCallManager", [102] = "TextMesh", [104] = "RenderSettings",
        [108] = "Light", [109] = "CGProgram", [110] = "BaseAnimationTrack", [111] = "Animation", [114] = "MonoBehaviour",
        [115] = "MonoScript", [116] = "MonoManager", [117] = "Texture3D", [118] = "NewAnimationTrack", [119] = "Projector",
        [120] = "LineRenderer", [121] = "Flare", [122] = "Halo", [123] = "LensFlare", [124] = "FlareLayer", [125] = "HaloLayer",
        [126] = "NavMeshProjectSettings", [128] = "Font", [129] = "PlayerSettings", [130] = "NamedObject", [134] = "PhysicMaterial",
        [135] = "SphereCollider", [136] = "CapsuleCollider", [137] = "SkinnedMeshRenderer", [138] = "FixedJoint",
        [141] = "BuildSettings", [142] = "AssetBundle", [143] = "CharacterController", [144] = "CharacterJoint", [145] = "SpringJoint",
        [146] = "WheelCollider", [147] = "ResourceManager", [150] = "PreloadData", [153] = "ConfigurableJoint",
        [154] = "TerrainCollider", [156] = "TerrainData", [157] = "LightmapSettings", [158] = "WebCamTexture",
        [159] = "EditorSettings", [162] = "EditorUserSettings", [164] = "AudioReverbFilter", [165] = "AudioHighPassFilter",
        [166] = "AudioChorusFilter", [167] = "AudioReverbZone", [168] = "AudioEchoFilter", [169] = "AudioLowPassFilter",
        [170] = "AudioDistortionFilter", [171] = "SparseTexture", [180] = "AudioBehaviour", [181] = "AudioFilter",
        [182] = "WindZone", [183] = "Cloth", [184] = "SubstanceArchive", [185] = "ProceduralMaterial", [186] = "ProceduralTexture",
        [187] = "Texture2DArray", [188] = "CubemapArray", [191] = "OffMeshLink", [192] = "OcclusionArea", [193] = "Tree",
        [195] = "NavMeshAgent", [196] = "NavMeshSettings", [198] = "ParticleSystem", [199] = "ParticleSystemRenderer",
        [200] = "ShaderVariantCollection", [205] = "LODGroup", [206] = "BlendTree", [207] = "Motion", [208] = "NavMeshObstacle",
        [210] = "SortingGroup", [212] = "SpriteRenderer", [213] = "Sprite", [214] = "CachedSpriteAtlas", [215] = "ReflectionProbe",
        [218] = "Terrain", [220] = "LightProbeGroup", [221] = "AnimatorOverrideController", [222] = "CanvasRenderer",
        [223] = "Canvas", [224] = "RectTransform", [225] = "CanvasGroup", [226] = "BillboardAsset", [227] = "BillboardRenderer",
        [228] = "SpeedTreeWindAsset", [229] = "AnchoredJoint2D", [230] = "Joint2D", [231] = "SpringJoint2D", [232] = "DistanceJoint2D",
        [233] = "HingeJoint2D", [234] = "SliderJoint2D", [235] = "WheelJoint2D", [236] = "ClusterInputManager",
        [237] = "BaseVideoTexture", [238] = "NavMeshData", [240] = "AudioMixer", [241] = "AudioMixerController",
        [243] = "AudioMixerGroupController", [244] = "AudioMixerEffectController", [245] = "AudioMixerSnapshotController",
        [246] = "PhysicsUpdateBehaviour2D", [247] = "ConstantForce2D", [248] = "Effector2D", [249] = "AreaEffector2D",
        [250] = "PointEffector2D", [251] = "PlatformEffector2D", [252] = "SurfaceEffector2D", [253] = "BuoyancyEffector2D",
        [254] = "RelativeJoint2D", [255] = "FixedJoint2D", [256] = "FrictionJoint2D", [257] = "TargetJoint2D",
        [258] = "LightProbes", [259] = "LightProbeProxyVolume", [271] = "SampleClip", [272] = "AudioMixerSnapshot",
        [273] = "AudioMixerGroup", [290] = "AssetBundleManifest", [300] = "RuntimeInitializeOnLoadManager",
        [310] = "UnityConnectSettings", [319] = "AvatarMask", [320] = "PlayableDirector", [328] = "VideoPlayer",
        [329] = "VideoClip", [330] = "ParticleSystemForceField", [331] = "SpriteMask", [363] = "OcclusionCullingData",
        [1001] = "PrefabInstance", [1002] = "EditorExtensionImpl", [1101] = "AnimatorStateTransition", [1102] = "AnimatorState",
        [1105] = "HumanTemplate", [1107] = "AnimatorStateMachine", [1108] = "PreviewAnimationClip", [1109] = "AnimatorTransition",
        [1111] = "AnimatorTransitionBase", [1120] = "LightingDataAsset", [1971053207] = "SpriteShapeRenderer",
        [19719996] = "TilemapCollider2D", [41386430] = "ImportLog", [156049354] = "Grid", [181963792] = "Preset",
        [298342798] = "VisualEffectAsset", [483693784] = "TilemapRenderer", [612988286] = "SpriteAtlasAsset",
        [687078895] = "SpriteAtlas", [825902497] = "RayTracingShader", [1183024399] = "LookAtConstraint",
        [1210832254] = "TerrainLayer", [1223240404] = "MultiArtifactTestImporter", [1386491679] = "PresetManager",
        [1403656975] = "StreamingManager", [1480428607] = "LowerResBlitTexture", [1660057539] = "SceneRoots",
        [1742807556] = "GridLayout", [1839735485] = "Tilemap", [1773428102] = "ParentConstraint", [1818360608] = "PositionConstraint",
        [1818360609] = "RotationConstraint", [1818360610] = "ScaleConstraint", [1953259897] = "TerrainInspector",
        [2083052967] = "VisualEffect", [2089858483] = "PlayerSettingsSplashScreen", [895512359] = "AimConstraint",
        [638013454] = "SpriteAtlasDatabase", [1931382933] = "UIRenderer",
        [73398921] = "VFXRenderer", [208985858] = "SearchIndex", [1691724297] = "ShaderIncludePathTracker",
        [1597193336] = "RoslynAdditionalFileAsset", [1652712579] = "MultiplayerManager", [1505163474] = "SceneVisibilityState",
        [310998524] = "StreamingController", [1046247431] = "AudioResource",
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> s_learned = new();

    /// <summary>Remember a class name seen as a type-tree root (covers IDs missing from the table).</summary>
    internal static void Learn(int classId, string name)
    {
        if (classId != MonoBehaviour && !string.IsNullOrEmpty(name) && !s_names.ContainsKey(classId)) s_learned.TryAdd(classId, name);
    }

    public static string Name(int classId) =>
        s_names.TryGetValue(classId, out var n) ? n : s_learned.TryGetValue(classId, out n) ? n : $"Class{classId}";
}
