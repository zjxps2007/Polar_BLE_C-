// Ported from Polar BLE SDK. Copyright Polar Electro Oy. See LICENSES/Polar_SDK_License.txt.
using System;
using System.Collections.Generic;
using PolarBle.Protocol.Pmd;

namespace PolarBle.Protocol.HeartRate
{
    public sealed class HeartRateData : EventArgs
    {
        public int BeatsPerMinute { get; }
        public bool SensorContact { get; }
        public bool SensorContactSupported { get; }
        public int? EnergyExpendedKilojoules { get; }
        public bool RrPresent { get; }
        public IReadOnlyList<int> RrIntervals1024 { get; }
        public IReadOnlyList<int> RrIntervalsMilliseconds { get; }
        internal HeartRateData(int hr, bool contact, bool supported, int? energy, bool rrPresent, IReadOnlyList<int> rr, IReadOnlyList<int> rrMs)
        { BeatsPerMinute = hr; SensorContact = contact; SensorContactSupported = supported; EnergyExpendedKilojoules = energy; RrPresent = rrPresent; RrIntervals1024 = rr; RrIntervalsMilliseconds = rrMs; }
    }
    public static class HeartRateParser
    {
        public static readonly Guid ServiceUuid = new Guid("0000180d-0000-1000-8000-00805f9b34fb");
        public static readonly Guid MeasurementUuid = new Guid("00002a37-0000-1000-8000-00805f9b34fb");
        public static HeartRateData Parse(byte[] bytes)
        {
            PmdBinary.Require(bytes, 0, 2); byte flags = bytes[0]; int hrWidth = (flags & 1) != 0 ? 2 : 1;
            int hr = (int)PmdBinary.Unsigned(bytes, 1, hrWidth), offset = 1 + hrWidth; int? energy = null;
            if ((flags & 8) != 0) { energy = (int)PmdBinary.Unsigned(bytes, offset, 2); offset += 2; }
            var rr = new List<int>(); var ms = new List<int>();
            if ((flags & 16) != 0)
                while (offset < bytes.Length)
                { int value = (int)PmdBinary.Unsigned(bytes, offset, 2); offset += 2; rr.Add(value); ms.Add((int)Math.Floor(value * 1000.0 / 1024 + 0.5)); }
            if (offset != bytes.Length) throw new PolarProtocolException("Unexpected Heart Rate Measurement trailing bytes.");
            return new HeartRateData(hr, (flags & 6) == 6, (flags & 4) != 0, energy, (flags & 16) != 0, rr, ms);
        }
    }
    public sealed class PolarHeartRateAdvertisement
    {
        public const ushort CompanyId = 0x006b;
        public bool BatteryOk { get; } public bool SensorContact { get; } public int FrameCounter { get; }
        public bool Broadcast { get; } public int SensorDataType { get; } public bool StatusFlag { get; }
        public byte KhzCode { get; } public int FastAverageHeartRate { get; } public int SlowAverageHeartRate { get; }
        private PolarHeartRateAdvertisement(byte[] data)
        {
            BatteryOk = (data[0] & 1) != 0; SensorContact = (data[0] & 2) != 0; FrameCounter = (data[0] >> 2) & 7;
            Broadcast = (data[0] & 32) != 0; SensorDataType = (data[0] >> 6) & 1; StatusFlag = (data[0] & 128) != 0;
            KhzCode = data[1]; FastAverageHeartRate = data[2]; SlowAverageHeartRate = data.Length == 4 ? data[3] : data[2];
        }
        /// <summary>Decode legacy Polar HR payload, after the company ID and any advertising record envelope have been removed.</summary>
        public static PolarHeartRateAdvertisement Parse(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length != 3 && payload.Length != 4) throw new PolarProtocolException("Legacy Polar HR advertising payload must have 3 or 4 bytes.");
            return new PolarHeartRateAdvertisement(payload);
        }
        /// <summary>Decode the transport's company-ID-indexed manufacturer values. Values exclude the two-byte company ID.
        /// Returns null for non-Polar or GPB-only advertisements; malformed records throw.</summary>
        public static PolarHeartRateAdvertisement? ParseManufacturerData(IReadOnlyDictionary<ushort, byte[]> manufacturerData)
        {
            if (manufacturerData == null) throw new ArgumentNullException(nameof(manufacturerData));
            return manufacturerData.TryGetValue(CompanyId, out var payload) ? ParseManufacturerPayload(payload) : null;
        }
        public static PolarHeartRateAdvertisement? ParseManufacturerPayload(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            int offset = 0;
            while (offset < payload.Length)
            {
                if ((payload[offset] & 0x40) != 0)
                {
                    // GPB record: type/flags, one-byte payload length, payload.
                    if (payload.Length - offset < 2) throw new PolarProtocolException("Truncated Polar GPB advertising header.");
                    int length = payload[offset + 1] + 2;
                    if (length > payload.Length - offset) throw new PolarProtocolException("Truncated Polar GPB advertising record.");
                    offset += length;
                }
                else
                {
                    int length = payload.Length - offset;
                    if (length != 3 && length != 4) throw new PolarProtocolException("Unsupported Polar HR advertising record length.");
                    var hr = new byte[length]; Buffer.BlockCopy(payload, offset, hr, 0, length); return Parse(hr);
                }
            }
            return null;
        }
    }
}
