using System;
using System.IO;
using Polar.Native;
using Unity.Collections;
using UnityEngine;
namespace PolarValidation
{
    [Serializable] public class NativeResult
    {
        public string unity,backend,mode="native ACC replay, 120 FPS target",sdkSha256;
        public double requestedSeconds,elapsedSeconds,fps,frameP99Ms,sdkP50Ms,sdkP95Ms,sdkP99Ms,readyP99Ms,consumeP99Ms;
        public long sdkAllocatedBytes;
        public ulong samples,totalObserved,produced,consumed,queued,skipped,ingressDropped,highWatermark;
        public ulong ingressBytes,decoderHandoffBytes,sampleQueueCopyBytes,consumerCopyBytes;
        public int load,error,frames;
        public bool drained,success;
    }
    public sealed class PolarPerfRunner:MonoBehaviour
    {
        PolarContext context; PolarStream stream;
        double[] frameTimes,pumpTimes,readyTimes,consumeTimes;
        int count,sampleCount; ulong samples,observed,previousSequence,closeRequest;
        long allocated; double began,last,seconds,measuredElapsed,drainBegan; int load,error;
        bool finished,draining,closeComplete,checkpoint;
        public static string Argument(string name,string fallback)
        {
            var a=Environment.GetCommandLineArgs();
            for(int i=0;i+1<a.Length;i++)if(a[i]==name)return a[i+1];
            return fallback;
        }
        void Start()
        {
            if(Argument("--polar-live","")!="") { finished=true;gameObject.AddComponent<PolarLiveRunner>();Destroy(this);return; }
            began=Time.realtimeSinceStartupAsDouble; last=began;
            try
            {
                QualitySettings.vSyncCount=0;Application.targetFrameRate=120;Application.runInBackground=true;
                seconds=double.Parse(Argument("--polar-seconds","30"),System.Globalization.CultureInfo.InvariantCulture);
                load=int.Parse(Argument("--polar-load","1"));
                if(seconds<1||seconds>18000||load<1||load>4)throw new ArgumentOutOfRangeException();
                int frames=(int)(seconds*200+10000);frameTimes=new double[frames];pumpTimes=new double[frames];
                int capacity=(int)(seconds*64*load+10000);readyTimes=new double[capacity];consumeTimes=new double[capacity];
                context=new PolarContext(OverflowPolicy.Lossless,8192);context.EventReceived+=OnEvent;
                // Exercise native protobuf interop on Mono and IL2CPP before warm-up.
                using(var message=new NativeMessage("polar.sdk.ServiceRequest"))
                {
                    message.SetUInt64("value",ulong.MaxValue);
                    if(message.Get("value",out _).Unsigned!=ulong.MaxValue)throw new Exception("Native protobuf uint64 mismatch");
                }
                var original=new byte[]{0,1,2,255};
                var decoded=NativeFormats.Convert(Codec.GzipDecode,NativeFormats.Convert(Codec.GzipEncode,original));
                if(decoded.Length!=original.Length)throw new Exception("Native gzip length mismatch");
                for(int i=0;i<original.Length;i++)if(decoded[i]!=original[i])throw new Exception("Native gzip byte mismatch");
                context.Submit(new Command {Operation=Operation.ReplayOpen,Measurement=Measurement.Acc,Flags=(uint)load});
            }
            catch(Exception ex) { Debug.LogException(ex);error=-11;Finish(); }
        }
        void OnEvent(Polar.Native.Event e,NativeArray<byte> payload,int n)
        {
            if(e.Request==closeRequest&&closeRequest!=0) {closeComplete=true;return;}
            if(e.Type==1&&e.Stream!=0&&e.Result==0)stream=context.AttachStream(e.Stream,8192);
            if(e.Result<0)error=e.Result;
        }
        void Update()
        {
            if(finished)return;
            double now=Time.realtimeSinceStartupAsDouble,elapsed=now-began;
            if(stream==null){if(elapsed>20){error=-5;Finish();}return;}
            bool measure=elapsed>5&&!draining;
            if(measure)
            {
                if(count>=frameTimes.Length){error=-8;Finish();return;}
                frameTimes[count]=(now-last)*1000;pumpTimes[count]=context.LastPumpNs/1e6;count++;
                allocated+=context.LastAllocatedBytes;
            }
            ulong consumedAt=PolarContext.ClockNs;
            for(int i=0;i<stream.Count;i++)
            {
                var s=stream.Samples[i];observed++;
                if(s.Sequence!=previousSequence+1)error=-8;
                previousSequence=s.Sequence;
                if(measure)
                {
                    samples++;
                    if(sampleCount>=readyTimes.Length){error=-8;break;}
                    readyTimes[sampleCount]=(s.ReadyNs-s.ReceivedNs)/1e6;
                    consumeTimes[sampleCount]=(consumedAt-s.ReceivedNs)/1e6;sampleCount++;
                }
            }
            last=now;if(stream.Error<0)error=stream.Error;
            if(!checkpoint&&seconds>1801&&elapsed>=1805)
            {
                checkpoint=true;Save(context.GetInfo(stream),elapsed-5,1800,false);
            }
            if(!draining&&(elapsed>=seconds+5||error<0))
            {
                draining=true;measuredElapsed=elapsed-5;drainBegan=now;
                closeRequest=context.Submit(new Command {Operation=Operation.ReplayClose,Stream=stream.Handle});
            }
            if(draining&&closeComplete)
            {
                var info=context.GetInfo(stream);
                if(info.PendingFrames==0&&info.QueuedSamples==0)Finish();
            }
            if(draining&&now-drainBegan>10){error=-5;Finish();}
        }
        static double P(double[] values,int n,double fraction)
        {if(n==0||values==null)return 0;Array.Sort(values,0,n);return values[Math.Min(n-1,(int)(fraction*(n-1)))];}
        NativeResult Save(StreamInfo info,double elapsed,double label,bool drained)
        {
            var metrics=stream==null||context==null?default:context.GetMetrics(stream);
            var r=new NativeResult {
                unity=Application.unityVersion,sdkSha256=Argument("--polar-sha256","unspecified"),requestedSeconds=label,elapsedSeconds=elapsed,
                load=load,error=error,frames=count,fps=count/Math.Max(elapsed,.001),samples=samples,totalObserved=observed,
                produced=info.ProducedSamples,consumed=info.ConsumedSamples,queued=info.QueuedSamples,
                skipped=info.SkippedSamples,ingressDropped=info.IngressDropped,highWatermark=info.HighWatermark,
                ingressBytes=metrics.IngressBytes,decoderHandoffBytes=metrics.DecoderBytes,sampleQueueCopyBytes=metrics.SampleQueueBytes,consumerCopyBytes=metrics.ConsumerBytes,
                sdkAllocatedBytes=allocated,frameP99Ms=P(frameTimes,count,.99),sdkP50Ms=P(pumpTimes,count,.5),sdkP95Ms=P(pumpTimes,count,.95),sdkP99Ms=P(pumpTimes,count,.99),
                readyP99Ms=P(readyTimes,sampleCount,.99),consumeP99Ms=P(consumeTimes,sampleCount,.99),drained=drained};
#if UNITY_EDITOR
            r.backend="Editor";
#elif ENABLE_IL2CPP
            r.backend="IL2CPP";
#else
            r.backend="Mono";
#endif
            r.success=error==0&&samples>0&&info.SkippedSamples==0&&info.IngressDropped==0&&allocated==0&&r.sdkP99Ms<=.25&&r.readyP99Ms<=1&&r.consumeP99Ms<=10&&r.fps>=119&&(!drained||observed==info.ProducedSamples&&observed==info.ConsumedSamples);
            string path=Argument("--polar-results",Application.persistentDataPath);Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path,r.backend+"-"+load+"x-"+label+"s.json"),JsonUtility.ToJson(r,true));
            Debug.Log("POLAR_NATIVE_RESULT "+JsonUtility.ToJson(r));return r;
        }
        void Finish()
        {
            if(finished)return;finished=true;
            var info=stream==null?default:context.GetInfo(stream);
            var result=Save(info,draining?measuredElapsed:Time.realtimeSinceStartupAsDouble-began-5,seconds,closeComplete);
            context?.Dispose();context=null;
            int exit=result.success?0:1;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying=false;
            UnityEditor.EditorApplication.delayCall+=()=>UnityEditor.EditorApplication.Exit(exit);
#else
            Application.Quit(exit);
#endif
        }
        void OnDestroy() {context?.Dispose();}
    }
}
