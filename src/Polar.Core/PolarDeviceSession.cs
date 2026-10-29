using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Firmware;
using PolarBle.Protocol.Battery;
using PolarBle.Protocol.Features;
using PolarBle.Protocol.HeartRate;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Pmd;
using PolarBle.Protocol.Telemetry;
using PolarBle.Services;
using PolarBle.Transport;
using PolarBle.WatchFaces;

namespace PolarBle
{
    [Flags]
    public enum PolarServices { None = 0, HeartRate = 1, DeviceInformation = 2, Battery = 4, Pmd = 8, FileTransfer = 16, FeatureConfiguration = 32, Thermometer = 64, Telemetry = 128, All = 255 }
    public sealed class PolarServiceReady : EventArgs
    { public PolarServices Service { get; } public PolarServiceReady(PolarServices service) { Service = service; } }
    public sealed class BatteryLevelEvent : EventArgs
    { public byte Level { get; } public BatteryLevelEvent(byte level) { Level = level; } }
    /// <summary>A single BLE connection. Service readiness does not imply every command is supported by this firmware.</summary>
    public sealed class PolarDeviceSession : IDisposable
    {
        private static readonly Guid DisUuid = BluetoothUuid(0x180a), BatteryUuid = BluetoothUuid(0x180f), BatteryLevelUuid = BluetoothUuid(0x2a19);
        private readonly IBleConnection _connection;
        private readonly SemaphoreSlim _lifecycle = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private bool _disposed;
        public string DeviceId => _connection.DeviceId;
        public bool IsConnected => !_disposed && _connection.IsConnected;
        public int MaxWriteSize => _connection.MaxWriteSize;
        public PolarServices ReadyServices { get; private set; }
        public IReadOnlyList<BleGattService> GattServices { get; private set; } = Array.Empty<BleGattService>();
        public IReadOnlyDictionary<Guid, string> DeviceInformation { get; private set; } = new Dictionary<Guid, string>();
        public byte? BatteryLevel { get; private set; }
        public BatteryStatusData? BatteryStatus { get; private set; }
        public PmdClient Pmd { get; }
        public PftpClient Pftp { get; }
        public PolarFileService Files { get; }
        public PolarDeviceService Device { get; }
        public PolarStoredDataService StoredData { get; }
        public PolarOfflineRecordingService Offline { get; }
        public PolarExerciseService Exercises { get; }
        public PolarRestService Rest { get; }
        public PolarLoggingService Logging { get; }
        public WatchFaceService WatchFace { get; }
        public PolarFeatureClient Features { get; }
        public HealthThermometerClient Thermometer { get; }
        public MemfaultClient Telemetry { get; }
        public event EventHandler<HeartRateData>? HeartRateReceived;
        public event EventHandler<BatteryLevelEvent>? BatteryLevelReceived;
        public event EventHandler<BatteryStatusData>? BatteryStatusReceived;
        public event EventHandler<PolarServiceReady>? ServiceReady;
        public event EventHandler<BleTransportError>? ErrorOccurred;
        public event EventHandler? Disconnected;
        public PolarDeviceSession(IBleConnection connection, bool h10FileSystem = false)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            Pmd = new PmdClient(connection); Pftp = new PftpClient(connection); Files = new PolarFileService(Pftp);
            Device = new PolarDeviceService(Pftp, Files, h10FileSystem); StoredData = new PolarStoredDataService(Files);
            Offline = new PolarOfflineRecordingService(Files, Pmd); Exercises = new PolarExerciseService(Pftp, Files);
            Rest = new PolarRestService(Pftp, Files); Logging = new PolarLoggingService(Files); WatchFace = new WatchFaceService(Pftp);
            Features = new PolarFeatureClient(connection); Thermometer = new HealthThermometerClient(connection); Telemetry = new MemfaultClient(connection);
            _connection.NotificationReceived += OnNotification; _connection.Disconnected += OnDisconnected; _connection.ErrorOccurred += OnError;
        }
        public async Task InitializeAsync(PolarServices requested = PolarServices.HeartRate | PolarServices.DeviceInformation | PolarServices.Battery, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _lifecycle.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed(); GattServices = await _connection.DiscoverServicesAsync(linked.Token).ConfigureAwait(false);
                bool Enable(PolarServices feature, Guid service) => requested.HasFlag(feature) && !ReadyServices.HasFlag(feature) && GattServices.Any(x => x.Uuid == service);
                void Ready(PolarServices feature) { ReadyServices |= feature; ServiceReady?.Invoke(this, new PolarServiceReady(feature)); }
                if (Enable(PolarServices.HeartRate, HeartRateParser.ServiceUuid)) Ready(PolarServices.HeartRate);
                if (Enable(PolarServices.DeviceInformation, DisUuid))
                {
                    var data = new Dictionary<Guid, string>();
                    foreach (var characteristic in GattServices.First(x => x.Uuid == DisUuid).Characteristics.Where(x => x.Properties.HasFlag(BleCharacteristicProperties.Read)))
                    {
                        byte[] bytes = await _connection.ReadAsync(DisUuid, characteristic.Uuid, linked.Token).ConfigureAwait(false);
                        // System ID / PnP ID / regulatory certificates are binary, not UTF8 strings.
                        data[characteristic.Uuid] = characteristic.Uuid == BluetoothUuid(0x2a23) || characteristic.Uuid == BluetoothUuid(0x2a50) || characteristic.Uuid == BluetoothUuid(0x2a2a)
                            ? BitConverter.ToString(bytes).Replace("-", "") : Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                    }
                    DeviceInformation = data; Ready(PolarServices.DeviceInformation);
                }
                if (Enable(PolarServices.Battery, BatteryUuid))
                {
                    var characteristics = GattServices.First(x => x.Uuid == BatteryUuid).Characteristics
                        .Where(x => x.Uuid == BatteryLevelUuid || x.Uuid == BatteryStatusParser.StatusUuid).ToArray();
                    var enabled = new List<Guid>();
                    try
                    {
                        foreach (var property in characteristics)
                        {
                            if ((property.Properties & (BleCharacteristicProperties.Notify | BleCharacteristicProperties.Indicate)) != 0)
                            { await _connection.SetNotificationsAsync(BatteryUuid, property.Uuid, true, linked.Token).ConfigureAwait(false); enabled.Add(property.Uuid); }
                            if (property.Properties.HasFlag(BleCharacteristicProperties.Read))
                            {
                                var bytes = await _connection.ReadAsync(BatteryUuid, property.Uuid, linked.Token).ConfigureAwait(false);
                                if (property.Uuid == BatteryLevelUuid) UpdateBattery(bytes); else UpdateBatteryStatus(bytes);
                            }
                        }
                        if (characteristics.Length > 0) Ready(PolarServices.Battery);
                    }
                    catch
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        foreach (var characteristic in enabled)
                            try { await _connection.SetNotificationsAsync(BatteryUuid, characteristic, false, cleanup.Token).ConfigureAwait(false); } catch { }
                        throw;
                    }
                }
                if (Enable(PolarServices.Pmd, PmdClient.ServiceUuid)) { await Pmd.InitializeAsync(linked.Token).ConfigureAwait(false); Ready(PolarServices.Pmd); }
                if (Enable(PolarServices.FileTransfer, PftpClient.ServiceUuid)) { await Pftp.InitializeAsync(linked.Token).ConfigureAwait(false); Ready(PolarServices.FileTransfer); }
                if (Enable(PolarServices.FeatureConfiguration, PolarFeatureClient.ServiceUuid)) { await Features.InitializeAsync(linked.Token).ConfigureAwait(false); Ready(PolarServices.FeatureConfiguration); }
                if (Enable(PolarServices.Thermometer, HealthThermometerClient.ServiceUuid)) Ready(PolarServices.Thermometer);
                if (Enable(PolarServices.Telemetry, MemfaultClient.ServiceUuid)) Ready(PolarServices.Telemetry);
            }
            finally { _lifecycle.Release(); }
        }
        public Task StartHeartRateAsync(CancellationToken cancellationToken = default)
        { Require(PolarServices.HeartRate); return _connection.SetNotificationsAsync(HeartRateParser.ServiceUuid, HeartRateParser.MeasurementUuid, true, cancellationToken); }
        public Task StopHeartRateAsync(CancellationToken cancellationToken = default)
        { Require(PolarServices.HeartRate); return _connection.SetNotificationsAsync(HeartRateParser.ServiceUuid, HeartRateParser.MeasurementUuid, false, cancellationToken); }
        public FirmwareSession AsFirmwareSession(bool h10FileSystem = false, bool isSensor = true)
        { Require(PolarServices.FileTransfer); return new FirmwareSession(Pftp, Files, Device, h10FileSystem, isSensor, ReadyServices.HasFlag(PolarServices.FeatureConfiguration) ? Features : null); }
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        { if (_disposed) return; try { await _connection.DisconnectAsync(cancellationToken).ConfigureAwait(false); } finally { Dispose(); } }
        private void OnNotification(object? sender, BleNotification args)
        {
            if (_disposed) return;
            try
            {
                if (args.ServiceUuid == HeartRateParser.ServiceUuid && args.CharacteristicUuid == HeartRateParser.MeasurementUuid) HeartRateReceived?.Invoke(this, HeartRateParser.Parse(args.Data));
                if (args.ServiceUuid == BatteryUuid && args.CharacteristicUuid == BatteryLevelUuid) UpdateBattery(args.Data);
                if (args.ServiceUuid == BatteryUuid && args.CharacteristicUuid == BatteryStatusParser.StatusUuid) UpdateBatteryStatus(args.Data);
            }
            catch (Exception error) { ErrorOccurred?.Invoke(this, new BleTransportError("decode notification", error)); }
        }
        private void UpdateBattery(byte[] bytes)
        { byte level = BatteryStatusParser.ParseLevel(bytes); BatteryLevel = level; BatteryLevelReceived?.Invoke(this, new BatteryLevelEvent(level)); }
        private void UpdateBatteryStatus(byte[] bytes)
        { var status = BatteryStatusParser.Parse(bytes); BatteryStatus = status; BatteryStatusReceived?.Invoke(this, status); }
        private void OnDisconnected(object? sender, EventArgs args)
        { ReadyServices = PolarServices.None; BatteryLevel = null; BatteryStatus = null; _lifetime.Cancel(); Disconnected?.Invoke(this, EventArgs.Empty); }
        private void OnError(object? sender, BleTransportError error) => ErrorOccurred?.Invoke(this, error);
        private void Require(PolarServices service) { ThrowIfDisposed(); if (!ReadyServices.HasFlag(service)) throw new PolarFeatureNotAvailableException(service.ToString()); }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(PolarDeviceSession)); }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; ReadyServices = PolarServices.None; BatteryLevel = null; BatteryStatus = null; _lifetime.Cancel();
            _connection.NotificationReceived -= OnNotification; _connection.Disconnected -= OnDisconnected; _connection.ErrorOccurred -= OnError;
            Pmd.Dispose(); Pftp.Dispose(); Rest.Dispose(); Features.Dispose(); Thermometer.Dispose(); Telemetry.Dispose(); _connection.Dispose();
        }
        public static Guid BluetoothUuid(ushort value) => new Guid($"0000{value:x4}-0000-1000-8000-00805f9b34fb");
    }
}
