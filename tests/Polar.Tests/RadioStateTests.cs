using System;
using System.Threading;
using System.Threading.Tasks;
using PolarBle;
using PolarBle.Transport;

namespace Polar.Tests
{
    internal static class RadioStateTests
    {
        public static void Run() => RunAsync().GetAwaiter().GetResult();
        private static async Task RunAsync()
        {
            using (var client = new PolarClient(new PlainTransport()))
            {
                Assert.Equal(BleRadioState.Unknown, await client.GetBluetoothRadioStateAsync());
                Assert.True(client.IsBlePowered == null);
            }
            var transport = new RadioTransport();
            using var observed = new PolarClient(transport);
            Assert.Equal(BleRadioState.On, await observed.GetBluetoothRadioStateAsync());
            Assert.True(observed.IsBlePowered == true);
            int notifications = 0;
            observed.BluetoothRadioStateChanged += (_, __) => notifications++;
            transport.Emit(BleRadioState.Off); transport.Emit(BleRadioState.Off);
            Assert.Equal(1, notifications); Assert.True(observed.IsBlePowered == false);
            transport.Emit(BleRadioState.Unknown);
            Assert.True(observed.IsBlePowered == null);
            transport.Emit(BleRadioState.Unavailable);
            Assert.True(observed.IsBlePowered == false);
            transport.Failure = new InvalidOperationException("OS state unavailable");
            await Assert.ThrowsAsync<InvalidOperationException>(() => observed.GetBluetoothRadioStateAsync());
            Assert.Equal(BleRadioState.Unavailable, observed.BluetoothRadioState);
            transport.Failure = null;
            transport.Pending = new TaskCompletionSource<BleRadioState>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateRead = observed.GetBluetoothRadioStateAsync();
            observed.Dispose();
            Assert.Equal(0, transport.Subscribers);
            transport.Pending.SetResult(BleRadioState.On);
            await Assert.ThrowsAsync<OperationCanceledException>(() => lateRead);
            transport.Emit(BleRadioState.On);
            Assert.Equal(3, notifications);
            Assert.Equal(BleRadioState.Unavailable, observed.BluetoothRadioState);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => observed.GetBluetoothRadioStateAsync());
        }

        private class PlainTransport : IBleTransport
        {
            public event EventHandler<BleDevice>? DeviceDiscovered { add { } remove { } }
            public event EventHandler<BleTransportError>? ErrorOccurred { add { } remove { } }
            public Task StartScanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopScanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<IBleConnection> ConnectAsync(string id, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Not exercised.");
            public void Dispose() { }
        }
        private sealed class RadioTransport : PlainTransport, IBleRadioStateProvider
        {
            private EventHandler<BleRadioStateChanged>? _changed;
            public int Subscribers { get; private set; }
            public Exception? Failure;
            public TaskCompletionSource<BleRadioState>? Pending;
            public event EventHandler<BleRadioStateChanged>? RadioStateChanged
            { add { _changed += value; Subscribers++; } remove { _changed -= value; Subscribers--; } }
            public void Emit(BleRadioState state) => _changed?.Invoke(this, new BleRadioStateChanged(state));
            public Task<BleRadioState> GetRadioStateAsync(CancellationToken cancellationToken = default)
                => Failure != null ? Task.FromException<BleRadioState>(Failure) : Pending?.Task ?? Task.FromResult(BleRadioState.On);
        }
    }
}
