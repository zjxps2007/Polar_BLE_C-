using System;
using System.Runtime.InteropServices;
namespace Polar.Native
{
    public enum Operation : uint { ScanStart=1, ScanStop=2, Connect=3, Disconnect=4, PairState=5, Pair=6, Services=7, Radio=8, Read=9, Write=10, Subscribe=11, AutoConnect=12, ReconnectPolicy=13, Initialize=14, DeviceInfo=15, Capabilities=16, PmdFeatures=20, PmdSettings=21, StreamStart=22, StreamStop=23, PmdCommand=24, FileGet=30, FilePut=31, FileRemove=32, FileList=33, Query=34, Notification=35, Features=40, Telemetry=41, Service=50, ReplayOpen=100, ReplayClose=101 }
    public enum Measurement : uint { Ecg=0, Ppg=1, Acc=2, Ppi=3, Gyroscope=5, Magnetometer=6, SkinTemperature=7, Location=10, Pressure=11, Temperature=12, OfflineHeartRate=14, Derived=15, HeartRate=64, Thermometer=65, BatteryLevel=66, BatteryStatus=67, HrAdvertisement=68 }
    public enum OverflowPolicy : uint { Latest=0, Lossless=1 }
    [StructLayout(LayoutKind.Sequential,Pack=8)]
    public struct Progress {public ulong TransferredBytes,TotalBytes;public uint PathLength,Direction;}
    [StructLayout(LayoutKind.Sequential,Pack=8)]
    public unsafe struct Advertisement {public ulong Address;public int Rssi;public uint Connectable;public fixed byte Name[128];public uint ManufacturerLength;public fixed byte Manufacturer[256];}
    [StructLayout(LayoutKind.Sequential, Pack=8)]
    public unsafe struct Sample
    {
        public ulong TimestampNs, ReceivedNs, ReadyNs, Sequence, Identifier;
        public uint Measurement, Format, Flags, ChannelCount;
        public fixed double Values[32];
        public fixed long Integers[8];
        public uint MetadataLength, Reserved;
        public fixed byte Metadata[512];
        public double Value(int channel) { if ((uint)channel>=ChannelCount||(uint)channel>=32) throw new ArgumentOutOfRangeException(nameof(channel)); fixed(double* p=Values) return p[channel]; }
    }
    [StructLayout(LayoutKind.Sequential, Pack=8)]
    public struct Event
    {
        public uint StructSize, Type;
        public ulong Request, Device, Stream, HostTimeNs;
        public int Result, HResult;
        public uint AttError, Stage;
        public ulong ElapsedNs;
        public uint DataLength, Flags;
    }
    [StructLayout(LayoutKind.Sequential, Pack=8)]
    public struct StreamInfo
    {
        public uint StructSize, Measurement;
        public ulong QueuedSamples, ProducedSamples, ConsumedSamples, SkippedSamples, IngressDropped, HighWatermark;
        public int State, Error;
        public uint SampleRate, PendingFrames;
    }
    [StructLayout(LayoutKind.Sequential,Pack=8)]
    public struct StreamMetrics {public uint StructSize,Reserved;public ulong IngressFrames,IngressBytes,DecoderBytes,SampleQueueBytes,ConsumerBytes;}
    [StructLayout(LayoutKind.Sequential, Pack=8)]
    public unsafe struct Command
    {
        public uint StructSize;
        public Operation Operation;
        public ulong Device, Stream, Address;
        public Measurement Measurement;
        public uint Flags;
        public byte* Path;
        public uint PathLength, TimeoutMs;
        public byte* Data;
        public uint DataLength, Reserved;
        public fixed byte ServiceUuid[16];
        public fixed byte CharacteristicUuid[16];
    }
    [StructLayout(LayoutKind.Sequential, Pack=8)]
    internal struct Config { public uint StructSize, AbiVersion, SampleCapacity, IngressCapacity, EventCapacity, OverflowPolicy, TimeoutMs, Reserved; }
    internal static unsafe partial class Api
    {
        const string Dll="polar_sdk";
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern uint polar_sdk_abi_version();
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern ulong polar_sdk_clock_ns();
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_create(in Config config,out ulong context);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_destroy(ulong context);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_submit(ulong context,in Command command,out ulong request);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_cancel(ulong context,ulong request);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_poll_event(ulong context,ref Event result,byte* buffer,uint capacity,out uint length);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_read_samples(ulong context,ulong stream,Sample* buffer,uint capacity,out uint written);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_stream_info(ulong context,ulong stream,ref StreamInfo info);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_get_stream_metrics(ulong context,ulong stream,ref StreamMetrics metrics);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_replay_frame(ulong context,ulong stream,byte* frame,uint length,ulong receivedNs);
    }
}
