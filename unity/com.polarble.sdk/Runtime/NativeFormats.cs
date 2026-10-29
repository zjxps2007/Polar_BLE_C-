using System;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
namespace Polar.Native
{
    public enum Codec : uint { GzipDecode=1,GzipEncode=2,Aes128=3,Aes256=4,Xor=5,ProtoRoundtrip=6,ProtoMerge=7,WatchfaceGet=8,WatchfaceSet=9,ZipList=10,JsonValidate=11,RestEvents=12 }
    [StructLayout(LayoutKind.Sequential,Pack=8)]
    public unsafe struct OfflineInfo
    {
        public uint StructSize,Version,FirmwareHash,HeaderFree;
        public fixed byte StartTimeUtc[20];
        public uint SampleRate;
        public ulong SampleCount,LastTimestampNs;
    }
    [StructLayout(LayoutKind.Sequential,Pack=8)]
    public unsafe struct RecordingPart { public byte* Data;public uint Length,Reserved; }
    [StructLayout(LayoutKind.Sequential,Pack=8)]
    public unsafe struct RecordingOptions
    {
        public uint StructSize,Strategy,Measurement,DerivedMethods;
        public byte* Key;public uint KeyLength,Reserved;
    }
    [StructLayout(LayoutKind.Sequential,Pack=8)]
    internal unsafe struct Transform
    {
        public uint StructSize,Operation;
        public byte* Data;public uint DataLength,Reserved;
        public byte* Parameters;public uint ParametersLength,Reserved2;
        public byte* Schema;public uint SchemaLength,Reserved3;
    }
    internal static unsafe partial class Api
    {
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_transform(in Transform transform,byte* output,uint capacity,out uint length);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_decode_recordings(RecordingPart* parts,uint count,in RecordingOptions options,ref OfflineInfo info,Sample* output,uint capacity,out uint written);
    }
    /// <summary>Offline/control operations; keep them off the per-frame receive path.
    /// Protocols, crypto, ZIP and protobuf are implemented exclusively in polar_sdk.dll.</summary>
    public static unsafe class NativeFormats
    {
        public static byte[] Convert(Codec codec,byte[] input,byte[] parameters=null,string schema=null)
        {
            byte[] name=schema==null?null:Encoding.UTF8.GetBytes(schema);
            fixed(byte* b=input)fixed(byte* p=parameters)fixed(byte* s=name)
            {
                var t=new Transform {StructSize=(uint)sizeof(Transform),Operation=(uint)codec,Data=b,DataLength=(uint)(input?.Length??0),Parameters=p,ParametersLength=(uint)(parameters?.Length??0),Schema=s,SchemaLength=(uint)(name?.Length??0)};
                Check(Api.polar_sdk_transform(in t,null,0,out uint size));
                byte[] result=new byte[size];fixed(byte* r=result)Check(Api.polar_sdk_transform(in t,r,size,out _));return result;
            }
        }
        /// <summary>Parts/key memory must remain valid and immutable during the call.
        /// An uncreated/empty output performs sizing and returns 2 when a buffer is needed.</summary>
        public static int DecodeRecordings(NativeArray<RecordingPart> parts,RecordingOptions options,ref OfflineInfo info,NativeArray<Sample> output,out uint written)
        {
            options.StructSize=(uint)sizeof(RecordingOptions);info.StructSize=(uint)sizeof(OfflineInfo);
            int result=Api.polar_sdk_decode_recordings(parts.IsCreated?(RecordingPart*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(parts):null,(uint)parts.Length,in options,ref info,output.IsCreated?(Sample*)NativeArrayUnsafeUtility.GetUnsafePtr(output):null,(uint)output.Length,out written);
            Check(result);return result;
        }
        static void Check(int result) { if(result<0)throw new InvalidOperationException("Polar native format result "+result); }
    }
}
