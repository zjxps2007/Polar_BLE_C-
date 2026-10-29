using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PolarBle;
using PolarBle.Protocol.Battery;
using PolarBle.Protocol.HeartRate;
using PolarBle.Transport;

namespace Polar.Tests
{
    internal static class BatteryAdvertisingTests
    {
        // Exact manufacturer fixtures from upstream BleAdvertisementContentTest (company ID is excluded by IBleTransport).
        private static readonly byte[] GpbOnly = { 0x72, 0x08, 0x97, 0xc9, 0xc3, 0, 0, 0, 0, 0 };
        private static readonly byte[] GpbAndHr = { 0x72, 0x08, 0x97, 0xc9, 0xc3, 0, 0, 0, 0, 0, 0x7a, 0x01, 0x03, 0x33, 0, 0 };
        public static void Run()
        { Battery(); Manufacturer(); Session().GetAwaiter().GetResult(); BroadcastEvents(); }
        private static void Battery()
        {
            var charging = BatteryStatusParser.Parse(new byte[] { 0, 0xa3 });
            Assert.Equal(BatteryChargeState.Charging, charging.ChargeState);
            Assert.Equal(BatteryPresentState.Present, charging.BatteryPresent);
            Assert.Equal(PowerSourceState.Connected, charging.WiredExternalPowerConnected);
            Assert.Equal(PowerSourceState.NotConnected, charging.WirelessExternalPowerConnected);
            Assert.Equal(BatteryChargeState.DischargingActive, BatteryStatusParser.Parse(new byte[] { 0, 0xc1 }).ChargeState);
            Assert.Equal(BatteryChargeState.DischargingInactive, BatteryStatusParser.Parse(new byte[] { 0, 0xe3 }).ChargeState);
            for (int state = 0; state < 4; state++)
            {
                Assert.Equal((PowerSourceState)state, BatteryStatusParser.Parse(new byte[] { 0, (byte)(state << 1) }).WiredExternalPowerConnected);
                Assert.Equal((PowerSourceState)state, BatteryStatusParser.Parse(new byte[] { 0, (byte)(state << 3) }).WirelessExternalPowerConnected);
            }
            var absent = BatteryStatusParser.Parse(new byte[] { 0, 0 });
            Assert.Equal(BatteryPresentState.NotPresent, absent.BatteryPresent); Assert.Equal(BatteryChargeState.Unknown, absent.ChargeState);
            byte[] raw = { 7, 0x2b, 0x10, 0x90 }; var optional = BatteryStatusParser.Parse(raw); raw[3] = 0;
            Assert.Equal((byte)7, optional.Flags); Assert.Equal((byte)0x90, optional.RawData[3]);
            Assert.Equal((byte)100, BatteryStatusParser.ParseLevel(new byte[] { 100 }));
            Assert.Throws<PolarProtocolException>(() => BatteryStatusParser.Parse(Array.Empty<byte>()));
            Assert.Throws<PolarProtocolException>(() => BatteryStatusParser.Parse(new byte[] { 0 }));
            Assert.Throws<PolarProtocolException>(() => BatteryStatusParser.ParseLevel(new byte[] { 255 }));
        }
        private static void Manufacturer()
        {
            var legacy = PolarHeartRateAdvertisement.ParseManufacturerPayload(new byte[] { 0x2b, 0x0b, 0xb6, 0xac })!;
            Assert.Equal(182, legacy.FastAverageHeartRate); Assert.Equal(172, legacy.SlowAverageHeartRate);
            Assert.Equal(2, legacy.FrameCounter); Assert.True(legacy.BatteryOk); Assert.True(legacy.SensorContact);
            Assert.True(PolarHeartRateAdvertisement.ParseManufacturerPayload(GpbOnly) == null);
            Assert.True(PolarHeartRateAdvertisement.ParseManufacturerPayload(Array.Empty<byte>()) == null);
            Assert.Equal(0, PolarHeartRateAdvertisement.ParseManufacturerPayload(GpbAndHr)!.SlowAverageHeartRate);
            // OH1 example from the iOS SDK: one GPB followed by a three-byte HR record.
            Assert.Equal(60, PolarHeartRateAdvertisement.ParseManufacturerPayload(new byte[] { 0x72, 0x06, 0x7f, 0x44, 0x37, 0, 0, 0, 0x33, 0, 0x3c })!.SlowAverageHeartRate);
            Assert.True(PolarHeartRateAdvertisement.ParseManufacturerData(new Dictionary<ushort, byte[]> { [0x016b] = GpbAndHr }) == null);
            Assert.Throws<PolarProtocolException>(() => PolarHeartRateAdvertisement.ParseManufacturerPayload(new byte[] { 0x72 }));
            Assert.Throws<PolarProtocolException>(() => PolarHeartRateAdvertisement.ParseManufacturerPayload(new byte[] { 0x72, 3, 0 }));
            Assert.Throws<PolarProtocolException>(() => PolarHeartRateAdvertisement.ParseManufacturerPayload(new byte[] { 0x33, 0 }));
        }
        private static async Task Session()
        {
            using (var connection = new BatteryConnection())
            using (var session = new PolarDeviceSession(connection))
            {
                int statuses = 0, levels = 0, errors = 0;
                session.BatteryStatusReceived += (_, __) => statuses++; session.BatteryLevelReceived += (_, __) => levels++;
                session.ErrorOccurred += (_, __) => errors++;
                await session.InitializeAsync(PolarServices.Battery);
                Assert.True(session.ReadyServices.HasFlag(PolarServices.Battery)); Assert.Equal((byte)80, session.BatteryLevel!.Value);
                Assert.Equal(BatteryChargeState.Charging, session.BatteryStatus!.ChargeState); Assert.Equal(1, statuses); Assert.Equal(1, levels);
                Assert.True(connection.Subscriptions.Contains(BatteryStatusParser.StatusUuid)); Assert.True(connection.Subscriptions.Contains(BatteryStatusParser.LevelUuid));
                await session.InitializeAsync(PolarServices.Battery); Assert.Equal(2, connection.Reads);
                connection.Emit(BatteryStatusParser.StatusUuid, new byte[] { 0, 0xe3 });
                Assert.Equal(BatteryChargeState.DischargingInactive, session.BatteryStatus!.ChargeState); Assert.Equal(2, statuses);
                connection.Emit(BatteryStatusParser.StatusUuid, new byte[] { 0 });
                Assert.Equal(1, errors); Assert.Equal(2, statuses); Assert.Equal(BatteryChargeState.DischargingInactive, session.BatteryStatus!.ChargeState);
                await connection.DisconnectAsync(); Assert.True(session.BatteryStatus == null); Assert.True(session.BatteryLevel == null);
            }
            using (var connection = new BatteryConnection { StatusOnly = true })
            using (var session = new PolarDeviceSession(connection))
            {
                await session.InitializeAsync(PolarServices.Battery);
                Assert.True(session.ReadyServices.HasFlag(PolarServices.Battery)); Assert.True(session.BatteryLevel == null); Assert.True(session.BatteryStatus != null);
            }
            using (var connection = new BatteryConnection { Status = new byte[] { 0 } })
            using (var session = new PolarDeviceSession(connection))
            {
                await Assert.ThrowsAsync<PolarProtocolException>(() => session.InitializeAsync(PolarServices.Battery));
                Assert.False(session.ReadyServices.HasFlag(PolarServices.Battery)); Assert.Equal(0, connection.Subscriptions.Count);
            }
        }
        private static void BroadcastEvents()
        {
            using var transport = new AdvertisingTransport(); using var client = new PolarClient(transport) { AutomaticReconnection = true };
            var broadcasts = new List<PolarHrBroadcastData>(); int discovered = 0, errors = 0;
            client.HrBroadcastReceived += (_, data) => broadcasts.Add(data); client.DeviceDiscovered += (_, __) => discovered++;
            client.ErrorOccurred += (_, __) => errors++;
            byte[] frame = { 0x2b, 0x0b, 0xb6, 0xac };
            transport.Emit("one", frame); transport.Emit("one", frame); Assert.Equal(1, broadcasts.Count); Assert.Equal(2, discovered);
            transport.Emit("two", frame); Assert.Equal(2, broadcasts.Count);
            transport.Emit("one", GpbOnly); Assert.Equal(2, broadcasts.Count);
            transport.Emit("one", frame); Assert.Equal(3, broadcasts.Count); // GPB-only packet clears stale HR presence.
            transport.Emit("one", new byte[] { 0x2f, 0, 80 }); Assert.Equal(4, broadcasts.Count);
            transport.Emit("one", new byte[] { 0x72 }); Assert.Equal(1, errors); Assert.Equal(4, broadcasts.Count);
            Assert.Equal(172, broadcasts[0].HeartRate); Assert.True(broadcasts[0].BatteryOk); Assert.Equal("one", broadcasts[0].Device.Id);
            transport.Emit("other", frame, 0x016b); Assert.Equal(4, broadcasts.Count);
            Assert.True(client.AutomaticReconnection);
        }
        private sealed class BatteryConnection : IBleConnection
        {
            public bool StatusOnly; public byte[] Status = { 0, 0xa3 }; public int Reads;
            public HashSet<Guid> Subscriptions { get; } = new HashSet<Guid>();
            public string DeviceId => "test-battery"; public bool IsConnected { get; private set; } = true; public int MaxWriteSize => 20;
            public event EventHandler<BleNotification>? NotificationReceived; public event EventHandler? Disconnected;
            public event EventHandler<BleTransportError>? ErrorOccurred { add { } remove { } }
            public Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(CancellationToken cancellationToken = default)
            {
                var characteristics = new List<BleGattCharacteristic>();
                if (!StatusOnly) characteristics.Add(new BleGattCharacteristic(BatteryStatusParser.LevelUuid, BleCharacteristicProperties.Read | BleCharacteristicProperties.Notify));
                characteristics.Add(new BleGattCharacteristic(BatteryStatusParser.StatusUuid, BleCharacteristicProperties.Read | BleCharacteristicProperties.Indicate));
                return Task.FromResult<IReadOnlyList<BleGattService>>(new[] { new BleGattService(BatteryStatusParser.ServiceUuid, characteristics) });
            }
            public Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default)
            { Reads++; return Task.FromResult(characteristicUuid == BatteryStatusParser.StatusUuid ? Status : new byte[] { 80 }); }
            public Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withResponse, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task SetNotificationsAsync(Guid serviceUuid, Guid characteristicUuid, bool enabled, CancellationToken cancellationToken = default)
            { if (enabled) Subscriptions.Add(characteristicUuid); else Subscriptions.Remove(characteristicUuid); return Task.CompletedTask; }
            public void Emit(Guid characteristic, byte[] data) => NotificationReceived?.Invoke(this, new BleNotification(BatteryStatusParser.ServiceUuid, characteristic, data));
            public Task DisconnectAsync(CancellationToken cancellationToken = default) { IsConnected = false; Disconnected?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
            public void Dispose() { }
        }
        private sealed class AdvertisingTransport : IBleTransport
        {
            public event EventHandler<BleDevice>? DeviceDiscovered;
            public event EventHandler<BleTransportError>? ErrorOccurred { add { } remove { } }
            public void Emit(string id, byte[] payload, ushort company = 0x006b)
                => DeviceDiscovered?.Invoke(this, new BleDevice(id, "", 0, -50, new Dictionary<ushort, byte[]> { [company] = payload }));
            public Task StartScanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopScanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<IBleConnection> ConnectAsync(string deviceId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Advertisement tests must not connect.");
            public void Dispose() { }
        }
    }
}
