// Kit #7 tests track: the oracle project's editor side (E1's E1Build pattern): the boot scene with OracleMain, the player settings, and the
// macOS Mono players (arm64 = this Mac's architecture, x64 = the second reference; stripping off, as the game's player).
using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class OracleBuild
{
    static string Root => Environment.GetEnvironmentVariable("ORACLE_BUILD") ?? "$SHEPHERD_WORK/kit7/tests-work/oracle/build/";

    static void Setup()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("OracleMain").AddComponent<OracleMain>();
        EditorSceneManager.SaveScene(scene, "Assets/OracleBoot.unity");
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/OracleBoot.unity", true) };
        PlayerSettings.companyName = "ShepherdOracle"; PlayerSettings.productName = "Oracle";
        PlayerSettings.runInBackground = true; PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 320; PlayerSettings.defaultScreenHeight = 240; PlayerSettings.resizableWindow = false;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.Disabled);
        PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Standalone, ApiCompatibilityLevel.NET_Standard);
        PlayerSettings.usePlayerLog = true;
        AssetDatabase.SaveAssets();
    }
    static void SetArch(string arch)
    {
        var t = Type.GetType("UnityEditor.OSXStandalone.UserBuildSettings, UnityEditor.OSXStandalone.Extensions");
        var p = t?.GetProperty("architecture", BindingFlags.Public | BindingFlags.Static);
        if (p != null) p.SetValue(null, Enum.Parse(p.PropertyType, arch == "arm64" ? "ARM64" : "x64"));
        else EditorUserBuildSettings.SetPlatformSettings("Standalone", "Architecture", arch == "arm64" ? "ARM64" : "x64");
        Debug.Log("Oracle: architecture " + arch);
    }
    static bool Build(string arch)
    {
        SetArch(arch);
        var rep = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { "Assets/OracleBoot.unity" }, locationPathName = Root + arch + "/Oracle.app", target = BuildTarget.StandaloneOSX, options = BuildOptions.None });
        Debug.Log("Oracle: build " + arch + " -> " + rep.summary.result + " errors=" + rep.summary.totalErrors + " size=" + rep.summary.totalSize);
        return rep.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded;
    }
    public static void BuildBoth() { AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate); Setup(); bool ok = Build("arm64") && Build("x64"); EditorApplication.Exit(ok ? 0 : 1); }
    public static void BuildArm64() { AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate); Setup(); EditorApplication.Exit(Build("arm64") ? 0 : 1); }
}
