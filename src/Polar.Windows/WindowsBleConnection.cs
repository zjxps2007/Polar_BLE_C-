using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace PolarBle.Windows
{
    internal sealed partial class WindowsBleConnection : IBleConnection
    {
        private readonly BluetoothLEDevice _device;
        private readonly Action<Exception> _reportError;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<Guid, GattDeviceService> _services = new();
        private readonly Dictionary<(Guid, Guid), GattCharacteristic> _characteristics = new();
        private readonly Dictionary<(Guid, Guid), TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>> _subscriptions = new();
        private readonly object _stateGate = new();
        private GattSession? _session;
        private int _disposed;
        private int _disconnectReported;
        private bool _initialized;
        public string DeviceId { get; }
        public bool IsConnected { get { lock (_stateGate) return Volatile.Read(ref _disposed) == 0 && _device.ConnectionStatus == BluetoothConnectionStatus.Connected; } }
        public int MaxWriteSize { get { lock (_stateGate) return Math.Max(20, (_session?.MaxPduSize ?? 23) - 3); } }
        public event EventHandler<BleNotification>? NotificationReceived;
        public event EventHandler? Disconnected;
        public event EventHandler<BleTransportError>? ErrorOccurred;

        internal WindowsBleConnection(string deviceId, BluetoothLEDevice device, Action<Exception> reportError)
        {
            DeviceId = deviceId;
            _device = device;
            _reportError = reportError;
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        }

        internal async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await RunSerializedAsync(async () =>
            {
                var session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask().ConfigureAwait(false);
                lock (_stateGate)
                {
                    if (Volatile.Read(ref _disposed) != 0) { session?.Dispose(); ThrowIfDisposed(); }
                    _session = session ?? throw new WindowsBleException("Create GATT session", "no session");
                    _session.MaintainConnection = true;
                }
                await DiscoverInternalAsync().ConfigureAwait(false);
                lock (_stateGate)
                {
                    ThrowIfDisposed();
                    if (_device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                        throw new WindowsBleException("Connect", "device did not establish a connection");
                    _initialized = true;
                }
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(CancellationToken cancellationToken = default)
            => RunSerializedAsync(DiscoverInternalAsync, cancellationToken);

        private async Task<IReadOnlyList<BleGattService>> DiscoverInternalAsync()
        {
            ThrowIfDisposed();
            // Preserve GATT objects while subscribed; repeated discovery must not lose active handlers.
            lock (_stateGate)
                if (_services.Count != 0)
                    return BuildServiceSnapshot();
            var result = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
            EnsureSuccess("Discover services", result.Status, result.ProtocolError);
            var discoveredServices = result.Services.ToArray();
            try
            {
                var discovered = new Dictionary<(Guid, Guid), GattCharacteristic>();
                foreach (var service in discoveredServices)
                {
                    ThrowIfDisposed();
                    var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
                    EnsureSuccess("Discover characteristics for " + service.Uuid, chars.Status, chars.ProtocolError);
                    foreach (var characteristic in chars.Characteristics)
                    {
                        if (!discovered.TryAdd((service.Uuid, characteristic.Uuid), characteristic))
                            throw new WindowsBleException("Discover characteristics", "ambiguous duplicate UUID " + characteristic.Uuid);
                    }
                }
                lock (_stateGate)
                {
                    ThrowIfDisposed();
                    foreach (var service in discoveredServices)
                    {
                        if (!_services.TryAdd(service.Uuid, service))
                            throw new WindowsBleException("Discover services", "ambiguous duplicate UUID " + service.Uuid);
                    }
                    foreach (var item in discovered) _characteristics.Add(item.Key, item.Value);
                    return BuildServiceSnapshot();
                }
            }
            catch
            {
                lock (_stateGate) { _services.Clear(); _characteristics.Clear(); }
                foreach (var service in discoveredServices) service.Dispose();
                throw;
            }
        }

        private IReadOnlyList<BleGattService> BuildServiceSnapshot()
            => _services.Keys.Select(service => new BleGattService(service,
                _characteristics.Where(item => item.Key.Item1 == service)
                    .Select(item => new BleGattCharacteristic(item.Key.Item2, ConvertProperties(item.Value.CharacteristicProperties))).ToArray())).ToArray();

        public Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default)
            => RunSerializedAsync(async () =>
            {
                var characteristic = GetCharacteristic(serviceUuid, characteristicUuid);
                if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                    throw new PolarFeatureNotAvailableException("GATT Read " + characteristicUuid);
                var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
                if (RequireEncryptionAfterSecurityRejection(characteristic, result.Status, result.ProtocolError))
                    result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
                EnsureSuccess("Read " + characteristicUuid, result.Status, result.ProtocolError);
                return WindowsBleTransport.ReadBuffer(result.Value);
            }, cancellationToken);

        public async Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withResponse, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            var copy = (byte[])data.Clone();
            await RunSerializedAsync(async () =>
            {
                var characteristic = GetCharacteristic(serviceUuid, characteristicUuid);
                var expected = withResponse ? GattCharacteristicProperties.Write : GattCharacteristicProperties.WriteWithoutResponse;
                if (!characteristic.CharacteristicProperties.HasFlag(expected))
                    throw new PolarFeatureNotAvailableException("GATT " + expected + " " + characteristicUuid);
                // Never silently fragment: Polar PMD/PFTP owns its protocol framing.
                if (!withResponse && copy.Length > MaxWriteSize)
                    throw new ArgumentOutOfRangeException(nameof(data), "Write without response exceeds negotiated GATT payload " + MaxWriteSize);
                using var writer = new DataWriter();
                writer.WriteBytes(copy);
                var buffer = writer.DetachBuffer();
                var result = await characteristic.WriteValueWithResultAsync(buffer,
                    withResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse).AsTask().ConfigureAwait(false);
                if (RequireEncryptionAfterSecurityRejection(characteristic, result.Status, result.ProtocolError))
                    result = await characteristic.WriteValueWithResultAsync(buffer,
                        withResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse).AsTask().ConfigureAwait(false);
                EnsureSuccess("Write " + characteristicUuid, result.Status, result.ProtocolError);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetNotificationsAsync(Guid serviceUuid, Guid characteristicUuid, bool enabled, CancellationToken cancellationToken = default)
        {
            await RunSerializedAsync(async () =>
            {
                var key = (serviceUuid, characteristicUuid);
                var characteristic = GetCharacteristic(serviceUuid, characteristicUuid);
                TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? handler;
                lock (_stateGate) _subscriptions.TryGetValue(key, out handler);
                if (enabled && handler != null) return true;
                if (!enabled && handler == null) return true;
                var configuration = GattClientCharacteristicConfigurationDescriptorValue.None;
                if (enabled)
                {
                    var properties = characteristic.CharacteristicProperties;
                    configuration = properties.HasFlag(GattCharacteristicProperties.Notify)
                        ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                        : properties.HasFlag(GattCharacteristicProperties.Indicate)
                            ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                            : throw new PolarFeatureNotAvailableException("GATT Notify/Indicate " + characteristicUuid);
                    handler = (sender, args) =>
                    {
                        if (Volatile.Read(ref _disposed) != 0) return;
                        try { NotificationReceived?.Invoke(this, new BleNotification(serviceUuid, characteristicUuid, WindowsBleTransport.ReadBuffer(args.CharacteristicValue))); }
                        catch (Exception ex) { ReportError(ex); }
                    };
                    lock (_stateGate)
                    {
                        ThrowIfDisposed();
                        characteristic.ValueChanged += handler;
                        _subscriptions.Add(key, handler);
                    }
                }
                try
                {
                    var result = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(configuration).AsTask().ConfigureAwait(false);
                    if (RequireEncryptionAfterSecurityRejection(characteristic, result.Status, result.ProtocolError))
                        result = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(configuration).AsTask().ConfigureAwait(false);
                    EnsureSuccess("Configure notification " + characteristicUuid, result.Status, result.ProtocolError);
                }
                catch
                {
                    if (enabled) RemoveSubscription(key, characteristic, handler!);
                    throw;
                }
                if (!enabled) RemoveSubscription(key, characteristic, handler!);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        private void RemoveSubscription((Guid, Guid) key, GattCharacteristic characteristic,
            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler)
        {
            lock (_stateGate)
            {
                characteristic.ValueChanged -= handler;
                _subscriptions.Remove(key);
            }
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dispose();
            return Task.CompletedTask;
        }

        private GattCharacteristic GetCharacteristic(Guid serviceUuid, Guid characteristicUuid)
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                if (!_characteristics.TryGetValue((serviceUuid, characteristicUuid), out var characteristic))
                    throw new PolarFeatureNotAvailableException("GATT characteristic " + serviceUuid + "/" + characteristicUuid);
                return characteristic;
            }
        }

        private async Task<T> RunSerializedAsync<T>(Func<Task<T>> action, CancellationToken token)
        {
            ThrowIfDisposed();
            await _gate.WaitAsync(token).ConfigureAwait(false);
            Task<T>? operation = null;
            try
            {
                ThrowIfDisposed();
                operation = action();
                return await operation.WaitAsync(token).ConfigureAwait(false);
            }
            finally
            {
                // Cancellation ends the caller's wait. Windows can still have a pending GATT command;
                // keep serialization until it completes instead of issuing overlapping commands.
                if (operation != null && !operation.IsCompleted)
                    _ = operation.ContinueWith(t => { _ = t.Exception; _gate.Release(); }, TaskScheduler.Default);
                else _gate.Release();
            }
        }

        private static BleCharacteristicProperties ConvertProperties(GattCharacteristicProperties properties)
        {
            var result = BleCharacteristicProperties.None;
            if (properties.HasFlag(GattCharacteristicProperties.Read)) result |= BleCharacteristicProperties.Read;
            if (properties.HasFlag(GattCharacteristicProperties.Write)) result |= BleCharacteristicProperties.Write;
            if (properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)) result |= BleCharacteristicProperties.WriteWithoutResponse;
            if (properties.HasFlag(GattCharacteristicProperties.Notify)) result |= BleCharacteristicProperties.Notify;
            if (properties.HasFlag(GattCharacteristicProperties.Indicate)) result |= BleCharacteristicProperties.Indicate;
            return result;
        }

        private static void EnsureSuccess(string operation, GattCommunicationStatus status, byte? error)
        {
            if (status != GattCommunicationStatus.Success) throw new WindowsBleException(operation, status.ToString(), error);
        }

        private static bool RequireEncryptionAfterSecurityRejection(GattCharacteristic characteristic, GattCommunicationStatus status, byte? error)
        {
            // Retry only an explicit ATT security rejection, never an ambiguous timeout/write.
            // This asks the Windows GATT stack for encryption and never removes existing bonds.
            if (status == GattCommunicationStatus.ProtocolError && (error == 0x05 || error == 0x0F)
                && characteristic.ProtectionLevel == GattProtectionLevel.Plain)
            {
                characteristic.ProtectionLevel = GattProtectionLevel.EncryptionRequired;
                return true;
            }
            return false;
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            lock (_stateGate)
            {
                // Windows may report the initial disconnected state while uncached discovery
                // is establishing the link (including encryption after pairing). Disposing
                // here cancels that OS operation. Only a published connection can lose a link.
                if (!_initialized || sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
            }
            // A connection object is terminal after link loss; callers explicitly reconnect and rediscover.
            Dispose();
        }

        private void ReportDisconnected()
        {
            if (Interlocked.Exchange(ref _disconnectReported, 1) != 0) return;
            try { Disconnected?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { ReportError(ex); }
        }

        private void ReportError(Exception exception)
        {
            try { ErrorOccurred?.Invoke(this, new BleTransportError("Windows BLE connection callback", exception)); }
            catch { }
            _reportError(exception);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new PolarDisconnectedException(DeviceId);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_stateGate)
            {
                _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                foreach (var item in _subscriptions)
                {
                    try { _characteristics[item.Key].ValueChanged -= item.Value; } catch { }
                }
                _subscriptions.Clear();
                if (_session != null)
                {
                    try { _session.MaintainConnection = false; } catch { }
                    try { _session.Dispose(); } catch (Exception ex) { ReportError(ex); }
                    _session = null;
                }
                foreach (var service in _services.Values)
                    try { service.Dispose(); } catch (Exception ex) { ReportError(ex); }
                _services.Clear();
                _characteristics.Clear();
                try { _device.Dispose(); } catch (Exception ex) { ReportError(ex); }
            }
            ReportDisconnected();
        }
    }
}
