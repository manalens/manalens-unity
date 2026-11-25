// Batch-mode entry points for scripts/test-unity-player-local.mjs:
//   Unity -batchmode -quit -projectPath <this> -executeMethod E2EBuild.BuildMac -manalens-out=<dir>
//   Unity -batchmode -quit -projectPath <this> -executeMethod E2EBuild.BuildWebGL -manalens-out=<dir>
//   Unity -batchmode -projectPath <this> -executeMethod E2EBuild.PlayMode -manalens-mode=editor-on ...
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class E2EBuild
{
    const string ExitKey = "ManalensE2E.ExitAfterPlayMode";

    // Entering Play mode reloads the domain: register the exit handler again after each reload.
    static E2EBuild()
    {
        if (!SessionState.GetBool(ExitKey, false)) return;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredEditMode) EditorApplication.Exit(0);
        };
    }

    const string ScenePath = "Assets/E2E/E2E.unity";

    static string Arg(string name)
    {
        foreach (var arg in Environment.GetCommandLineArgs())
            if (arg.StartsWith("-manalens-" + name + "=", StringComparison.Ordinal)) return arg.Substring(name.Length + 11);
        return null;
    }

    /// <summary>Fixed names, so the persistent data path is known: ~/Library/Application Support/ManalensE2E/ManalensE2E on macOS.</summary>
    static void Setup()
    {
        PlayerSettings.companyName = "ManalensE2E";
        PlayerSettings.productName = "ManalensE2E";
        PlayerSettings.bundleVersion = "0.1.0-player";
        PlayerSettings.runInBackground = true;
        PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Standalone, "dev.manalens.e2e");
        if (!File.Exists(ScenePath))
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
    }

    static void Build(BuildTarget target, string location, BuildOptions options = BuildOptions.None)
    {
        Setup();
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = location,
            target = target,
            options = options,
        });
        Debug.Log($"E2E_BUILD {target} {report.summary.result} {report.summary.totalErrors} errors {report.summary.totalSize} bytes");
        if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    public static void BuildMac()
    {
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        Build(BuildTarget.StandaloneOSX, Path.Combine(Arg("out"), "ManalensE2E.app"));
    }

    public static void BuildWebGL()
    {
        // WebGL is always IL2CPP, with managed code stripping: the same AOT path as Android and iOS IL2CPP builds.
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithStacktrace;
        PlayerSettings.SetManagedStrippingLevel(UnityEditor.Build.NamedBuildTarget.WebGL, ManagedStrippingLevel.High);
        Build(BuildTarget.WebGL, Arg("out"));
    }

    /// <summary>Enters Play mode in the batch-mode editor; quits the editor when Play mode ends.</summary>
    public static void PlayMode()
    {
        Setup();
        EditorSceneManager.OpenScene(ScenePath);
        SessionState.SetBool(ExitKey, true);
        EditorApplication.EnterPlaymode();
    }
}
