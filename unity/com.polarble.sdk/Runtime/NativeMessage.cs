using System;
using System.Runtime.InteropServices;
using System.Text;
namespace Polar.Native
{
    public enum FieldKind : uint { Int32=1, Int64=2, UInt32=3, UInt64=4, Double=5, Float=6, Bool=7, Enum=8, Bytes=9, Message=10 }
    [StructLayout(LayoutKind.Sequential, Pack=8)]
    public struct Field
    {
        public uint StructSize;
        public FieldKind Kind;
        public uint Present, Count;
        public long Signed;
        public ulong Unsigned;
        public double Real;
        public uint DataLength, Reserved;
    }
    internal static unsafe partial class Api
    {
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_release_stream(ulong context,ulong stream);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_message_parse(byte* schema,uint schemaLength,byte* data,uint length,out ulong message);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_message_set(ulong message,byte* path,uint pathLength,uint index,in Field value,byte* data,uint length);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_message_field(ulong message,byte* path,uint pathLength,uint index,ref Field value,byte* data,uint capacity,out uint length);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_message_serialize(ulong message,byte* data,uint capacity,out uint length);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int polar_sdk_message_destroy(ulong message);
    }
    /// <summary>Control-plane message lifetime only. All protobuf parsing and encoding stays native.
    /// Uses managed allocations; do not call this on the sensor batch receive path.</summary>
    public sealed unsafe class NativeMessage : IDisposable
    {
        ulong handle;
        public NativeMessage(string schema,byte[] wire=null)
        {
            byte[] name=Encoding.UTF8.GetBytes(schema);
            fixed(byte* s=name)fixed(byte* b=wire)Check(Api.polar_sdk_message_parse(s,(uint)name.Length,b,(uint)(wire?.Length??0),out handle));
        }
        public NativeMessage Set(string path,Field value,byte[] data=null,uint index=0)
        {
            CheckAlive();byte[] name=Encoding.UTF8.GetBytes(path);value.StructSize=(uint)sizeof(Field);
            fixed(byte* p=name)fixed(byte* b=data)Check(Api.polar_sdk_message_set(handle,p,(uint)name.Length,index,in value,b,(uint)(data?.Length??0)));
            return this;
        }
        public NativeMessage SetUInt64(string path,ulong value)=>Set(path,new Field {Kind=FieldKind.UInt64,Present=1,Unsigned=value});
        public NativeMessage SetInt64(string path,long value)=>Set(path,new Field {Kind=FieldKind.Int64,Present=1,Signed=value});
        public NativeMessage SetInt32(string path,int value)=>Set(path,new Field {Kind=FieldKind.Int32,Present=1,Signed=value});
        public NativeMessage SetBool(string path,bool value)=>Set(path,new Field {Kind=FieldKind.Bool,Present=1,Unsigned=value?1ul:0ul});
        public NativeMessage SetEnum(string path,int value)=>Set(path,new Field {Kind=FieldKind.Enum,Present=1,Signed=value});
        public NativeMessage SetUInt32(string path,uint value,uint index=0)=>Set(path,new Field {Kind=FieldKind.UInt32,Present=1,Unsigned=value},null,index);
        public NativeMessage SetText(string path,string value)=>Set(path,new Field {Kind=FieldKind.Bytes,Present=1},Encoding.UTF8.GetBytes(value));
        public NativeMessage SetBytes(string path,byte[] value)=>Set(path,new Field {Kind=FieldKind.Bytes,Present=1},value);
        public Field Get(string path,out byte[] data,uint index=0)
        {
            CheckAlive();byte[] name=Encoding.UTF8.GetBytes(path);var f=new Field {StructSize=(uint)sizeof(Field)};
            fixed(byte* p=name)
            {
                Check(Api.polar_sdk_message_field(handle,p,(uint)name.Length,index,ref f,null,0,out uint length));
                data=length==0?Array.Empty<byte>():new byte[length];
                if(length!=0)fixed(byte* b=data)Check(Api.polar_sdk_message_field(handle,p,(uint)name.Length,index,ref f,b,length,out _));
            }
            return f;
        }
        public byte[] Serialize()
        {
            CheckAlive();Check(Api.polar_sdk_message_serialize(handle,null,0,out uint length));var bytes=new byte[length];
            fixed(byte* b=bytes)Check(Api.polar_sdk_message_serialize(handle,b,length,out _));return bytes;
        }
        public ulong Submit(PolarContext context,ulong device,uint service)
        {
            byte[] wire=Serialize();fixed(byte* b=wire)return context.Submit(new Command {Operation=(Operation)50,Device=device,Flags=service,Data=b,DataLength=(uint)wire.Length});
        }
        public ulong Submit(PolarContext context,ulong device,Service service)=>Submit(context,device,(uint)service);
        public void Dispose() { if(handle==0)return;Api.polar_sdk_message_destroy(handle);handle=0;GC.SuppressFinalize(this); }
        ~NativeMessage() { Dispose(); }
        void CheckAlive() { if(handle==0)throw new ObjectDisposedException(nameof(NativeMessage)); }
        static void Check(int result) { if(result<0)throw new InvalidOperationException("Polar native message result "+result); }
    }
}
