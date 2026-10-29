using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace PolarBle.Windows
{
    /// <summary>Windows 10 2004+ BLE transport. Events are raised on OS worker threads.</summary>
    public sealed partial class WindowsBleTransport : IBleTransport, IPairingBleTransport
    {
        private readonly BluetoothLEAdvertisementWatcher _watcher;
        private readonly ConcurrentDictionary<string, WindowsBleConnection> _connections = new();
        private readonly ConcurrentDictionary<ulong, string> _names = new();
        private readonly SemaphoreSlim _connectGate = new(1, 1);
        private readonly object _scanGate = new();
        private int _disposed;
        public event EventHandler<BleDevice>? DeviceDiscovered;
        public event EventHandler<BleTransportError>? ErrorOccurred;

        public WindowsBleTransport()
        {
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _watcher.Received += OnAdvertisement;
            _watcher.Stopped += OnScanStopped;
            _ = GetRadioStateAsync();
        }

        public Task StartScanAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_scanGate)
            {
                ThrowIfDisposed();
                if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
                    _watcher.Start();
            }
            return Task.CompletedTask;
        }

        public Task StopScanAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_scanGate)
            {
                ThrowIfDisposed();
                _watcher.Stop();
            }
            return Task.CompletedTask;
        }

        /// <summary>Requests the normal Windows encrypted pairing flow. Existing bonds are never removed.</summary>
        public async Task PairAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
            ThrowIfDisposed();
            await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            BluetoothLEDevice? device = null;
            try
            {
                ThrowIfDisposed();
                Task<BluetoothLEDevice> open;
                if (deviceId.StartsWith("ble:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ulong.TryParse(deviceId.Substring(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)
                        || address > 0xFFFFFFFFFFFFUL) throw new ArgumentException("Invalid Bluetooth address identifier.", nameof(deviceId));
                    open = BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask();
                }
                else open = BluetoothLEDevice.FromIdAsync(deviceId).AsTask();
                try { device = await open.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch
                {
                    _ = open.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
                    throw;
                }
                if (device == null) throw new WindowsBleException("Pair", "device unavailable or access denied");
                // Pair the association endpoint returned by enumeration. The DeviceInformation
                // embedded in a BluetoothLEDevice can represent a device interface and cannot
                // necessarily host a custom pairing ceremony.
                var endpoints = await DeviceInformation.FindAllAsync(
                    BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(device.BluetoothAddress),
                    null, DeviceInformationKind.AssociationEndpoint).AsTask(cancellationToken).ConfigureAwait(false);
                if (endpoints.Count == 0) throw new WindowsBleException("Pair", "Bluetooth association endpoint not found; scan while the device is advertising");
                var pairing = endpoints[0].Pairing;
                if (pairing.IsPaired) return;
                if (!pairing.CanPair) throw new WindowsBleException("Pair", "device does not allow pairing in its current state");
                // Basic DeviceInformationPairing.PairAsync is not supported by desktop apps.
                // Use custom Just Works pairing, which can still require Windows system consent.
                // https://learn.microsoft.com/windows/apps/desktop/modernize/winrt-api-desktop-app-support
                var custom = pairing.Custom;
                void ConfirmOnly(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs request)
                { if (request.PairingKind == DevicePairingKinds.ConfirmOnly) request.Accept(); }
                bool registered = false;
                DevicePairingResult result;
                try
                {
                    custom.PairingRequested += ConfirmOnly;
                    registered = true;
                    result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.Encryption)
                        .AsTask(cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                { throw new WindowsBleException("Pair (Windows Bluetooth Settings may be required for this desktop process)", ex); }
                finally { if (registered) custom.PairingRequested -= ConfirmOnly; }
                if (result.Status != DevicePairingResultStatus.Paired && result.Status != DevicePairingResultStatus.AlreadyPaired)
                    throw new WindowsBleException("Pair", result.Status + "; protection=" + result.ProtectionLevelUsed + ". Complete pairing in Windows Bluetooth Settings if system consent is required.");
            }
            finally { device?.Dispose(); _connectGate.Release(); }
        }

        public async Task<IBleConnection> ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
            ThrowIfDisposed();
            await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            BluetoothLEDevice? device = null;
            WindowsBleConnection? connection = null;
            try
            {
                ThrowIfDisposed();
                if (_connections.TryGetValue(deviceId, out var existing) && existing.IsConnected)
                    throw new InvalidOperationException("This transport already has a connection to " + deviceId);
                Task<BluetoothLEDevice> pending;
                if (deviceId.StartsWith("ble:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ulong.TryParse(deviceId.Substring(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)
                        || address > 0xFFFFFFFFFFFFUL)
                        throw new ArgumentException("Invalid Bluetooth address identifier.", nameof(deviceId));
                    pending = BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask();
                }
                else
                {
                    pending = BluetoothLEDevice.FromIdAsync(deviceId).AsTask();
                }
                try { device = await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch
                {
                    // Windows may finish a connection after cancellation. Dispose its late result.
                    _ = pending.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
                    throw;
                }
                if (device == null) throw new WindowsBleException("Open BLE device", "device unavailable or access denied");
                ThrowIfDisposed();
                connection = new WindowsBleConnection(deviceId, device, ReportBackgroundError);
                device = null; // Ownership transferred.
                await connection.InitializeAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfDisposed();
                if (_connections.TryRemove(deviceId, out var stale)) stale.Dispose();
                _connections[deviceId] = connection;
                if (Volatile.Read(ref _disposed) != 0)
                { _connections.TryRemove(deviceId, out _); ThrowIfDisposed(); }
                return connection;
            }
            catch
            {
                connection?.Dispose();
                device?.Dispose();
                throw;
            }
            finally { _connectGate.Release(); }
        }

        private void OnAdvertisement(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                var name = args.Advertisement.LocalName;
                if (!string.IsNullOrEmpty(name)) _names[args.BluetoothAddress] = name;
                else _names.TryGetValue(args.BluetoothAddress, out name);
                var manufacturerData = new Dictionary<ushort, byte[]>();
                foreach (var item in args.Advertisement.ManufacturerData)
                    manufacturerData[item.CompanyId] = ReadBuffer(item.Data);
                var found = new BleDevice("ble:" + args.BluetoothAddress.ToString("X12", CultureInfo.InvariantCulture),
                    name ?? "", args.BluetoothAddress, args.RawSignalStrengthInDBm,
                    manufacturerData, args.Advertisement.ServiceUuids.ToArray());
                DeviceDiscovered?.Invoke(this, found);
            }
            catch (Exception ex) { ReportBackgroundError(ex); }
        }

        private void OnScanStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            if (Volatile.Read(ref _disposed) == 0 && args.Error != BluetoothError.Success)
                ReportBackgroundError(new WindowsBleException("Scan", args.Error.ToString()));
        }

        private void ReportBackgroundError(Exception error)
        {
            try { ErrorOccurred?.Invoke(this, new BleTransportError("Windows BLE callback", error)); }
            catch { /* User event handlers must not escape a Windows Runtime callback. */ }
        }

        internal static byte[] ReadBuffer(IBuffer buffer)
        {
            using var reader = DataReader.FromBuffer(buffer);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            return bytes;
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            DisposeRadioObservation();
            lock (_scanGate)
            {
                _watcher.Received -= OnAdvertisement;
                _watcher.Stopped -= OnScanStopped;
                try { _watcher.Stop(); } catch { }
            }
            foreach (var item in _connections.Values) item.Dispose();
            _connections.Clear();
            _names.Clear();
            // Do not dispose the semaphore: an in-flight ConnectAsync still owns/relinquishes it.
        }
    }
}
