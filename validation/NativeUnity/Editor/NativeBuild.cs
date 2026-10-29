using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace PolarValidation
{
    public static class NativeBuild
    {
        public static void Editor() { Scene();EditorApplication.isPlaying=true; }
        public static void Mono()=>Build(ScriptingImplementation.Mono2x,"Mono");
        public static void Il2Cpp()=>Build(ScriptingImplementation.IL2CPP,"IL2CPP");
        static void Scene()
        {
            const string native="Packages/com.polarble.sdk/Runtime/Plugins/x86_64/polar_sdk.dll";
            AssetDatabase.ImportAsset(native,ImportAssetOptions.ForceUpdate);
            var plugin=AssetImporter.GetAtPath(native) as PluginImporter;
            if(plugin==null||!plugin.isNativePlugin)throw new InvalidOperationException("Native polar_sdk.dll importer missing");
            plugin.SetCompatibleWithAnyPlatform(false);plugin.SetCompatibleWithEditor(true);plugin.SetEditorData("CPU","x86_64");plugin.SetEditorData("OS","Windows");plugin.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64,true);plugin.SetPlatformData(BuildTarget.StandaloneWindows64,"CPU","x86_64");plugin.SaveAndReimport();
            PlayerSettings.allowUnsafeCode=true;PlayerSettings.runInBackground=true;
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var camera=new GameObject("Camera").AddComponent<Camera>();camera.backgroundColor=new Color(.04f,.07f,.12f);camera.clearFlags=CameraClearFlags.SolidColor;
            new GameObject("Polar Native Performance").AddComponent<PolarPerfRunner>();
            EditorSceneManager.SaveScene(scene,"Assets/NativeValidation.unity");
        }
        static void Build(ScriptingImplementation backend,string name)
        {
            Scene();PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone,ApiCompatibilityLevel.NET_Standard);PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,backend);
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone,ManagedStrippingLevel.Low);PlayerSettings.productName="Polar Native Validation";
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes=new[]{"Assets/NativeValidation.unity"},locationPathName="Builds/"+name+"/PolarNativeValidation.exe",target=BuildTarget.StandaloneWindows64,options=BuildOptions.None });
            string path=PolarPerfRunner.Argument("--polar-results","Results");Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"build-"+name+".txt"),"Result="+report.summary.result+"\nErrors="+report.summary.totalErrors+"\nWarnings="+report.summary.totalWarnings+"\nSeconds="+report.summary.totalTime.TotalSeconds);
            EditorApplication.Exit(report.summary.result==BuildResult.Succeeded?0:1);
        }
    }
}
