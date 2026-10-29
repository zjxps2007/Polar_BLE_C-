using System;
using System.IO;
using System.Text;

namespace PolarBle.Native
{
    // Version 1 wire format: little-endian fixed-width integers, Int32 byte lengths,
    // strict UTF-8 strings, and Guid.ToByteArray() GUID order (not RFC network order).
    internal static class Wire
    {
        internal const int MaxPayload = 16 * 1024 * 1024;
        internal static readonly UTF8Encoding Utf8 = new(false, true);
        internal static byte[] Encode(Action<BinaryWriter> action)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            action(writer);
            return stream.ToArray();
        }
        internal static void Text(BinaryWriter writer, string text) => Bytes(writer, Utf8.GetBytes(text));
        internal static void Bytes(BinaryWriter writer, byte[] value) { writer.Write(value.Length); writer.Write(value); }
        internal static string Text(BinaryReader reader) => Utf8.GetString(Bytes(reader));
        internal static byte[] Bytes(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxPayload || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid wire buffer length.");
            return reader.ReadBytes(count);
        }
        internal static Guid Guid(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(16);
            if (bytes.Length != 16) throw new EndOfStreamException();
            return new Guid(bytes);
        }
        internal static void End(BinaryReader reader)
        {
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Unexpected trailing request data.");
        }
    }
}
