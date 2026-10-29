using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PolarBle;
using PolarBle.Protocol.Features;
using PolarBle.Protocol.Telemetry;
using PolarBle.Transport;

namespace Polar.Tests
{
    internal static class ProtocolServiceTests
    {
        public static void Run() { Thermometer(); FeatureControl().GetAwaiter().GetResult(); Telemetry().GetAwaiter().GetResult(); }
        private static void Thermometer()
        {
            var celsius = HealthThermometerClient.Parse(new byte[] { 0, 0x44, 0x0E, 0, 254 }); // 3652 * 10^-2
            Assert.Equal(36.52, celsius.Celsius);
            Assert.True(Math.Abs(celsius.Fahrenheit - 97.736) < 0.0001);
            var negative = HealthThermometerClient.Parse(new byte[] { 0, 0x2E, 0xFB, 0xFF, 254 }); // signed -1234
            Assert.Equal(-12.34, negative.Celsius);
            var fahrenheit = HealthThermometerClient.Parse(new byte[] { 1, 0xDA, 3, 0, 255 });
            Assert.True(Math.Abs(fahrenheit.Celsius - 37) < 0.0001);
            var date = HealthThermometerClient.Parse(new byte[] { 6, 37, 0, 0, 0, 0xEA, 7, 10, 29, 6, 30, 0, 2 });
            Assert.Equal(new DateTime(2026, 10, 29, 6, 30, 0), date.Timestamp!.Value);
            Assert.Equal((byte)2, date.TemperatureType!.Value);
            Assert.True(double.IsNaN(HealthThermometerClient.Parse(new byte[] { 0, 255, 255, 127, 0 }).Celsius));
            Assert.Throws<PolarProtocolException>(() => HealthThermometerClient.Parse(new byte[] { 2, 1, 0, 0, 0 }));
            Assert.Throws<PolarProtocolException>(() => HealthThermometerClient.Parse(new byte[] { 0, 1, 0, 0, 0, 1 }));
        }
        private static async Task FeatureControl()
        {
            using var connection = new TestConnection(); using var client = new PolarFeatureClient(connection);
            connection.Reads[PolarFeatureClient.FeaturesUuid] = new byte[] { 0xD7, 0x0B };
            var flags = await client.ReadFeaturesAsync(); Assert.True(flags.HasFlag(PolarFeatureFlags.MultiConnection)); Assert.True(flags.HasFlag(PolarFeatureFlags.SensorInitiatedSecurity));
            await Assert.ThrowsAsync<PolarFeatureNotAvailableException>(() => client.GetMultiConnectionAsync());
            await client.InitializeAsync();
            connection.OnWrite = (characteristic, bytes) =>
            { Assert.Equal(PolarFeatureClient.ControlPointUuid, characteristic); Assert.SequenceEqual(new byte[] { 8, 1 }, bytes); connection.Emit(PolarFeatureClient.ServiceUuid, characteristic, new byte[] { 240, 8, 1 }); };
            await client.SetMultiConnectionAsync(true);
            connection.OnWrite = (characteristic, bytes) =>
            { Assert.SequenceEqual(new byte[] { 9 }, bytes); connection.Emit(PolarFeatureClient.ServiceUuid, characteristic, new byte[] { 240, 9, 1, 1 }); };
            Assert.True(await client.GetMultiConnectionAsync());
            connection.OnWrite = (characteristic, bytes) => connection.Emit(PolarFeatureClient.ServiceUuid, characteristic, new byte[] { 240, bytes[0], 2 });
            Assert.Equal((byte)2, (await Assert.ThrowsAsync<PolarFeatureCommandException>(() => client.SetSensorInitiatedSecurityAsync(false))).Status);
            connection.OnWrite = (characteristic, bytes) => connection.Emit(PolarFeatureClient.ServiceUuid, characteristic, new byte[] { 240 });
            await Assert.ThrowsAsync<PolarProtocolException>(() => client.GetSensorInitiatedSecurityAsync());
            await Assert.ThrowsAsync<PolarProtocolException>(() => client.GetMultiConnectionAsync());
            using var fresh = new TestConnection(); using var interrupted = new PolarFeatureClient(fresh);
            await interrupted.InitializeAsync(); fresh.OnWrite = (_, _) => fresh.Disconnect();
            await Assert.ThrowsAsync<PolarDisconnectedException>(() => interrupted.SendCommandAsync(PolarFeatureCommand.GetSecurityMode));
        }
        private static async Task Telemetry()
        {
            using var connection = new TestConnection(); using var client = new MemfaultClient(connection);
            connection.Reads[MemfaultClient.FeaturesUuid] = new byte[] { 1, 0, 0, 0 };
            connection.Reads[MemfaultClient.DeviceIdentifierUuid] = Encoding.UTF8.GetBytes("test-sensor");
            connection.Reads[MemfaultClient.DataUriUuid] = Encoding.UTF8.GetBytes("https://example.invalid/ingest");
            connection.Reads[MemfaultClient.AuthorizationUuid] = Encoding.UTF8.GetBytes("test-only-header");
            var config = await client.ReadConfigurationAsync(); Assert.Equal(1u, config.SupportedFeatures); Assert.Equal("test-sensor", config.DeviceIdentifier);
            var chunks = new List<MemfaultChunk>(); int errors = 0;
            client.DataReceived += (_, e) => chunks.Add(e); client.ErrorOccurred += (_, _) => errors++;
            connection.OnWrite = (characteristic, bytes) => { Assert.Equal(MemfaultClient.DataExportUuid, characteristic); Assert.SequenceEqual(new byte[] { 1 }, bytes); };
            await client.StartAsync();
            connection.Emit(MemfaultClient.ServiceUuid, MemfaultClient.DataExportUuid, new byte[] { 31, 5, 6 });
            connection.Emit(MemfaultClient.ServiceUuid, MemfaultClient.DataExportUuid, new byte[] { 31, 5, 6 }); // duplicate
            connection.Emit(MemfaultClient.ServiceUuid, MemfaultClient.DataExportUuid, new byte[] { 0, 7 }); // wrap
            connection.Emit(MemfaultClient.ServiceUuid, MemfaultClient.DataExportUuid, new byte[] { 3, 8 }); // two lost
            Assert.Equal(3, chunks.Count); Assert.Equal(0, chunks[1].DroppedChunks); Assert.Equal(2, chunks[2].DroppedChunks);
            Assert.SequenceEqual(new byte[] { 5, 6 }, chunks[0].Payload);
            connection.Emit(MemfaultClient.ServiceUuid, MemfaultClient.DataExportUuid, Array.Empty<byte>()); Assert.Equal(1, errors);
            connection.OnWrite = (_, bytes) => Assert.SequenceEqual(new byte[] { 0 }, bytes);
            await client.StopAsync(); connection.Emit(MemfaultClient.ServiceUuid, MemfaultClient.DataExportUuid, new byte[] { 4, 9 }); Assert.Equal(3, chunks.Count);
            connection.Reads[MemfaultClient.FeaturesUuid] = Array.Empty<byte>();
            await Assert.ThrowsAsync<PolarProtocolException>(() => client.ReadConfigurationAsync());
            connection.Reads[MemfaultClient.FeaturesUuid] = new byte[] { 1 };
            Assert.Equal(1u, (await client.ReadConfigurationAsync()).SupportedFeatures); // retry after consent/error
        }
        private sealed class TestConnection : IBleConnection
        {
            public string DeviceId => "protocol-test"; public bool IsConnected { get; private set; } = true; public int MaxWriteSize => 232;
            public event EventHandler<BleNotification>? NotificationReceived; public event EventHandler? Disconnected;
            public event EventHandler<BleTransportError>? ErrorOccurred { add { } remove { } }
            public Dictionary<Guid, byte[]> Reads { get; } = new Dictionary<Guid, byte[]>();
            public Action<Guid, byte[]>? OnWrite { get; set; }
            public Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BleGattService>>(Array.Empty<BleGattService>());
            public Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default) => Task.FromResult(Reads[characteristicUuid]);
            public Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withResponse, CancellationToken cancellationToken = default)
            { OnWrite?.Invoke(characteristicUuid, data); return Task.CompletedTask; }
            public Task SetNotificationsAsync(Guid serviceUuid, Guid characteristicUuid, bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task DisconnectAsync(CancellationToken cancellationToken = default) { Disconnect(); return Task.CompletedTask; }
            public void Disconnect() { IsConnected = false; Disconnected?.Invoke(this, EventArgs.Empty); }
            public void Emit(Guid service, Guid characteristic, byte[] bytes) => NotificationReceived?.Invoke(this, new BleNotification(service, characteristic, bytes));
            public void Dispose() { }
        }
    }
}
