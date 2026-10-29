using System;
using System.Collections.Generic;
using System.IO;
using Polar.Native;
using Unity.Collections;
using UnityEngine;
namespace PolarValidation
{
    [Serializable] public class LiveResult
    {
        public string unity,backend,mode,address,sdkSha256;
        public double requestedSeconds,elapsedSeconds,streamSeconds,hrMaxGapSeconds,accMaxGapSeconds;
        public ulong hrSamples,accSamples;
        public bool readyBeforeCompletion,completed,success;
        public int error,hresult,attError,stage,phase;
        public uint requestedFeatures;
    }
    // Hardware acceptance probe. No replay or managed BLE fallback.
    public sealed class PolarLiveRunner:MonoBehaviour
    {
        PolarContext context; PolarStream hr,acc;
        ulong connectRequest,hrRequest,accRequest;
        LiveResult result; double began,streamBegan,lastHr,lastAcc;
        bool wantHr,wantAcc,finished,ready;
        readonly List<string> events=new List<string>();
        void Start()
        {
            began=Time.realtimeSinceStartupAsDouble;
            result=new LiveResult {unity=Application.unityVersion,address=PolarPerfRunner.Argument("--polar-live",""),
                mode=PolarPerfRunner.Argument("--polar-measurement","hr-acc"),sdkSha256=PolarPerfRunner.Argument("--polar-sha256","unspecified")};
#if UNITY_EDITOR
            result.backend="Editor";
#elif ENABLE_IL2CPP
            result.backend="IL2CPP";
#else
            result.backend="Mono";
#endif
            try
            {
                QualitySettings.vSyncCount=0;Application.targetFrameRate=120;Application.runInBackground=true;
                result.requestedSeconds=double.Parse(PolarPerfRunner.Argument("--polar-seconds","60"),System.Globalization.CultureInfo.InvariantCulture);
                if(result.requestedSeconds<1)throw new ArgumentOutOfRangeException();
                wantHr=result.mode=="hr"||result.mode=="hr-acc";wantAcc=result.mode=="acc"||result.mode=="hr-acc";
                if(!wantHr&&!wantAcc)throw new ArgumentException("Unknown live measurement");
                context=new PolarContext(OverflowPolicy.Lossless,8192);context.EventReceived+=OnEvent;
                var features=ConnectionFeatures.Default|(wantAcc?ConnectionFeatures.Streaming:ConnectionFeatures.None);
                result.requestedFeatures=uint.Parse(PolarPerfRunner.Argument("--polar-features",((uint)features).ToString()));
                connectRequest=context.Connect(Convert.ToUInt64(result.address,16),false,
                    (ConnectionFeatures)result.requestedFeatures);
            }
            catch(Exception e){events.Add(e.ToString());result.error=-11;Finish();}
        }
        void OnEvent(Polar.Native.Event e,NativeArray<byte> payload,int length)
        {
            events.Add($"t={Time.realtimeSinceStartupAsDouble-began:F3} event={e.Type} request={e.Request} result={e.Result} HRESULT={e.HResult:X8} ATT={e.AttError} stage={e.Stage}");
            if(PolarContext.TryGetConnectionState(e,payload,length,out var state))
            {
                result.phase=(int)state.Phase;ready=state.Phase==ConnectionPhase.Ready;
                events.Add($"phase={state.Phase} ready={state.Ready} failed={state.Failed} unavailable={state.Unavailable} pairing={state.PairingStatus} attempts={state.AuthenticationAttempts}");
            }
            if(e.Result<0&&(e.Type==1||e.Type==3||e.Type==4))
            {result.error=e.Result;result.hresult=e.HResult;result.attError=(int)e.AttError;result.stage=(int)e.Stage;return;}
            if(e.Type!=1||e.Result!=0)return;
            if(e.Request==connectRequest)
            {
                result.readyBeforeCompletion=ready;
                if(!ready){result.error=-11;return;}
                if(wantHr)hrRequest=context.Start(e.Device,Measurement.HeartRate);
                if(wantAcc)accRequest=context.Start(e.Device,Measurement.Acc);
            }
            if(e.Request==hrRequest&&e.Stream!=0)hr=context.AttachStream(e.Stream,8192);
            if(e.Request==accRequest&&e.Stream!=0)acc=context.AttachStream(e.Stream,8192);
            if(streamBegan==0&&(!wantHr||hr!=null)&&(!wantAcc||acc!=null))streamBegan=Time.realtimeSinceStartupAsDouble;
        }
        void Update()
        {
            if(finished)return;
            var now=Time.realtimeSinceStartupAsDouble;
            if(hr!=null&&hr.Count>0){if(result.hrSamples>0)result.hrMaxGapSeconds=Math.Max(result.hrMaxGapSeconds,now-lastHr);lastHr=now;result.hrSamples+=(ulong)hr.Count;}
            if(acc!=null&&acc.Count>0){if(result.accSamples>0)result.accMaxGapSeconds=Math.Max(result.accMaxGapSeconds,now-lastAcc);lastAcc=now;result.accSamples+=(ulong)acc.Count;}
            if(result.error==0){if(hr!=null&&hr.Error<0)result.error=hr.Error;else if(acc!=null&&acc.Error<0)result.error=acc.Error;}
            if(streamBegan!=0&&now-streamBegan>=result.requestedSeconds)result.completed=true;
            if(streamBegan==0&&now-began>90)result.error=-5;
            if(result.completed||result.error<0)Finish();
        }
        void Finish()
        {
            if(finished)return;finished=true;
            double now=Time.realtimeSinceStartupAsDouble;
            result.elapsedSeconds=now-began;result.streamSeconds=streamBegan==0?0:now-streamBegan;
            result.success=result.completed&&result.error==0&&result.readyBeforeCompletion&&
                (!wantHr||result.hrSamples>=3&&now-lastHr<=5&&result.hrMaxGapSeconds<=5)&&
                (!wantAcc||result.accSamples>=3&&now-lastAcc<=5&&result.accMaxGapSeconds<=5);
            context?.Dispose();context=null;
            string path=PolarPerfRunner.Argument("--polar-results",Application.persistentDataPath);Directory.CreateDirectory(path);
            string name=Path.Combine(path,result.backend+"-live-"+result.mode);
            File.WriteAllText(name+".json",JsonUtility.ToJson(result,true));File.WriteAllLines(name+".log",events);
            Debug.Log("POLAR_LIVE_RESULT "+JsonUtility.ToJson(result));
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying=false;
            UnityEditor.EditorApplication.delayCall+=()=>UnityEditor.EditorApplication.Exit(result.success?0:1);
#else
            Application.Quit(result.success?0:1);
#endif
        }
        void OnDestroy(){context?.Dispose();}
    }
}
