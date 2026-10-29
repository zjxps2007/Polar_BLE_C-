// Ported from Polar BLE SDK BleBattClient / BleBasClient. See LICENSES/Polar_SDK_License.txt.
using System;
using System.Collections.Generic;

namespace PolarBle.Protocol.Battery
{
    public enum BatteryChargeState { Unknown = 0, Charging = 1, DischargingActive = 2, DischargingInactive = 3 }
    public enum BatteryPresentState { NotPresent = 0, Present = 1, Unknown = 2 }
    public enum PowerSourceState { NotConnected = 0, Connected = 1, Unknown = 2, ReservedForFutureUse = 3 }

    public sealed class BatteryStatusData : EventArgs
    {
        public BatteryChargeState ChargeState { get; }
        public BatteryPresentState BatteryPresent { get; }
        public PowerSourceState WiredExternalPowerConnected { get; }
        public PowerSourceState WirelessExternalPowerConnected { get; }
        public byte Flags { get; }
        public byte PowerStateLowByte { get; }
        /// <summary>The complete BAS value, including optional fields not interpreted by the upstream SDK.</summary>
        public IReadOnlyList<byte> RawData { get; }
        internal BatteryStatusData(byte[] bytes)
        {
            Flags = bytes[0]; PowerStateLowByte = bytes[1];
            ChargeState = (BatteryChargeState)((bytes[1] >> 5) & 3);
            BatteryPresent = (BatteryPresentState)(bytes[1] & 1);
            WiredExternalPowerConnected = (PowerSourceState)((bytes[1] >> 1) & 3);
            WirelessExternalPowerConnected = (PowerSourceState)((bytes[1] >> 3) & 3);
            RawData = Array.AsReadOnly((byte[])bytes.Clone());
        }
    }

    public static class BatteryStatusParser
    {
        public static readonly Guid ServiceUuid = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
        public static readonly Guid LevelUuid = new Guid("00002a19-0000-1000-8000-00805f9b34fb");
        public static readonly Guid StatusUuid = new Guid("00002bed-0000-1000-8000-00805f9b34fb");
        public static byte ParseLevel(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length != 1 || bytes[0] > 100) throw new PolarProtocolException("Invalid battery level.");
            return bytes[0];
        }
        public static BatteryStatusData Parse(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            // Both Android/iOS SDKs decode these two bytes, including their two-byte golden fixtures.
            if (bytes.Length < 2) throw new PolarProtocolException("Battery Level Status requires at least flags and power-state bytes.");
            return new BatteryStatusData(bytes);
        }
    }
}
