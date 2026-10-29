// Ported from Polar BLE SDK. Copyright Polar Electro Oy. See LICENSES/Polar_SDK_License.txt.
using System;
using System.Collections.Generic;
using System.Linq;

namespace PolarBle.Protocol.Pmd
{
    public enum PmdMeasurementType : byte
    {
        Ecg = 0, Ppg = 1, Accelerometer = 2, Ppi = 3, Gyroscope = 5, Magnetometer = 6,
        SkinTemperature = 7, SdkMode = 9, Location = 10, Pressure = 11, Temperature = 12,
        OfflineRecording = 13, OfflineHeartRate = 14, DerivedMeasurement = 15, Unknown = 63
    }
    public enum PmdSettingType : byte
    {
        SampleRate = 0, Resolution = 1, Range = 2, RangeMilliunit = 3, Channels = 4,
        Factor = 5, Security = 6, DerivedMeasurementMethod = 7, SourceMeasurementType = 8,
        SourceMeasurementSampleRate = 9, SourceMeasurementRange = 10,
        DerivedMeasurementTimeWindow = 11, DerivedMeasurementSettingsGroupId = 12
    }
    public enum PmdTriggerMode : byte { Disabled = 0, SystemStart = 1, ExerciseStart = 2 }
    [Flags] public enum PmdMeasurementStatus { Inactive = 0, Online = 1, Offline = 2 }

