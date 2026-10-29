using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.LowLevel;
namespace Polar.Native
{
    public sealed class PolarStream : IDisposable
    {
        public ulong Handle { get; private set; }
        public NativeArray<Sample> Samples { get; private set; }
        public int Count { get; internal set; }
        public int Error { get; internal set; }
        internal PolarStream(ulong handle,int capacity) { Handle=handle; Samples=new NativeArray<Sample>(capacity,Allocator.Persistent,NativeArrayOptions.UninitializedMemory); }
        public void Dispose() { if(Samples.IsCreated)Samples.Dispose(); Samples=default;Handle=0;Count=0; }
    }
    // Own one context per application's BLE lifetime. This class is main-thread only.
    public sealed unsafe class PolarContext : IDisposable
    {
        static readonly List<PolarContext> Active=new List<PolarContext>();
        static readonly ProfilerMarker PumpMarker=new ProfilerMarker("Polar.Native.BatchReceive");
        static bool installed;
        readonly List<PolarStream> streams=new List<PolarStream>();
        NativeArray<byte> eventBuffer;
        ulong handle;
        public ulong Handle=>handle;
        public ulong LastPumpNs { get; private set; }
        public long LastAllocatedBytes { get; private set; }
        public event Action<Event,NativeArray<byte>,int> EventReceived;
        public PolarContext(OverflowPolicy policy=OverflowPolicy.Latest,int sampleCapacity=4096)
        {
            if(Api.polar_sdk_abi_version()!=2||sizeof(Sample)!=896||sizeof(Command)!=104||sizeof(Event)!=72||sizeof(ConnectOptions)!=16||sizeof(ConnectionState)!=32)throw new InvalidOperationException("Polar native ABI mismatch");
            var config=new Config { StructSize=(uint)sizeof(Config),AbiVersion=2,SampleCapacity=(uint)sampleCapacity,IngressCapacity=256,EventCapacity=256,OverflowPolicy=(uint)policy,TimeoutMs=30000 };
            Check(Api.polar_sdk_create(in config,out handle));
            eventBuffer=new NativeArray<byte>(65536,Allocator.Persistent,NativeArrayOptions.UninitializedMemory);
            Active.Add(this);Install();
        }
        public static ulong ClockNs=>Api.polar_sdk_clock_ns();
        public ulong Submit(Command command) { ThrowIfDisposed();command.StructSize=(uint)sizeof(Command);Check(Api.polar_sdk_submit(handle,in command,out var request));return request; }
        public void Release(PolarStream stream) { ThrowIfDisposed();Check(Api.polar_sdk_release_stream(handle,stream.Handle));streams.Remove(stream);stream.Dispose(); }
        public ulong Scan()=>Submit(new Command { Operation=Operation.ScanStart });
        public ulong Initialize(ulong device,uint features=0)=>Submit(new Command {Operation=Operation.Initialize,Device=device,Flags=features});
        public ulong Capabilities(ulong device)=>Submit(new Command {Operation=Operation.Capabilities,Device=device});
        // Explicit recovery of a stale Windows bond; Connect never unpairs automatically.
        public ulong Unpair(ulong address)=>Submit(new Command {Operation=Operation.Unpair,Address=address});
        public ulong DeviceInfo(ulong device)=>Submit(new Command {Operation=Operation.DeviceInfo,Device=device});
        public ulong ObserveRadio(bool enabled=true)=>Submit(new Command {Operation=Operation.Radio,Flags=enabled?1u:2u});
        public ulong HrBroadcasts(ulong[] addresses=null,bool deduplicate=true) {fixed(ulong* p=addresses)return Submit(new Command {Operation=Operation.ScanStart,Flags=4u|(deduplicate?2u:0u),Data=(byte*)p,DataLength=(uint)(addresses?.Length??0)*8});}
        // Connect completion means the native core finished preparing the requested features.
        // No Windows Settings pairing step is required. Protected ATT operations authenticate on demand.
        public ulong Connect(ulong address,bool automaticReconnect=false,ConnectionFeatures features=ConnectionFeatures.Default,bool authenticate=true)
        {
            var options=new ConnectOptions {StructSize=(uint)sizeof(ConnectOptions),Features=(uint)features,Authentication=authenticate?0u:1u};
            return Submit(new Command {Operation=Operation.Connect,Address=address,Flags=automaticReconnect?2u:0u,Data=(byte*)&options,DataLength=(uint)sizeof(ConnectOptions)});
        }
        public static bool TryGetConnectionState(Event e,NativeArray<byte> payload,int length,out ConnectionState state)
        {
            state=default;if(e.Type!=16||length!=sizeof(ConnectionState)||payload.Length<length)return false;
            state=*(ConnectionState*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(payload);return true;
        }
        public ulong Start(ulong device,Measurement type)=>Submit(new Command { Operation=Operation.StreamStart,Device=device,Measurement=type });
        public void Cancel(ulong request) { ThrowIfDisposed();Check(Api.polar_sdk_cancel(handle,request)); }
        public PolarStream AttachStream(ulong stream,int capacity=4096) { ThrowIfDisposed();for(int i=0;i<streams.Count;i++)if(streams[i].Handle==stream)return streams[i];var result=new PolarStream(stream,capacity);streams.Add(result);return result; }
        public StreamInfo GetInfo(PolarStream stream) { ThrowIfDisposed();var info=new StreamInfo {StructSize=(uint)sizeof(StreamInfo)};Check(Api.polar_sdk_stream_info(handle,stream.Handle,ref info));return info; }
        public StreamMetrics GetMetrics(PolarStream stream) {ThrowIfDisposed();var metrics=new StreamMetrics {StructSize=(uint)sizeof(StreamMetrics)};Check(Api.polar_sdk_get_stream_metrics(handle,stream.Handle,ref metrics));return metrics;}
        public void ReplayFrame(PolarStream stream,byte[] frame) { ThrowIfDisposed();fixed(byte* p=frame)Check(Api.polar_sdk_replay_frame(handle,stream.Handle,p,(uint)frame.Length,ClockNs)); }
        void Pump()
        {
            ulong start=ClockNs;long allocated=GC.GetAllocatedBytesForCurrentThread();using(PumpMarker.Auto())
            {
                for(int i=0;i<streams.Count;i++)
                {
                    var stream=streams[i];if(stream.Handle==0)continue;
                    int result=Api.polar_sdk_read_samples(handle,stream.Handle,(Sample*)NativeArrayUnsafeUtility.GetUnsafePtr(stream.Samples),(uint)stream.Samples.Length,out uint count);
                    stream.Count=(int)count;if(result<0)stream.Error=result;
                }
                for(int i=0;i<128;i++)
                {
                    var e=new Event { StructSize=(uint)sizeof(Event) };
                    int result=Api.polar_sdk_poll_event(handle,ref e,(byte*)NativeArrayUnsafeUtility.GetUnsafePtr(eventBuffer),(uint)eventBuffer.Length,out uint length);
                    if(result==1)break;
                    if(result==2) { eventBuffer.Dispose();eventBuffer=new NativeArray<byte>((int)length,Allocator.Persistent,NativeArrayOptions.UninitializedMemory);i--;continue; }
                    Check(result);EventReceived?.Invoke(e,eventBuffer,(int)length);
                    if(handle==0)break;
                }
            }
            LastPumpNs=ClockNs-start;
            LastAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
        }
        static void UpdateAll() { for(int i=0;i<Active.Count;i++)Active[i].Pump(); }
        struct PolarEarlyUpdate { }
        static void Install()
        {
            if(installed)return;var loop=PlayerLoop.GetCurrentPlayerLoop();
            for(int i=0;i<loop.subSystemList.Length;i++)if(loop.subSystemList[i].type==typeof(UnityEngine.PlayerLoop.EarlyUpdate))
            {
                var early=loop.subSystemList[i];var old=early.subSystemList;var next=new PlayerLoopSystem[old.Length+1];Array.Copy(old,0,next,1,old.Length);
                next[0]=new PlayerLoopSystem {type=typeof(PolarEarlyUpdate),updateDelegate=UpdateAll};early.subSystemList=next;loop.subSystemList[i]=early;PlayerLoop.SetPlayerLoop(loop);installed=true;break;
            }
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() { DisposeAll();var loop=PlayerLoop.GetCurrentPlayerLoop();for(int i=0;i<loop.subSystemList.Length;i++){var parent=loop.subSystemList[i];if(parent.subSystemList==null)continue;var children=new List<PlayerLoopSystem>(parent.subSystemList);children.RemoveAll(x=>x.type==typeof(PolarEarlyUpdate));parent.subSystemList=children.ToArray();loop.subSystemList[i]=parent;}PlayerLoop.SetPlayerLoop(loop);installed=false;Application.quitting-=DisposeAll;Application.quitting+=DisposeAll; }
        public static void DisposeAll() { while(Active.Count>0)Active[Active.Count-1].Dispose(); }
        public void Dispose() { if(handle==0)return;var native=handle;handle=0;Active.Remove(this);Api.polar_sdk_destroy(native);for(int i=0;i<streams.Count;i++)streams[i].Dispose();streams.Clear();if(eventBuffer.IsCreated)eventBuffer.Dispose();EventReceived=null; }
        void ThrowIfDisposed() { if(handle==0)throw new ObjectDisposedException(nameof(PolarContext)); }
        static void Check(int result) { if(result<0)throw new InvalidOperationException("Polar SDK result "+result); }
    }
}
