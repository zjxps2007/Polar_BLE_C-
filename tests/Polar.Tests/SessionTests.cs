using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PolarBle;
using PolarBle.Protocol.HeartRate;
using PolarBle.Transport;

namespace Polar.Tests
{
    internal static class SessionTests
    {
        public static void Run() => RunAsync().GetAwaiter().GetResult();
        private static async Task RunAsync()
        {
            using var transport = new SessionTransport(); using var client = new PolarClient(transport);
            var results = await Task.WhenAll(client.ConnectAsync("device", PolarServices.HeartRate), client.ConnectAsync("device", PolarServices.HeartRate));
            Assert.Equal(1, transport.Connections); Assert.True(ReferenceEquals(results[0], results[1]));
            var session = results[0]; Assert.Equal(PolarServices.HeartRate, session.ReadyServices);
            int hr = 0, errors = 0; session.HeartRateReceived += (_, value) => hr = value.BeatsPerMinute;
            session.ErrorOccurred += (_, __) => errors++;
            await session.StartHeartRateAsync(); transport.Current!.Notify(new byte[] { 0, 70 }); Assert.Equal(70, hr);
            transport.Current.Notify(new byte[] { 1 }); Assert.Equal(1, errors);
            Assert.Throws<PolarFeatureNotAvailableException>(() => session.AsFirmwareSession());
            await session.StopHeartRateAsync(); Assert.False(transport.Current.Subscribed);
            var upgraded = await client.ConnectAsync("device", PolarServices.DeviceInformation);
            Assert.True(ReferenceEquals(session, upgraded));
            Assert.Equal(PolarServices.HeartRate | PolarServices.DeviceInformation, upgraded.ReadyServices);
            await Assert.ThrowsAsync<ArgumentException>(() => client.ConnectAsync("device", PolarServices.HeartRate, h10FileSystem: true));
            client.AutomaticReconnection = true;
            var connected = new TaskCompletionSource<PolarDeviceSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.SessionConnected += (_, value) => connected.TrySetResult(value.Session);
            transport.Current.Drop();
            await Task.WhenAny(connected.Task, Task.Delay(4000)); Assert.True(connected.Task.IsCompletedSuccessfully, "Automatic reconnect did not complete.");
            Assert.Equal(2, transport.Connections); Assert.False(ReferenceEquals(session, connected.Task.Result));
            Assert.Equal(PolarServices.HeartRate | PolarServices.DeviceInformation, connected.Task.Result.ReadyServices);
            await client.DisconnectAsync("device"); await Task.Delay(1200); Assert.Equal(2, transport.Connections);
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => client.PairAsync("device"));
            await PendingConnectionCannotSurviveDisconnectAsync();
            await ScheduledReconnectCannotSurviveDisconnectAsync();
            await PendingReconnectCannotSurviveDisconnectAsync();
            await DisablingReconnectCancelsPendingAttemptAsync();
            await ConnectedCallbackCanReenterAsync();
            await ImmediateRedropSchedulesAnotherAttemptAsync();
        }
        private static async Task CompleteWithinAsync(Task task, string message)
        { await Task.WhenAny(task, Task.Delay(5000)); Assert.True(task.IsCompleted, message); await task; }
        private static async Task PendingConnectionCannotSurviveDisconnectAsync()
        {
            using var transport = new SessionTransport(); using var client = new PolarClient(transport);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IBleConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.ConnectOverride = (_, __) => { entered.TrySetResult(true); return release.Task; };
            int events = 0; client.SessionConnected += (_, __) => events++;
            var pending = client.ConnectAsync("device", PolarServices.HeartRate);
            await CompleteWithinAsync(entered.Task, "Initial connect did not enter the transport.");
            await client.DisconnectAsync("device");
            var late = new SessionConnection(); release.TrySetResult(late);
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
            Assert.True(late.Disposed); Assert.Equal(0, events);
            transport.ConnectOverride = null;
            var fresh = await client.ConnectAsync("device", PolarServices.HeartRate, h10FileSystem: true);
            Assert.True(fresh.IsConnected); Assert.Equal(2, transport.Connections); Assert.Equal(1, events);
        }
        private static async Task ScheduledReconnectCannotSurviveDisconnectAsync()
        {
            using var transport = new SessionTransport(); using var client = new PolarClient(transport) { AutomaticReconnection = true };
            await client.ConnectAsync("device", PolarServices.HeartRate);
            transport.Current!.Drop(); await client.DisconnectAsync("device");
            await Task.Delay(1200); Assert.Equal(1, transport.Connections);
        }
        private static async Task PendingReconnectCannotSurviveDisconnectAsync()
        {
            using var transport = new SessionTransport(); using var client = new PolarClient(transport) { AutomaticReconnection = true };
            await client.ConnectAsync("device", PolarServices.HeartRate);
            var stale = transport.Current!;
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IBleConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.ConnectOverride = (_, __) => { entered.TrySetResult(true); return release.Task; };
            int events = 0; client.SessionConnected += (_, __) => events++;
            stale.Drop(); await CompleteWithinAsync(entered.Task, "Reconnect did not enter the transport.");
            await client.DisconnectAsync("device");
            var late = new SessionConnection(); release.TrySetResult(late);
            await CompleteWithinAsync(late.DisposedSignal.Task, "Cancelled reconnect retained a late transport connection.");
            Assert.Equal(0, events);
            transport.ConnectOverride = null;
            var fresh = await client.ConnectAsync("device", PolarServices.HeartRate, h10FileSystem: true);
            Assert.True(fresh.IsConnected); Assert.Equal(3, transport.Connections); Assert.Equal(1, events);
            stale.Drop(); await Task.Delay(1200); Assert.Equal(3, transport.Connections);
        }
        private static async Task DisablingReconnectCancelsPendingAttemptAsync()
        {
            using var transport = new SessionTransport(); using var client = new PolarClient(transport) { AutomaticReconnection = true };
            await client.ConnectAsync("device", PolarServices.HeartRate);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IBleConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.ConnectOverride = (_, __) => { entered.TrySetResult(true); return release.Task; };
            transport.Current!.Drop(); await CompleteWithinAsync(entered.Task, "Reconnect did not start before disabling.");
            client.AutomaticReconnection = false;
            var late = new SessionConnection(); release.TrySetResult(late);
            await CompleteWithinAsync(late.DisposedSignal.Task, "Disabling automatic reconnection retained a pending connection.");
            Assert.False(late.IsConnected);
            transport.ConnectOverride = null;
            var manual = await client.ConnectAsync("device", PolarServices.HeartRate);
            Assert.True(manual.IsConnected); Assert.Equal(3, transport.Connections);
        }
        private static async Task ConnectedCallbackCanReenterAsync()
        {
            using var transport = new SessionTransport(); using var client = new PolarClient(transport);
            int errors = 0; client.ErrorOccurred += (_, error) => { if (error.Operation == "session connected callback") errors++; };
            client.SessionConnected += (_, value) =>
            {
                var same = client.ConnectAsync("device", PolarServices.DeviceInformation).GetAwaiter().GetResult();
                Assert.True(ReferenceEquals(value.Session, same));
                throw new InvalidOperationException("Application callback failure.");
            };
            var connect = Task.Run(() => client.ConnectAsync("device", PolarServices.HeartRate));
            await CompleteWithinAsync(connect, "SessionConnected callback reentry deadlocked.");
            Assert.True(connect.Result.IsConnected); Assert.Equal(1, transport.Connections); Assert.Equal(1, errors);
            Assert.Equal(PolarServices.HeartRate | PolarServices.DeviceInformation, connect.Result.ReadyServices);
        }
        private static async Task ImmediateRedropSchedulesAnotherAttemptAsync()
        {
            using var transport = new SessionTransport(); using var client = new PolarClient(transport) { AutomaticReconnection = true };
            await client.ConnectAsync("device", PolarServices.HeartRate);
            var recovered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.SessionConnected += (_, __) => { if (transport.Connections == 2) transport.Current!.Drop(); else recovered.TrySetResult(true); };
            transport.Current!.Drop();
            await CompleteWithinAsync(recovered.Task, "Disconnect during reconnect callback lost the next retry.");
            Assert.Equal(3, transport.Connections); Assert.True(transport.Current!.IsConnected);
        }
        private sealed class SessionTransport : IBleTransport
        {
            public int Connections; public SessionConnection? Current;
            public Func<int, CancellationToken, Task<IBleConnection>>? ConnectOverride;
            public event EventHandler<BleDevice>? DeviceDiscovered { add { } remove { } }
            public event EventHandler<BleTransportError>? ErrorOccurred { add { } remove { } }
            public Task StartScanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopScanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<IBleConnection> ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
            { int attempt = Interlocked.Increment(ref Connections); if (ConnectOverride != null) return ConnectOverride(attempt, cancellationToken); Current = new SessionConnection(); return Task.FromResult<IBleConnection>(Current); }
            public void Dispose() { Current?.Dispose(); }
        }
        private sealed class SessionConnection : IBleConnection
        {
            public string DeviceId => "device"; public bool IsConnected { get; private set; } = true;
            public int MaxWriteSize => 229; public bool Subscribed;
            public bool Disposed; public TaskCompletionSource<bool> DisposedSignal { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public event EventHandler<BleNotification>? NotificationReceived;
            public event EventHandler? Disconnected;
            public event EventHandler<BleTransportError>? ErrorOccurred { add { } remove { } }
            public Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<BleGattService>>(new[] {
                    new BleGattService(HeartRateParser.ServiceUuid, new[] { new BleGattCharacteristic(HeartRateParser.MeasurementUuid, BleCharacteristicProperties.Notify) }),
                    new BleGattService(PolarDeviceSession.BluetoothUuid(0x180a), new[] { new BleGattCharacteristic(PolarDeviceSession.BluetoothUuid(0x2a28), BleCharacteristicProperties.Read) }) });
            public Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default)
                => serviceUuid == PolarDeviceSession.BluetoothUuid(0x180a) ? Task.FromResult(Encoding.UTF8.GetBytes("3.0.16")) : throw new InvalidOperationException("Unexpected read.");
            public Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withResponse, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected write.");
            public Task SetNotificationsAsync(Guid serviceUuid, Guid characteristicUuid, bool enabled, CancellationToken cancellationToken = default) { Subscribed = enabled; return Task.CompletedTask; }
            public void Notify(byte[] bytes) => NotificationReceived?.Invoke(this, new BleNotification(HeartRateParser.ServiceUuid, HeartRateParser.MeasurementUuid, bytes));
            public void Drop() { IsConnected = false; Disconnected?.Invoke(this, EventArgs.Empty); }
            public Task DisconnectAsync(CancellationToken cancellationToken = default) { Drop(); return Task.CompletedTask; }
            public void Dispose() { IsConnected = false; Disposed = true; DisposedSignal.TrySetResult(true); }
        }
    }
}