    public sealed class PmdSettings
    {
        public IReadOnlyDictionary<PmdSettingType, int> Selected { get; }
        public IReadOnlyDictionary<PmdSettingType, IReadOnlyList<int>> Available { get; }
        public PmdSettings(IReadOnlyDictionary<PmdSettingType, int>? selected = null)
        {
            var copy = selected == null ? new Dictionary<PmdSettingType, int>() : selected.ToDictionary(x => x.Key, x => x.Value);
            foreach (var value in copy) Validate(value.Key, value.Value);
            Selected = copy;
            Available = new Dictionary<PmdSettingType, IReadOnlyList<int>>();
        }
        private PmdSettings(Dictionary<PmdSettingType, IReadOnlyList<int>> available)
        { Available = available; Selected = new Dictionary<PmdSettingType, int>(); }
        public static PmdSettings Parse(byte[] bytes)
        {
            var result = new Dictionary<PmdSettingType, IReadOnlyList<int>>();
            int offset = 0;
            while (offset < bytes.Length)
            {
                PmdBinary.Require(bytes, offset, 2);
                var type = (PmdSettingType)bytes[offset++];
                int count = bytes[offset++], width = Width(type);
                if (type == PmdSettingType.Security) throw new PolarProtocolException("Security material is not an integer PMD setting.");
                var values = new List<int>(count);
                for (int i = 0; i < count; i++, offset += width)
                    values.Add(unchecked((int)PmdBinary.Unsigned(bytes, offset, width)));
                result[type] = values;
            }
            return new PmdSettings(result);
        }
        public PmdSettings SelectMaximum()
        {
            var result = new Dictionary<PmdSettingType, int>();
            foreach (var pair in Available)
            {
                if (pair.Value.Count == 0) continue;
                result[pair.Key] = pair.Key == PmdSettingType.DerivedMeasurementMethod
                    ? pair.Value.Aggregate(0, (bits, id) => bits | (1 << id)) : pair.Value.Max();
            }
            return new PmdSettings(result);
        }
        public int Get(PmdSettingType key, int fallback = 0)
        {
            if (Selected.TryGetValue(key, out int selected)) return selected;
            return Available.TryGetValue(key, out var values) && values.Count > 0 ? values[0] : fallback;
        }
        public PmdSettings WithResponse(PmdSettings response)
        {
            var result = Selected.ToDictionary(x => x.Key, x => x.Value);
            foreach (var item in response.Available)
                if (item.Value.Count > 0) result[item.Key] = item.Key == PmdSettingType.DerivedMeasurementMethod
                    ? item.Value.Aggregate(0, (bits, id) => bits | (1 << id)) : item.Value[0];
            return new PmdSettings(result);
        }
        public byte[] Serialize()
        {
            var bytes = new List<byte>();
            foreach (var pair in Selected.OrderBy(x => x.Key))
            {
                if (pair.Key == PmdSettingType.Factor || pair.Key == PmdSettingType.SourceMeasurementRange) continue;
                if (pair.Key == PmdSettingType.DerivedMeasurementMethod)
                {
                    var ids = Enumerable.Range(0, 16).Where(i => (pair.Value & (1 << i)) != 0).ToArray();
                    if (ids.Length == 0) continue;
                    bytes.Add((byte)pair.Key); bytes.Add((byte)ids.Length);
                    foreach (int id in ids) bytes.Add((byte)id);
                }
                else
                {
                    bytes.Add((byte)pair.Key); bytes.Add(1);
                    for (int i = 0; i < Width(pair.Key); i++) bytes.Add((byte)(pair.Value >> (i * 8)));
                }
            }
            return bytes.ToArray();
        }
        private static void Validate(PmdSettingType type, int value)
        {
            int width = Width(type);
            if (type == PmdSettingType.Security) throw new ArgumentException("Use PmdSecret for security settings.");
            if (type == PmdSettingType.DerivedMeasurementMethod)
            { if (value < 0 || value > 65535) throw new ArgumentOutOfRangeException(nameof(value)); return; }
            if ((width == 1 && (value < 0 || value > 255)) || (width == 2 && (value < 0 || value > 65535)))
                throw new ArgumentOutOfRangeException(nameof(value));
        }
        private static int Width(PmdSettingType type)
        {
            switch (type)
            {
                case PmdSettingType.SampleRate: case PmdSettingType.Resolution: case PmdSettingType.Range:
                case PmdSettingType.SourceMeasurementSampleRate: return 2;
                case PmdSettingType.RangeMilliunit: case PmdSettingType.Factor: case PmdSettingType.SourceMeasurementRange:
                case PmdSettingType.DerivedMeasurementTimeWindow: return 4;
                case PmdSettingType.Channels: case PmdSettingType.DerivedMeasurementMethod:
                case PmdSettingType.SourceMeasurementType: case PmdSettingType.DerivedMeasurementSettingsGroupId: return 1;
                case PmdSettingType.Security: return 16;
                default: throw new PolarProtocolException("Unknown PMD setting: " + (byte)type);
            }
        }
    }
    public sealed class PmdSample
    {
        /// <summary>Device timestamp, nanoseconds since Polar epoch (2000-01-01). Zero means unavailable.</summary>
        public ulong TimestampNanoseconds { get; }
        public IReadOnlyList<double> Values { get; }
        /// <summary>Additional exact fields, e.g. status words, ulong sport ID, NMEA text or derived method arrays.</summary>
        public IReadOnlyDictionary<string, object> Metadata { get; }
        public PmdSample(ulong timestamp, IReadOnlyList<double> values, IReadOnlyDictionary<string, object>? metadata = null)
        { TimestampNanoseconds = timestamp; Values = values; Metadata = metadata ?? new Dictionary<string, object>(); }
    }
    public sealed class PmdDataBatch : EventArgs
    {
        public PmdMeasurementType Type { get; }
        public byte FrameType { get; }
        public bool Compressed { get; }
        public ulong FrameTimestampNanoseconds { get; }
        public IReadOnlyList<string> Channels { get; }
        public IReadOnlyList<PmdSample> Samples { get; }
        public byte[] RawFrame { get; }
        internal PmdDataBatch(PmdMeasurementType type, byte frameType, bool compressed, ulong timestamp,
            IReadOnlyList<string> channels, IReadOnlyList<PmdSample> samples, byte[] raw)
        { Type = type; FrameType = frameType; Compressed = compressed; FrameTimestampNanoseconds = timestamp;
          Channels = channels; Samples = samples; RawFrame = (byte[])raw.Clone(); }
    }
    public sealed class PmdErrorEventArgs : EventArgs
    { public Exception Error { get; } public PmdErrorEventArgs(Exception error) { Error = error; } }
    public sealed class PmdControlPointException : PolarException
    {
        public byte OperationCode { get; } public byte StatusCode { get; }
        public PmdControlPointException(byte operation, byte status)
            : base("PMD operation " + operation + " failed with device status " + status)
        { OperationCode = operation; StatusCode = status; }
    }
    public sealed class PmdOfflineTrigger
    {
        public PmdTriggerMode Mode { get; }
        /// <summary>A null value denotes a disabled measurement trigger.</summary>
        public IReadOnlyDictionary<PmdMeasurementType, PmdSettings?> Triggers { get; }
        public PmdOfflineTrigger(PmdTriggerMode mode, IReadOnlyDictionary<PmdMeasurementType, PmdSettings?> triggers)
        { Mode = mode; Triggers = triggers; }
        public static PmdOfflineTrigger Parse(byte[] bytes)
        {
            PmdBinary.Require(bytes, 0, 1);
            if (bytes[0] > 2) throw new PolarProtocolException("Unknown offline trigger mode.");
            var result = new Dictionary<PmdMeasurementType, PmdSettings?>(); int offset = 1;
            while (offset < bytes.Length)
            {
                PmdBinary.Require(bytes, offset, 2); byte status = bytes[offset++]; var type = (PmdMeasurementType)(bytes[offset++] & 63);
                if (status > 1) throw new PolarProtocolException("Unknown offline trigger status.");
                if (status == 0) result[type] = null;
                else { PmdBinary.Require(bytes, offset, 1); int count = bytes[offset++]; result[type] = PmdSettings.Parse(PmdBinary.Slice(bytes, offset, count)); offset += count; }
            }
            return new PmdOfflineTrigger((PmdTriggerMode)bytes[0], result);
        }
    }
    internal static class PmdBinary
    {
        public static void Require(byte[] data, int offset, int count)
        { if (offset < 0 || count < 0 || offset > data.Length - count) throw new PolarProtocolException("Truncated PMD packet."); }
        public static byte[] Slice(byte[] data, int offset, int count)
        { Require(data, offset, count); var result = new byte[count]; Array.Copy(data, offset, result, 0, count); return result; }
        public static ulong Unsigned(byte[] data, int offset, int count)
        { Require(data, offset, count); if (count > 8) throw new ArgumentOutOfRangeException(nameof(count)); ulong value = 0; for (int i = 0; i < count; i++) value |= (ulong)data[offset + i] << (8 * i); return value; }
        public static int Signed(byte[] data, int offset, int count)
        { if (count < 1 || count > 4) throw new ArgumentOutOfRangeException(nameof(count)); int value = unchecked((int)Unsigned(data, offset, count)); return count == 4 ? value : value << (32 - count * 8) >> (32 - count * 8); }
        public static float Float(int bits) => BitConverter.Int32BitsToSingle(bits);
        public static float Float(byte[] data, int offset) => Float(unchecked((int)Unsigned(data, offset, 4)));
        public static double Double(byte[] data, int offset) => BitConverter.Int64BitsToDouble(unchecked((long)Unsigned(data, offset, 8)));
    }
}
