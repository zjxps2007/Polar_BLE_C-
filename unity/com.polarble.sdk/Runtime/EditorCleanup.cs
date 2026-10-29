#if UNITY_EDITOR
using UnityEditor;
namespace Polar.Native
{
    [InitializeOnLoad]
    internal static class EditorCleanup
    {
        static EditorCleanup() { AssemblyReloadEvents.beforeAssemblyReload+=PolarContext.DisposeAll;EditorApplication.playModeStateChanged+=OnPlayState; }
        static void OnPlayState(PlayModeStateChange state) { if(state==PlayModeStateChange.ExitingPlayMode)PolarContext.DisposeAll(); }
    }
}
#endif
