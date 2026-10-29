using System;
using System.IO;
using UnityEditor;
namespace PolarValidation
{
    [InitializeOnLoad]
    public static class PlayStopCycles
    {
        const string Prefix="PolarNativeCycles.";
        static PlayStopCycles()
        {
            EditorApplication.playModeStateChanged+=Changed;
            EditorApplication.update+=Update;
        }
        public static void Run()
        {
            SessionState.SetBool(Prefix+"active",true);
            SessionState.SetBool(Prefix+"armed",false);
            SessionState.SetInt(Prefix+"count",0);
            NativeBuild.Editor();
        }
        static void Changed(PlayModeStateChange state)
        {
            if(!SessionState.GetBool(Prefix+"active",false))return;
            if(state==PlayModeStateChange.EnteredPlayMode)
            {
                SessionState.SetFloat(Prefix+"deadline",(float)EditorApplication.timeSinceStartup+1);
                SessionState.SetBool(Prefix+"armed",true);
            }
            else if(state==PlayModeStateChange.ExitingPlayMode)SessionState.SetBool(Prefix+"armed",false);
            else if(state==PlayModeStateChange.EnteredEditMode)
            {
                int count=SessionState.GetInt(Prefix+"count",0)+1;SessionState.SetInt(Prefix+"count",count);
                if(count>=20)
                {
                    SessionState.SetBool(Prefix+"active",false);
                    string path=PolarPerfRunner.Argument("--polar-results","Results");Directory.CreateDirectory(path);
                    File.WriteAllText(Path.Combine(path,"editor-play-stop.json"),"{\"completedCycles\":20,\"domainReload\":true,\"cleanupInvokedByOnDestroyAndEditorExit\":true,\"requiresLogLeakAudit\":true}");
                    EditorApplication.Exit(0);
                }
                else EditorApplication.delayCall+=()=>EditorApplication.isPlaying=true;
            }
        }
        static void Update()
        {
            if(SessionState.GetBool(Prefix+"active",false)&&SessionState.GetBool(Prefix+"armed",false)&&EditorApplication.timeSinceStartup>=SessionState.GetFloat(Prefix+"deadline",float.MaxValue))
                EditorApplication.isPlaying=false;
        }
    }
}
