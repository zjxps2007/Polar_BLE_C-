// Adapted from Polar BLE SDK KvtxScriptUtils / PolarWatchFaceUtils.
// Copyright Polar Electro Oy. See LICENSES/Polar_SDK_License.txt.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace PolarBle.WatchFaces
{
    public sealed class WatchFaceConfiguration
    {
        public ushort TimeStyleId { get; set; }
        public ushort ComplicationLayoutId { get; set; }
        public ushort BackgroundStyleId { get; set; }
        public uint AccentColor { get; set; }
        public int[] ComplicationIds { get; set; } = Array.Empty<int>();
        public byte FontFaceId { get; set; }
    }

    /// <summary>Wire-compatible FlatBuffer table and KVTX framing. Rejects malformed input.</summary>
    public static class WatchFaceCodec
    {
        public const uint ConfigurationKey = 1064434511;
        public static byte[] Encode(WatchFaceConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            var ids = configuration.ComplicationIds ?? throw new ArgumentException("ComplicationIds cannot be null.");
            if (ids.Length > 1000) throw new ArgumentOutOfRangeException(nameof(configuration));
            var data = new byte[checked(44 + ids.Length * 4)];
            Put32(data, 0, 20); Put16(data, 4, 16); Put16(data, 6, 20);
            ushort[] offsets = { 4, 6, 8, 12, 16, 10 };
            for (int i = 0; i < offsets.Length; i++) Put16(data, 8 + i * 2, offsets[i]);
            Put32(data, 20, 16);
            Put16(data, 24, configuration.TimeStyleId); Put16(data, 26, configuration.ComplicationLayoutId);
            Put16(data, 28, configuration.BackgroundStyleId); data[30] = configuration.FontFaceId;
            Put32(data, 32, configuration.AccentColor); Put32(data, 36, 4); Put32(data, 40, (uint)ids.Length);
            for (int i = 0; i < ids.Length; i++) Put32(data, 44 + i * 4, unchecked((uint)ids[i]));
            return data;
        }
        public static WatchFaceConfiguration Decode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            int table = checked((int)U32(data, 0));
            int vtable = checked(table - unchecked((int)U32(data, table)));
            int vtableSize = U16(data, vtable), objectSize = U16(data, vtable + 2);
            if (vtableSize < 4 || (vtableSize & 1) != 0 || objectSize < 4) throw Invalid();
            Bounds(data, vtable, vtableSize); Bounds(data, table, objectSize);
            int Field(int index, int size)
            {
                if (4 + index * 2 >= vtableSize) return 0;
                int offset = U16(data, vtable + 4 + index * 2);
                if (offset == 0) return 0;
                if (offset < 4 || offset > objectSize - size) throw Invalid();
                return checked(table + offset);
            }
            int time = Field(0, 2), layout = Field(1, 2), background = Field(2, 2), color = Field(3, 4), vectorRef = Field(4, 4), font = Field(5, 1);
            var result = new WatchFaceConfiguration
            {
                TimeStyleId = time == 0 ? (ushort)0 : U16(data, time),
                ComplicationLayoutId = layout == 0 ? (ushort)0 : U16(data, layout),
                BackgroundStyleId = background == 0 ? (ushort)0 : U16(data, background),
                AccentColor = color == 0 ? 0 : U32(data, color), FontFaceId = font == 0 ? (byte)0 : data[font]
            };
            if (vectorRef != 0)
            {
                int vector = checked(vectorRef + (int)U32(data, vectorRef));
                uint count = U32(data, vector);
                if (count > 1000) throw Invalid();
                Bounds(data, vector + 4, checked((int)count * 4));
                result.ComplicationIds = new int[count];
                for (int i = 0; i < count; i++) result.ComplicationIds[i] = unchecked((int)U32(data, vector + 4 + i * 4));
            }
            return result;
        }
        public static byte[] WriteAndCommit(uint key, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var script = new byte[checked(data.Length + 10)];
            Put32(script, 1, key); Put32(script, 5, (uint)data.Length); data.CopyTo(script, 9); script[script.Length - 1] = 5;
            return script;
        }
        public static byte[]? ExtractValue(byte[] script, uint key)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            var values = new Dictionary<uint, byte[]>(); int at = 0;
            while (at < script.Length)
            {
                byte command = script[at++]; if (command == 5) continue;
                uint target = U32(script, at); at += 4;
                bool indexed = command >= 6 && command <= 8, applicable = true;
                if (indexed) { Bounds(script, at, 1); int size = script[at++]; Bounds(script, at, size); at += size; applicable = size == 0; }
                if (command == 0 || command == 1 || command == 6 || command == 7)
                {
                    int size = checked((int)U32(script, at)); at += 4; Bounds(script, at, size);
                    if (applicable)
                    {
                        bool append = command == 1 || command == 7;
                        byte[] prior = append && values.TryGetValue(target, out var found) ? found : Array.Empty<byte>();
                        var value = new byte[checked(prior.Length + size)]; prior.CopyTo(value, 0); Array.Copy(script, at, value, prior.Length, size); values[target] = value;
                    }
                    at += size;
                }
                else if (command == 2 || command == 8) { if (applicable) values.Remove(target); }
                else if (command == 3 || command == 4)
                {
                    uint other = U32(script, at); at += 4;
                    // Both operands are keys. Their direction is not documented by the SDK.
                    if (target == key || other == key)
                        throw new PolarProtocolException("KVTX COPY/MOVE involving the requested key is not supported by the public watchface codec.");
                }
                else throw new PolarProtocolException("Unknown KVTX command: " + command);
            }
            return values.TryGetValue(key, out var result) ? result : null;
        }
        private static void Bounds(byte[] data, int offset, int length)
        { if (offset < 0 || length < 0 || offset > data.Length - length) throw Invalid(); }
        private static PolarProtocolException Invalid() => new PolarProtocolException("Malformed watchface FlatBuffer/KVTX payload.");
        private static ushort U16(byte[] data, int offset) { Bounds(data, offset, 2); return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)); }
        private static uint U32(byte[] data, int offset) { Bounds(data, offset, 4); return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)); }
        private static void Put16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
        private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    }
}
