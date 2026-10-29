using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Transport;

namespace PolarBle.Protocol.Pftp
{
    public sealed class PftpNotification : EventArgs
    {
        public byte Id { get; }
        public byte[] Parameters { get; }
        public PftpNotification(byte id, byte[] parameters) { Id = id; Parameters = parameters; }
    }

    /// <summary>Serialized Polar RFC77 transport. Owns subscriptions, not the BLE connection.</summary>
    public sealed class PftpClient : IDisposable
    {
        public static readonly Guid ServiceUuid = new Guid("0000feee-0000-1000-8000-00805f9b34fb");
        public static readonly Guid MtuUuid = new Guid("fb005c51-02e7-f387-1cad-8acd2d8df0c8");
        public static readonly Guid D2hUuid = new Guid("fb005c52-02e7-f387-1cad-8acd2d8df0c8");
        public static readonly Guid H2dUuid = new Guid("fb005c53-02e7-f387-1cad-8acd2d8df0c8");
        private readonly IBleConnection _connection;
        private readonly SemaphoreSlim _operations = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _received = new SemaphoreSlim(0);
        private readonly Queue<byte[]> _packets = new Queue<byte[]>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly object _gate = new object();
        private PftpAssembler? _notification;
        private bool _ready, _disposed, _faulted, _pending;
        private Exception? _transportFailure;
        public TimeSpan PacketTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public int MaximumResponseBytes { get; set; } = 64 * 1024 * 1024;
        public event EventHandler<PftpNotification>? NotificationReceived;
        public event EventHandler<Exception>? NotificationError;
        public PftpClient(IBleConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _connection.NotificationReceived += OnNotification;
            _connection.Disconnected += OnDisconnected;
            _connection.ErrorOccurred += OnTransportError;
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CheckConnection();
                if (_faulted) throw new PolarProtocolException("PFTP stream failed; reconnect and create a new client.");
                if (_ready) return;
                await _connection.SetNotificationsAsync(ServiceUuid, MtuUuid, true, cancellationToken).ConfigureAwait(false);
                try { await _connection.SetNotificationsAsync(ServiceUuid, D2hUuid, true, cancellationToken).ConfigureAwait(false); }
                catch
                {
                    try { await _connection.SetNotificationsAsync(ServiceUuid, MtuUuid, false, CancellationToken.None).ConfigureAwait(false); } catch { }
                    throw;
                }
                _ready = true;
            }
            finally { _operations.Release(); }
        }

        public Task<byte[]> RequestAsync(byte[] operation, CancellationToken cancellationToken = default, IProgress<long>? progress = null)
            => ExchangeAsync(PftpFraming.Request(operation), cancellationToken, progress, false, 0);
        public Task<byte[]> QueryAsync(ushort id, byte[]? parameters = null, CancellationToken cancellationToken = default)
            => ExchangeAsync(PftpFraming.Query(id, parameters), cancellationToken, null, false, 0);
        public async Task WriteAsync(byte[] operation, byte[] data, CancellationToken cancellationToken = default, IProgress<long>? progress = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            await ExchangeAsync(PftpFraming.Request(operation, data), cancellationToken, progress, true, operation.Length + 2).ConfigureAwait(false);
        }

