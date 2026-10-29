// Ported from Polar BLE SDK. Copyright Polar Electro Oy. See LICENSES/Polar_SDK_License.txt.
using System;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Protocol.Pmd;
using PolarBle.Transport;

namespace PolarBle.Protocol.Features
{
    public sealed class TemperatureMeasurement : EventArgs
    {
        public double Celsius { get; } public double Fahrenheit { get; }
        /// <summary>Device-local time from optional Bluetooth Date Time field; timezone is unspecified.</summary>
        public DateTime? Timestamp { get; } public byte? TemperatureType { get; }
        public string? SpecialValue { get; }
        internal TemperatureMeasurement(double celsius, double fahrenheit, DateTime? timestamp, byte? type, string? special)
        { Celsius = celsius; Fahrenheit = fahrenheit; Timestamp = timestamp; TemperatureType = type; SpecialValue = special; }
    }
    public sealed class HealthThermometerClient : IDisposable
    {
        public static readonly Guid ServiceUuid = new Guid("00001809-0000-1000-8000-00805f9b34fb");
        public static readonly Guid MeasurementUuid = new Guid("00002a1c-0000-1000-8000-00805f9b34fb");
        public static readonly Guid TemperatureTypeUuid = new Guid("00002a1d-0000-1000-8000-00805f9b34fb");
        private readonly IBleConnection _connection; private bool _disposed;
        public event EventHandler<TemperatureMeasurement>? MeasurementReceived;
        public event EventHandler<PmdErrorEventArgs>? ErrorOccurred;
        public HealthThermometerClient(IBleConnection connection)
        { _connection = connection; connection.NotificationReceived += OnNotification; }
        public Task StartAsync(CancellationToken cancellationToken = default)
        { Check(); return _connection.SetNotificationsAsync(ServiceUuid, MeasurementUuid, true, cancellationToken); }
        public Task StopAsync(CancellationToken cancellationToken = default)
        { Check(); return _connection.SetNotificationsAsync(ServiceUuid, MeasurementUuid, false, cancellationToken); }
        public async Task<byte> ReadTemperatureTypeAsync(CancellationToken cancellationToken = default)
        { Check(); var bytes = await _connection.ReadAsync(ServiceUuid, TemperatureTypeUuid, cancellationToken).ConfigureAwait(false); PmdBinary.Require(bytes, 0, 1); return bytes[0]; }
        public static TemperatureMeasurement Parse(byte[] data)
        {
            PmdBinary.Require(data, 0, 5); int flags = data[0]; uint raw = (uint)PmdBinary.Unsigned(data, 1, 3);
            double temperature; string? special = null;
            switch (raw)
            {
                case 0x7ffffe: temperature = double.PositiveInfinity; special = "PositiveInfinity"; break;
                case 0x800002: temperature = double.NegativeInfinity; special = "NegativeInfinity"; break;
                case 0x7fffff: temperature = double.NaN; special = "NaN"; break;
                case 0x800000: temperature = double.NaN; special = "NotAtThisResolution"; break;
                case 0x800001: temperature = double.NaN; special = "Reserved"; break;
                default: temperature = Math.Round(PmdBinary.Signed(data, 1, 3) * Math.Pow(10, (sbyte)data[4]), 2, MidpointRounding.AwayFromZero); break;
            }
            int offset = 5; DateTime? timestamp = null; byte? type = null;
            if ((flags & 2) != 0)
            {
                PmdBinary.Require(data, offset, 7); int year = (int)PmdBinary.Unsigned(data, offset, 2);
                if (year != 0 && data[offset + 2] != 0 && data[offset + 3] != 0)
                {
                    try { timestamp = new DateTime(year, data[offset + 2], data[offset + 3], data[offset + 4], data[offset + 5], data[offset + 6], DateTimeKind.Unspecified); }
                    catch (ArgumentOutOfRangeException) { throw new PolarProtocolException("Invalid thermometer Date Time."); }
                }
                offset += 7;
            }
            if ((flags & 4) != 0) { PmdBinary.Require(data, offset, 1); type = data[offset++]; }
            if (offset != data.Length) throw new PolarProtocolException("Unexpected thermometer trailing data.");
            bool fahrenheit = (flags & 1) != 0;
            return new TemperatureMeasurement(fahrenheit ? (temperature - 32) * 5 / 9 : temperature,
                fahrenheit ? temperature : temperature * 9 / 5 + 32, timestamp, type, special);
        }
        private void OnNotification(object? sender, BleNotification args)
        {
            if (_disposed || args.ServiceUuid != ServiceUuid || args.CharacteristicUuid != MeasurementUuid) return;
            try { MeasurementReceived?.Invoke(this, Parse(args.Data)); }
            catch (Exception error) { ErrorOccurred?.Invoke(this, new PmdErrorEventArgs(error)); }
        }
        private void Check()
        { if (_disposed) throw new ObjectDisposedException(nameof(HealthThermometerClient)); if (!_connection.IsConnected) throw new PolarDisconnectedException(_connection.DeviceId); }
        public void Dispose() { if (_disposed) return; _disposed = true; _connection.NotificationReceived -= OnNotification; }
    }
}
