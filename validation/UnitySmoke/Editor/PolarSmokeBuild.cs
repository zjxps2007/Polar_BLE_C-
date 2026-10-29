using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PolarValidation
{
    public static class PolarSmokeBuild
    {
        private static Task<SmokeResult> _pending;
        private static CancellationTokenSource _timeout;
        public static void RunEditor()
        {
            ConfigureNativeImporter();
            _timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            _pending = PolarSmoke.RunAsync("UnityEditor-Mono", _timeout.Token);
            EditorApplication.update += CompleteEditor;
        }

        private static void CompleteEditor()
        {
            if (!_pending.IsCompleted) return;
            EditorApplication.update -= CompleteEditor;
            try
            {
                var result = _pending.GetAwaiter().GetResult();
                PolarSmoke.Write(result, "UnityEditor-Mono.json");
                _timeout.Dispose();
                EditorApplication.Exit(result.success ? 0 : 1);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        public static void BuildMono() => Build(ScriptingImplementation.Mono2x, "Mono");
        public static void BuildIl2Cpp() => Build(ScriptingImplementation.IL2CPP, "IL2CPP");

        private static void Build(ScriptingImplementation backend, string name)
        {
            ConfigureNativeImporter();
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, ManagedStrippingLevel.Low);
            PlayerSettings.productName = "Polar BLE Unity Validation";
            PlayerSettings.companyName = "PolarBlePortValidation";
            PlayerSettings.runInBackground = true;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Polar BLE smoke").AddComponent<PolarSmokeRunner>();
            const string scenePath = "Assets/PolarSmoke.unity";
            if (!EditorSceneManager.SaveScene(scene, scenePath)) throw new IOException("Failed to save smoke scene.");
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = Path.Combine(project, "Builds", name, "PolarSmoke.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scenePath }, locationPathName = output,
                target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development
            });
            var summary = report.summary;
            string evidence = PolarSmoke.Argument("--polar-results", Path.Combine(project, "SmokeResults"));
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "Build-" + name + ".txt"),
                "Unity=" + Application.unityVersion + "\nBackend=" + name + "\nResult=" + summary.result +
                "\nErrors=" + summary.totalErrors + "\nWarnings=" + summary.totalWarnings +
                "\nDuration=" + summary.totalTime + "\nBytes=" + summary.totalSize + "\nOutput=" + output + "\n");
            Debug.Log("POLAR_BUILD " + name + " result=" + summary.result + " errors=" + summary.totalErrors + " warnings=" + summary.totalWarnings);
            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        private static void ConfigureNativeImporter()
        {
            const string path = "Packages/com.polarble.sdk/Runtime/Plugins/x86_64/Polar.Native.dll";
            var importer = AssetImporter.GetAtPath(path) as PluginImporter;
            if (importer == null || !importer.isNativePlugin) throw new InvalidOperationException("Expected published NativeAOT plugin at " + path);
            bool changed = importer.GetCompatibleWithAnyPlatform() || !importer.GetCompatibleWithEditor()
                || !importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64)
                || importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows)
                || importer.GetCompatibleWithPlatform(BuildTarget.StandaloneLinux64)
                || importer.GetCompatibleWithPlatform(BuildTarget.StandaloneOSX)
                || importer.GetEditorData("CPU") != "x86_64" || importer.GetEditorData("OS") != "Windows";
            if (!changed) return;
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);
            importer.SetEditorData("CPU", "x86_64");
            importer.SetEditorData("OS", "Windows");
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, false);
            importer.SetPlatformData(BuildTarget.StandaloneWindows64, "CPU", "x86_64");
            importer.SaveAndReimport();
        }
    }
}