        private async Task<byte[]> ExchangeAsync(byte[] message, CancellationToken cancellationToken, IProgress<long>? progress, bool write, int headerBytes)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            try { await _operations.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw LifetimeFailure(); }
            bool started = false;
            try
            {
                EnsureReady();
                lock (_gate) { _packets.Clear(); while (_received.Wait(0)) { } _pending = true; }
                int sent = 0, index = 0;
                foreach (var packet in PftpFraming.Fragment(message, Math.Min(_connection.MaxWriteSize, 512)))
                {
                    started = true;
                    // Periodic ATT acknowledgements bound the controller queue, as in the upstream writer.
                    using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    writeTimeout.CancelAfter(PacketTimeout);
                    await _connection.WriteAsync(ServiceUuid, MtuUuid, packet, ++index % 5 == 0 || (packet[0] & 6) == 2, writeTimeout.Token).ConfigureAwait(false);
                    sent += packet.Length - 1;
                    if (write) progress?.Report(Math.Max(0, sent - headerBytes));
                    // Devices may reject a PUT before all its data has been sent.
                    lock (_gate)
                    {
                        if (write && _packets.Count > 0 && sent < message.Length)
                        {
                            var response = _packets.Peek();
                            if (response.Length == 3 && ((response[0] >> 1) & 3) == 0 && (response[1] != 0 || response[2] != 0))
                                throw new PftpResponseException((ushort)(response[1] | (response[2] << 8)));
                            throw new PolarProtocolException("Device interrupted a PFTP write.");
                        }
                    }
                }
                using var assembler = new PftpAssembler(MaximumResponseBytes);
                while (true)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    timeout.CancelAfter(PacketTimeout);
                    await _received.WaitAsync(timeout.Token).ConfigureAwait(false);
                    byte[] packet; lock (_gate) { packet = _packets.Dequeue(); }
                    bool complete = assembler.Add(packet);
                    if (!write) progress?.Report(assembler.Length);
                    if (complete) return assembler.GetResult();
                }
            }
            catch (PftpResponseException) { throw; }
            catch (Exception ex) when (started)
            {
                // RFC76 has no transaction ID. Do not accept a late response as the next request's result.
                _faulted = true; _ready = false;
                if (_connection.IsConnected)
                {
                    using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try { await _connection.WriteAsync(ServiceUuid, MtuUuid, new byte[3], true, cancelTimeout.Token).ConfigureAwait(false); } catch { }
                }
                if (_lifetime.IsCancellationRequested || !_connection.IsConnected) throw LifetimeFailure();
                if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException("PFTP packet timed out; reconnect before retrying.", ex);
                throw;
            }
            finally { lock (_gate) { _pending = false; } _operations.Release(); }
        }

        public async Task SendNotificationAsync(byte id, byte[]? parameters = null, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _operations.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                EnsureReady();
                foreach (var packet in PftpFraming.Fragment(PftpFraming.Notification(id, parameters), Math.Min(_connection.MaxWriteSize, 512)))
                    await _connection.WriteAsync(ServiceUuid, H2dUuid, packet, false, linked.Token).ConfigureAwait(false);
            }
            finally { _operations.Release(); }
        }

        private void OnNotification(object? sender, BleNotification args)
        {
            if (_disposed || args.ServiceUuid != ServiceUuid) return;
            if (args.CharacteristicUuid == MtuUuid)
            {
                lock (_gate)
                {
                    if (!_pending) return;
                    if (_packets.Count >= 4096) { _faulted = true; _lifetime.Cancel(); return; }
                    _packets.Enqueue((byte[])args.Data.Clone()); _received.Release();
                }
                return;
            }
            if (args.CharacteristicUuid != D2hUuid) return;
            PftpNotification? notification = null;
            try
            {
                lock (_gate)
                {
                    _notification ??= new PftpAssembler(1024 * 1024);
                    if (_notification.Add(args.Data))
                    {
                        var data = _notification.GetResult(); _notification.Dispose(); _notification = null;
                        if (data.Length == 0) throw new PolarProtocolException("Empty PFTP notification.");
                        var parameters = new byte[data.Length - 1]; Buffer.BlockCopy(data, 1, parameters, 0, parameters.Length);
                        notification = new PftpNotification(data[0], parameters);
                    }
                }
                if (notification != null) NotificationReceived?.Invoke(this, notification);
            }
            catch (Exception ex)
            {
                lock (_gate) { _notification?.Dispose(); _notification = null; }
                NotificationError?.Invoke(this, ex);
            }
        }
        private void OnDisconnected(object? sender, EventArgs args) { _ready = false; _lifetime.Cancel(); }
        private void OnTransportError(object? sender, BleTransportError error)
        { _transportFailure = error.Exception; _faulted = true; _ready = false; _lifetime.Cancel(); }
        private PolarException LifetimeFailure() => _transportFailure == null
            ? new PolarDisconnectedException(_connection.DeviceId)
            : new PolarException("PFTP BLE transport failed.", _transportFailure);
        private void CheckConnection()
        { if (_disposed) throw new ObjectDisposedException(nameof(PftpClient)); if (!_connection.IsConnected) throw new PolarDisconnectedException(_connection.DeviceId); }
        private void EnsureReady()
        { CheckConnection(); if (!_ready || _faulted) throw new PolarFeatureNotAvailableException("PFTP (initialize, or reconnect after a failed transfer)"); }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; _ready = false;
            _connection.NotificationReceived -= OnNotification; _connection.Disconnected -= OnDisconnected;
            _connection.ErrorOccurred -= OnTransportError;
            _lifetime.Cancel();
            lock (_gate) { _notification?.Dispose(); _notification = null; }
            // Semaphores remain valid until any already-running operation has unwound.
        }
    }
}
