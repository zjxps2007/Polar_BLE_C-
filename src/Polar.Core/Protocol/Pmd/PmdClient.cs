// Ported from Polar BLE SDK. Copyright Polar Electro Oy. See LICENSES/Polar_SDK_License.txt.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Transport;

namespace PolarBle.Protocol.Pmd
{
    /// <summary>One PMD session per BLE connection. Events arrive on the transport thread; Unity consumers must dispatch to the main thread.</summary>
    public sealed class PmdClient : IDisposable
    {
        public static readonly Guid ServiceUuid = new Guid("fb005c80-02e7-f387-1cad-8acd2d8df0c8");
        public static readonly Guid ControlPointUuid = new Guid("fb005c81-02e7-f387-1cad-8acd2d8df0c8");
        public static readonly Guid DataUuid = new Guid("fb005c82-02e7-f387-1cad-8acd2d8df0c8");
        private readonly IBleConnection _connection;
        private readonly SemaphoreSlim _commands = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _initialization = new SemaphoreSlim(1, 1);
        private readonly object _gate = new object();
        private readonly Dictionary<PmdMeasurementType, PmdSettings> _settings = new Dictionary<PmdMeasurementType, PmdSettings>();
        private readonly Dictionary<int, ulong> _timestamps = new Dictionary<int, ulong>();
        private TaskCompletionSource<byte[]>? _pending;
        private readonly List<byte> _response = new List<byte>();
        private byte _pendingCommand;
        private byte? _responseType;
        private bool _ready, _disposed, _commandStreamInvalid;
        public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public event EventHandler<PmdDataBatch>? DataReceived;
        public event EventHandler<PmdErrorEventArgs>? ErrorOccurred;
        public event EventHandler<PmdMeasurementStoppedEventArgs>? MeasurementStopped;
        public PmdClient(IBleConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            connection.NotificationReceived += OnNotification;
            connection.Disconnected += OnDisconnected;
            connection.ErrorOccurred += OnTransportError;
        }
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CheckConnected(); if (_ready) return;
                await _connection.SetNotificationsAsync(ServiceUuid, ControlPointUuid, true, cancellationToken).ConfigureAwait(false);
                try { await _connection.SetNotificationsAsync(ServiceUuid, DataUuid, true, cancellationToken).ConfigureAwait(false); }
                catch { try { await _connection.SetNotificationsAsync(ServiceUuid, ControlPointUuid, false, CancellationToken.None).ConfigureAwait(false); } catch { } throw; }
                _ready = true;
            }
            finally { _initialization.Release(); }
        }
        public async Task<IReadOnlyCollection<PmdMeasurementType>> ReadFeaturesAsync(CancellationToken cancellationToken = default)
        {
            CheckConnected(); var bytes = await _connection.ReadAsync(ServiceUuid, ControlPointUuid, cancellationToken).ConfigureAwait(false);
            PmdBinary.Require(bytes, 0, 3); var result = new List<PmdMeasurementType>();
            for (int id = 0; id <= 15; id++)
                if ((bytes[1 + id / 8] & (1 << (id % 8))) != 0 && Enum.IsDefined(typeof(PmdMeasurementType), (byte)id)) result.Add((PmdMeasurementType)id);
            return result;
        }
        public async Task<PmdSettings> GetSettingsAsync(PmdMeasurementType type, bool offline = false, bool full = false, CancellationToken cancellationToken = default)
            => PmdSettings.Parse(await CommandAsync(full ? (byte)4 : (byte)1, new[] { RecordingByte(type, offline) }, cancellationToken).ConfigureAwait(false));
        public async Task<PmdSettings> GetDerivedSettingsGroupAsync(byte groupId, CancellationToken cancellationToken = default)
            => PmdSettings.Parse(await CommandAsync(10, new[] { groupId }, cancellationToken).ConfigureAwait(false));
        public async Task StartAsync(PmdMeasurementType type, PmdSettings settings, bool offline = false, PmdSecret? secret = null, CancellationToken cancellationToken = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var bytes = new List<byte> { RecordingByte(type, offline) }; bytes.AddRange(settings.Serialize());
            if (secret != null) bytes.AddRange(secret.SerializeToSettings());
            // Record settings before sending: some devices notify immediately after accepting the command.
            PmdSettings? previous;
            lock (_gate) { _settings.TryGetValue(type, out previous); _settings[type] = settings; ClearTimestamps(type); }
            try
            {
                var response = await CommandAsync(2, bytes.ToArray(), cancellationToken).ConfigureAwait(false);
                var updated = response.Length == 0 ? settings : settings.WithResponse(PmdSettings.Parse(response));
                lock (_gate) _settings[type] = updated;
            }
            catch { lock (_gate) { if (previous == null) _settings.Remove(type); else _settings[type] = previous; } throw; }
        }
        /// <summary>Upstream STOP_MEASUREMENT sends the measurement ID; the device stops that measurement.</summary>
        public async Task StopAsync(PmdMeasurementType type, CancellationToken cancellationToken = default)
        {
            await CommandAsync(3, new[] { (byte)type }, cancellationToken).ConfigureAwait(false);
            lock (_gate) { _settings.Remove(type); ClearTimestamps(type); }
        }
        public async Task<IReadOnlyDictionary<PmdMeasurementType, PmdMeasurementStatus>> GetMeasurementStatusAsync(CancellationToken cancellationToken = default)
        {
            var bytes = await CommandAsync(5, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<PmdMeasurementType, PmdMeasurementStatus>();
            foreach (byte value in bytes) result[(PmdMeasurementType)(value & 63)] = (PmdMeasurementStatus)(value >> 6);
            return result;
        }
        public async Task SetSdkModeAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            await CommandAsync(enabled ? (byte)2 : (byte)3, new[] { (byte)PmdMeasurementType.SdkMode }, cancellationToken).ConfigureAwait(false);
            lock (_gate) { _settings.Clear(); _timestamps.Clear(); }
        }
        public async Task<bool> GetSdkModeAsync(CancellationToken cancellationToken = default)
        {
            var bytes = await CommandAsync(6, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
            PmdBinary.Require(bytes, 0, 1); if (bytes[0] > 1) throw new PolarProtocolException("Unknown SDK mode status."); return bytes[0] == 1;
        }
        public async Task<PmdOfflineTrigger> GetOfflineTriggerAsync(CancellationToken cancellationToken = default)
            => PmdOfflineTrigger.Parse(await CommandAsync(7, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false));
        public async Task SetOfflineTriggerModeAsync(PmdTriggerMode mode, CancellationToken cancellationToken = default)
        {
            if (mode > PmdTriggerMode.ExerciseStart) throw new ArgumentOutOfRangeException(nameof(mode));
            await CommandAsync(8, new[] { (byte)mode }, cancellationToken).ConfigureAwait(false);
        }
        public async Task SetOfflineTriggerSettingAsync(PmdMeasurementType type, bool enabled, PmdSettings? settings = null,
            PmdSecret? secret = null, CancellationToken cancellationToken = default)
        {
            var bytes = new List<byte> { enabled ? (byte)1 : (byte)0, (byte)type };
            if (enabled)
            {
                var data = new List<byte>(); if (settings != null) data.AddRange(settings.Serialize()); if (secret != null) data.AddRange(secret.SerializeToSettings());
                if (data.Count > 255) throw new ArgumentException("Offline trigger settings exceed protocol length.");
                bytes.Add((byte)data.Count); bytes.AddRange(data);
            }
            await CommandAsync(9, bytes.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        public async Task SetOfflineTriggerAsync(PmdOfflineTrigger trigger, PmdSecret? secret = null, CancellationToken cancellationToken = default)
        {
            await SetOfflineTriggerModeAsync(trigger.Mode, cancellationToken).ConfigureAwait(false);
            if (trigger.Mode == PmdTriggerMode.Disabled) return;
            var available = await GetOfflineTriggerAsync(cancellationToken).ConfigureAwait(false);
            foreach (var type in available.Triggers.Keys)
            {
                bool enabled = trigger.Triggers.TryGetValue(type, out var selected) && selected != null;
                await SetOfflineTriggerSettingAsync(type, enabled, selected, secret, cancellationToken).ConfigureAwait(false);
            }
        }
        private async Task<byte[]> CommandAsync(byte command, byte[] parameters, CancellationToken token)
        {
            await _commands.WaitAsync(token).ConfigureAwait(false);
            TaskCompletionSource<byte[]>? source = null;
            try
            {
                CheckConnected(); if (!_ready) throw new PolarFeatureNotAvailableException("PMD: call InitializeAsync first");
                if (_commandStreamInvalid) throw new PolarProtocolException("PMD control transaction was interrupted; reconnect before issuing another command.");
                if (CommandTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
                source = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) { _pending = source; _pendingCommand = command; _responseType = null; _response.Clear(); }
                var request = new byte[1 + parameters.Length]; request[0] = command; Array.Copy(parameters, 0, request, 1, parameters.Length);
                if (request.Length > _connection.MaxWriteSize) throw new PolarProtocolException("PMD control command exceeds negotiated ATT payload.");
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(CommandTimeout);
                    using (timeout.Token.Register(() => source.TrySetCanceled()))
                    {
                        try
                        {
                            await _connection.WriteAsync(ServiceUuid, ControlPointUuid, request, true, timeout.Token).ConfigureAwait(false);
                            return await source.Task.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            _commandStreamInvalid = true;
                            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                            throw new TimeoutException("PMD control point response timed out; reconnect before retrying.");
                        }
                        catch (PmdControlPointException) { throw; }
                        catch { _commandStreamInvalid = true; throw; }
                    }
                }
            }
            finally { lock (_gate) if (_pending == source) _pending = null; _commands.Release(); }
        }
        private void OnNotification(object? sender, BleNotification notification)
        {
            if (_disposed || notification.ServiceUuid != ServiceUuid) return;
            try
            {
                if (notification.CharacteristicUuid == ControlPointUuid)
                {
                    var bytes = notification.Data; PmdBinary.Require(bytes, 0, 1);
                    if (bytes[0] == 1)
                    {
                        PmdBinary.Require(bytes, 0, 2);
                        for (int i = 1; i < bytes.Length; i++)
                        {
                            var stoppedType = (PmdMeasurementType)(bytes[i] & 63);
                            lock (_gate) ClearTimestamps(stoppedType);
                            MeasurementStopped?.Invoke(this, new PmdMeasurementStoppedEventArgs(stoppedType));
                        }
                        return;
                    }
                    PmdBinary.Require(bytes, 0, 4);
                    if (bytes[0] != 0xf0) throw new PolarProtocolException("Unknown PMD server control command.");
                    lock (_gate)
                    {
                        if (_pending == null || bytes[1] != _pendingCommand) return;
                        if (_responseType.HasValue && _responseType.Value != bytes[2]) throw new PolarProtocolException("PMD multi-packet response changed measurement type.");
                        _responseType = bytes[2];
                        if (bytes[3] != 0) { _pending.TrySetException(new PmdControlPointException(bytes[1], bytes[3])); return; }
                        if (bytes.Length > 5) _response.AddRange(PmdBinary.Slice(bytes, 5, bytes.Length - 5));
                        if (_response.Count > 65536) throw new PolarProtocolException("PMD response exceeds limit.");
                        if (bytes.Length <= 4 || bytes[4] == 0)
                        {
                            // Apply factor before any subsequent sensor notification can be decoded.
                            if (_pendingCommand == 2 && _settings.TryGetValue((PmdMeasurementType)(bytes[2] & 63), out var current) && _response.Count > 0)
                                _settings[(PmdMeasurementType)(bytes[2] & 63)] = current.WithResponse(PmdSettings.Parse(_response.ToArray()));
                            _pending.TrySetResult(_response.ToArray());
                        }
                    }
                }
                else if (notification.CharacteristicUuid == DataUuid)
                {
                    var bytes = notification.Data; PmdBinary.Require(bytes, 0, 10); var type = (PmdMeasurementType)(bytes[0] & 63);
                    int key = ((int)type << 8) | (bytes[9] & 127); PmdDataBatch batch;
                    lock (_gate)
                    {
                        _settings.TryGetValue(type, out var settings); _timestamps.TryGetValue(key, out ulong previous);
                        int rate = settings?.Get(PmdSettingType.SampleRate) ?? 0;
                        float factor = settings == null ? 1 : PmdBinary.Float(settings.Get(PmdSettingType.Factor, BitConverter.SingleToInt32Bits(1f)));
                        int methodBits = settings?.Get(PmdSettingType.DerivedMeasurementMethod) ?? 0;
                        var methods = methodBits == 0 ? null : Enumerable.Range(0, 16).Where(i => (methodBits & (1 << i)) != 0).ToArray();
                        batch = PmdFrameParser.Parse(bytes, rate, factor, previous, methods); _timestamps[key] = batch.FrameTimestampNanoseconds;
                    }
                    DataReceived?.Invoke(this, batch);
                }
            }
            catch (Exception error)
            {
                if (notification.CharacteristicUuid == ControlPointUuid) lock (_gate) _pending?.TrySetException(error);
                ErrorOccurred?.Invoke(this, new PmdErrorEventArgs(error));
            }
        }
        private void OnDisconnected(object? sender, EventArgs args)
        {
            _ready = false; _commandStreamInvalid = true;
            lock (_gate) { _settings.Clear(); _timestamps.Clear(); _pending?.TrySetException(new PolarDisconnectedException(_connection.DeviceId)); }
        }
        private void OnTransportError(object? sender, BleTransportError error)
        {
            _commandStreamInvalid = true;
            lock (_gate) _pending?.TrySetException(error.Exception);
            ErrorOccurred?.Invoke(this, new PmdErrorEventArgs(error.Exception));
        }
        private void ClearTimestamps(PmdMeasurementType type)
        { foreach (int key in _timestamps.Keys.Where(k => (k >> 8) == (int)type).ToArray()) _timestamps.Remove(key); }
        private static byte RecordingByte(PmdMeasurementType type, bool offline) => (byte)((byte)type | (offline ? 128 : 0));
        private void CheckConnected()
        { if (_disposed) throw new ObjectDisposedException(nameof(PmdClient)); if (!_connection.IsConnected) throw new PolarDisconnectedException(_connection.DeviceId); }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; _ready = false;
            _connection.NotificationReceived -= OnNotification; _connection.Disconnected -= OnDisconnected;
            _connection.ErrorOccurred -= OnTransportError;
            lock (_gate) _pending?.TrySetException(new ObjectDisposedException(nameof(PmdClient)));
            // Connection owner performs asynchronous unsubscription/disconnection; no async-void cleanup.
        }
    }
    public sealed class PmdMeasurementStoppedEventArgs : EventArgs
    { public PmdMeasurementType Type { get; } public PmdMeasurementStoppedEventArgs(PmdMeasurementType type) { Type = type; } }
}
