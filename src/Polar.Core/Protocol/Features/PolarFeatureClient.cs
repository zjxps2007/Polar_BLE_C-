// Ported from Polar BLE SDK. Copyright Polar Electro Oy. See LICENSES/Polar_SDK_License.txt.
using System;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Protocol.Pmd;
using PolarBle.Transport;

namespace PolarBle.Protocol.Features
{
    [Flags] public enum PolarFeatureFlags : ushort
    {
        None = 0, Broadcast = 1, Khz5 = 2, FirmwareUpdate = 4, WhisperMode = 16,
        BleMode = 64, MultiConnection = 128, AntPlus = 256, SecurityMode = 512, SensorInitiatedSecurity = 2048
    }
    public enum PolarFeatureCommand : byte
    {
        ConfigureBroadcast = 1, GetBroadcast = 2, Configure5Khz = 3, Get5Khz = 4,
        ConfigureWhisperMode = 5, GetWhisperMode = 6, ConfigureBleMode = 7,
        ConfigureMultiConnection = 8, GetMultiConnection = 9, ConfigureAntPlus = 10,
        GetAntPlus = 11, GetSecurityMode = 12, ConfigureSensorInitiatedSecurity = 14, GetSensorInitiatedSecurity = 15
    }
    public sealed class PolarFeatureCommandException : PolarException
    {
        public PolarFeatureCommand Command { get; } public byte Status { get; }
        public PolarFeatureCommandException(PolarFeatureCommand command, byte status)
            : base("Polar feature command " + command + " failed with status " + status) { Command = command; Status = status; }
    }
    public sealed class PolarFeatureClient : IDisposable
    {
        public static readonly Guid ServiceUuid = new Guid("6217ff4b-fb31-1140-ad5a-a45545d7ecf3");
        public static readonly Guid FeaturesUuid = new Guid("6217ff4c-c8ec-b1fb-1380-3ad986708e2d");
        public static readonly Guid ControlPointUuid = new Guid("6217ff4d-91bb-91d0-7e2a-7cd3bda8a1f3");
        private readonly IBleConnection _connection;
        private readonly SemaphoreSlim _serial = new SemaphoreSlim(1, 1);
        private readonly object _gate = new object();
        private TaskCompletionSource<byte[]>? _pending;
        private PolarFeatureCommand _pendingCommand;
        private bool _ready, _disposed, _invalid;
        public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public event EventHandler<PmdErrorEventArgs>? ErrorOccurred;
        public PolarFeatureClient(IBleConnection connection)
        {
            _connection = connection; connection.NotificationReceived += OnNotification;
            connection.Disconnected += OnDisconnected; connection.ErrorOccurred += OnError;
        }
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { Check(); if (!_ready) { await _connection.SetNotificationsAsync(ServiceUuid, ControlPointUuid, true, cancellationToken).ConfigureAwait(false); _ready = true; } }
            finally { _serial.Release(); }
        }
        public async Task<PolarFeatureFlags> ReadFeaturesAsync(CancellationToken cancellationToken = default)
        {
            Check(); var data = await _connection.ReadAsync(ServiceUuid, FeaturesUuid, cancellationToken).ConfigureAwait(false);
            PmdBinary.Require(data, 0, 2); return (PolarFeatureFlags)PmdBinary.Unsigned(data, 0, 2);
        }
        public async Task<byte[]> SendCommandAsync(PolarFeatureCommand command, byte[]? parameters = null, CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(typeof(PolarFeatureCommand), command)) throw new ArgumentOutOfRangeException(nameof(command));
            bool configure = command == PolarFeatureCommand.ConfigureBroadcast || command == PolarFeatureCommand.Configure5Khz ||
                command == PolarFeatureCommand.ConfigureWhisperMode || command == PolarFeatureCommand.ConfigureBleMode ||
                command == PolarFeatureCommand.ConfigureMultiConnection || command == PolarFeatureCommand.ConfigureAntPlus || command == PolarFeatureCommand.ConfigureSensorInitiatedSecurity;
            if (configure && (parameters == null || parameters.Length == 0)) throw new ArgumentException("Configure command requires parameters.", nameof(parameters));
            if (!configure && parameters != null && parameters.Length > 0) throw new ArgumentException("Query command takes no parameters.", nameof(parameters));
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            TaskCompletionSource<byte[]>? completion = null;
            try
            {
                Check(); if (!_ready) throw new PolarFeatureNotAvailableException("PFCS: InitializeAsync required");
                if (_invalid) throw new PolarProtocolException("PFCS transaction interrupted; reconnect before retrying.");
                if (CommandTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
                var bytes = new byte[1 + (parameters?.Length ?? 0)]; bytes[0] = (byte)command;
                if (parameters != null) Array.Copy(parameters, 0, bytes, 1, parameters.Length);
                if (bytes.Length > _connection.MaxWriteSize) throw new ArgumentException("PFCS command exceeds negotiated ATT payload.");
                completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) { _pending = completion; _pendingCommand = command; }
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(CommandTimeout);
                    using (timeout.Token.Register(() => completion.TrySetCanceled()))
                    {
                        try
                        {
                            await _connection.WriteAsync(ServiceUuid, ControlPointUuid, bytes, true, timeout.Token).ConfigureAwait(false);
                            return await completion.Task.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            _invalid = true; if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                            throw new TimeoutException("PFCS response timed out; reconnect before retrying.");
                        }
                        catch (PolarFeatureCommandException) { throw; }
                        catch { _invalid = true; throw; }
                    }
                }
            }
            finally { lock (_gate) if (_pending == completion) _pending = null; _serial.Release(); }
        }
        public async Task SetMultiConnectionAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            await RequireFeatureAsync(PolarFeatureFlags.MultiConnection, cancellationToken).ConfigureAwait(false);
            await SendCommandAsync(PolarFeatureCommand.ConfigureMultiConnection, new[] { enabled ? (byte)1 : (byte)0 }, cancellationToken).ConfigureAwait(false);
        }
        public async Task<bool> GetMultiConnectionAsync(CancellationToken cancellationToken = default)
        { await RequireFeatureAsync(PolarFeatureFlags.MultiConnection, cancellationToken).ConfigureAwait(false); return ParseBoolean(await SendCommandAsync(PolarFeatureCommand.GetMultiConnection, cancellationToken: cancellationToken).ConfigureAwait(false)); }
        public async Task SetSensorInitiatedSecurityAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            await RequireFeatureAsync(PolarFeatureFlags.SensorInitiatedSecurity, cancellationToken).ConfigureAwait(false);
            await SendCommandAsync(PolarFeatureCommand.ConfigureSensorInitiatedSecurity, new[] { enabled ? (byte)1 : (byte)0 }, cancellationToken).ConfigureAwait(false);
        }
        public async Task<bool> GetSensorInitiatedSecurityAsync(CancellationToken cancellationToken = default)
        { await RequireFeatureAsync(PolarFeatureFlags.SensorInitiatedSecurity, cancellationToken).ConfigureAwait(false); return ParseBoolean(await SendCommandAsync(PolarFeatureCommand.GetSensorInitiatedSecurity, cancellationToken: cancellationToken).ConfigureAwait(false)); }
        private async Task RequireFeatureAsync(PolarFeatureFlags flag, CancellationToken token)
        { if (((await ReadFeaturesAsync(token).ConfigureAwait(false)) & flag) == 0) throw new PolarFeatureNotAvailableException(flag.ToString()); }
        private static bool ParseBoolean(byte[] data)
        { PmdBinary.Require(data, 0, 1); if (data[0] > 1) throw new PolarProtocolException("Invalid PFCS boolean response."); return data[0] == 1; }
        private void OnNotification(object? sender, BleNotification args)
        {
            if (_disposed || args.ServiceUuid != ServiceUuid || args.CharacteristicUuid != ControlPointUuid) return;
            try
            {
                var data = args.Data; PmdBinary.Require(data, 0, 3);
                if (data[0] != 240) throw new PolarProtocolException("Invalid PFCS response marker.");
                lock (_gate)
                {
                    if (_pending == null || data[1] != (byte)_pendingCommand) return;
                    if (data[2] != 1) _pending.TrySetException(new PolarFeatureCommandException(_pendingCommand, data[2]));
                    else _pending.TrySetResult(PmdBinary.Slice(data, 3, data.Length - 3));
                }
            }
            catch (Exception error) { _invalid = true; lock (_gate) _pending?.TrySetException(error); ErrorOccurred?.Invoke(this, new PmdErrorEventArgs(error)); }
        }
        private void OnDisconnected(object? sender, EventArgs args)
        { _ready = false; _invalid = true; lock (_gate) _pending?.TrySetException(new PolarDisconnectedException(_connection.DeviceId)); }
        private void OnError(object? sender, BleTransportError args)
        { _invalid = true; lock (_gate) _pending?.TrySetException(args.Exception); ErrorOccurred?.Invoke(this, new PmdErrorEventArgs(args.Exception)); }
        private void Check()
        { if (_disposed) throw new ObjectDisposedException(nameof(PolarFeatureClient)); if (!_connection.IsConnected) throw new PolarDisconnectedException(_connection.DeviceId); }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; _connection.NotificationReceived -= OnNotification;
            _connection.Disconnected -= OnDisconnected; _connection.ErrorOccurred -= OnError;
            lock (_gate) _pending?.TrySetException(new ObjectDisposedException(nameof(PolarFeatureClient)));
        }
    }
}
