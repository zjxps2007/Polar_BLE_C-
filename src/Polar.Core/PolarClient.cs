using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Protocol.HeartRate;
using PolarBle.Transport;

namespace PolarBle
{
    public sealed class PolarSessionEvent : EventArgs
    { public PolarDeviceSession Session { get; } public PolarSessionEvent(PolarDeviceSession session) { Session = session; } }
    public sealed class PolarHrBroadcastData : EventArgs
    {
        public BleDevice Device { get; }
        public PolarHeartRateAdvertisement Advertisement { get; }
        public int HeartRate => Advertisement.SlowAverageHeartRate;
        public bool BatteryOk => Advertisement.BatteryOk;
        public PolarHrBroadcastData(BleDevice device, PolarHeartRateAdvertisement advertisement) { Device = device; Advertisement = advertisement; }
    }
    /// <summary>One client per process. Session identifiers are transport IDs returned during discovery.</summary>
    public sealed partial class PolarClient : IDisposable
    {
        private readonly IBleTransport _transport;
        private readonly object _gate = new object();
        private readonly Dictionary<string, PolarDeviceSession> _sessions = new Dictionary<string, PolarDeviceSession>();
        private readonly Dictionary<string, ConnectionRequest> _requests = new Dictionary<string, ConnectionRequest>();
        private readonly Dictionary<string, ReconnectOperation> _reconnecting = new Dictionary<string, ReconnectOperation>();
        private readonly Dictionary<string, int> _hrCounters = new Dictionary<string, int>();
        private readonly SemaphoreSlim _connect = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private bool _disposed, _automaticReconnection;
        public bool AutomaticReconnection
        {
            get { lock (_gate) return _automaticReconnection; }
            set
            {
                ReconnectOperation[] cancel;
                lock (_gate) { _automaticReconnection = value; cancel = value ? Array.Empty<ReconnectOperation>() : _reconnecting.Values.ToArray(); }
                foreach (var operation in cancel) operation.Cancel();
            }
        }
        public bool PolarOnly { get; set; } = true;
        public event EventHandler<BleDevice>? DeviceDiscovered;
        /// <summary>Raised while scanning for a new Polar HR advertising frame counter. No GATT connection is required.</summary>
        public event EventHandler<PolarHrBroadcastData>? HrBroadcastReceived;
        public event EventHandler<BleTransportError>? ErrorOccurred;
        /// <summary>Also raised after reconnection. Subscribe again to streams on the new Session.</summary>
        public event EventHandler<PolarSessionEvent>? SessionConnected;
        public PolarClient(IBleTransport transport)
        { _transport = transport ?? throw new ArgumentNullException(nameof(transport)); _transport.DeviceDiscovered += OnDiscovered; _transport.ErrorOccurred += OnError; InitializeRadioState(); }
        public Task StartScanAsync(CancellationToken token = default) { ThrowIfDisposed(); return _transport.StartScanAsync(token); }
        public Task StopScanAsync(CancellationToken token = default) { ThrowIfDisposed(); return _transport.StopScanAsync(token); }
        public Task PairAsync(string deviceId, CancellationToken cancellationToken = default)
        { ThrowIfDisposed(); return _transport is IPairingBleTransport pairing ? pairing.PairAsync(deviceId, cancellationToken) : throw new PlatformNotSupportedException("This BLE transport does not expose pairing."); }
        public Task<PolarDeviceSession> ConnectAsync(string deviceId, PolarServices services = PolarServices.HeartRate | PolarServices.DeviceInformation | PolarServices.Battery,
            bool h10FileSystem = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("A device identifier is required.", nameof(deviceId));
            cancellationToken.ThrowIfCancellationRequested();
            ConnectionRequest request;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_requests.TryGetValue(deviceId, out var current))
                {
                    if (current.H10 != h10FileSystem) throw new ArgumentException("The file-system type cannot change within a connection request. Disconnect before changing h10FileSystem.", nameof(h10FileSystem));
                    request = current; request.Services |= services;
                }
                else { request = new ConnectionRequest(services, h10FileSystem); _requests.Add(deviceId, request); }
            }
            return ConnectCoreAsync(deviceId, request, cancellationToken);
        }
        private async Task<PolarDeviceSession> ConnectCoreAsync(string deviceId, ConnectionRequest request, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, request.Cancellation.Token);
            await _connect.WaitAsync(linked.Token).ConfigureAwait(false);
            PolarDeviceSession result;
            try
            {
                PolarDeviceSession? existing; PolarServices services;
                lock (_gate) { EnsureCurrentRequest(deviceId, request); services = request.Services; _sessions.TryGetValue(deviceId, out existing); }
                if (existing?.IsConnected == true)
                {
                    await existing.InitializeAsync(services, linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    lock (_gate) EnsureCurrentRequest(deviceId, request);
                    return existing;
                }
                existing?.Dispose();
                var connection = await _transport.ConnectAsync(deviceId, linked.Token).ConfigureAwait(false);
                // A platform operation may finish after cancellation; never publish or retain that late connection.
                if (linked.IsCancellationRequested) { connection.Dispose(); linked.Token.ThrowIfCancellationRequested(); }
                var session = new PolarDeviceSession(connection, request.H10);
                session.Disconnected += (_, __) => ScheduleReconnect(deviceId, session, request);
                try
                {
                    await session.InitializeAsync(services, linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        EnsureCurrentRequest(deviceId, request);
                        if (!session.IsConnected) throw new PolarDisconnectedException(deviceId);
                        _sessions[deviceId] = session;
                    }
                    result = session;
                }
                catch { session.Dispose(); throw; }
            }
            finally { _connect.Release(); }
            // Do not hold the connect semaphore while invoking application code.
            lock (_gate) { if (!_requests.TryGetValue(deviceId, out var current) || !ReferenceEquals(current, request) || !result.IsConnected) return result; }
            try { SessionConnected?.Invoke(this, new PolarSessionEvent(result)); }
            catch (Exception error) { try { ErrorOccurred?.Invoke(this, new BleTransportError("session connected callback", error)); } catch { } }
            return result;
        }
        public async Task<PolarDeviceSession> AutoConnectAsync(Func<BleDevice, bool> predicate, TimeSpan timeout,
            PolarServices services = PolarServices.HeartRate | PolarServices.DeviceInformation | PolarServices.Battery, bool h10FileSystem = false, CancellationToken cancellationToken = default)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token); deadline.CancelAfter(timeout);
            var found = new TaskCompletionSource<BleDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? _, BleDevice device) { try { if (predicate(device)) found.TrySetResult(device); } catch (Exception e) { found.TrySetException(e); } }
            void ErrorHandler(object? _, BleTransportError error) => found.TrySetException(error.Exception);
            DeviceDiscovered += Handler; ErrorOccurred += ErrorHandler;
            using var cancel = deadline.Token.Register(() => found.TrySetCanceled(deadline.Token));
            try
            {
                await StartScanAsync(deadline.Token).ConfigureAwait(false);
                var device = await found.Task.ConfigureAwait(false);
                await StopScanAsync(CancellationToken.None).ConfigureAwait(false);
                return await ConnectAsync(device.Id, services, h10FileSystem, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            { throw new TimeoutException("Polar device discovery/connection timed out.", ex); }
            finally { DeviceDiscovered -= Handler; ErrorOccurred -= ErrorHandler; if (!_disposed) await StopScanAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        public async Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            PolarDeviceSession? session; ConnectionRequest? request;
            lock (_gate) { _requests.TryGetValue(deviceId, out request); _requests.Remove(deviceId); _sessions.TryGetValue(deviceId, out session); _sessions.Remove(deviceId); }
            // Invalidate before cancelling: even transports that ignore cancellation cannot restore this request.
            request?.Cancellation.Cancel();
            if (session != null) await session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        private void OnDiscovered(object? sender, BleDevice device)
        {
            if (_disposed || (PolarOnly && !device.Name.StartsWith("Polar ", StringComparison.OrdinalIgnoreCase) && !device.ManufacturerData.ContainsKey(PolarHeartRateAdvertisement.CompanyId))) return;
            PolarHeartRateAdvertisement? advertisement = null;
            try { advertisement = PolarHeartRateAdvertisement.ParseManufacturerData(device.ManufacturerData); }
            catch (PolarProtocolException error) { ErrorOccurred?.Invoke(this, new BleTransportError("decode HR advertisement", error)); }
            bool updated = false;
            lock (_gate)
            {
                if (_disposed) return;
                if (advertisement == null) _hrCounters.Remove(device.Id);
                else
                {
                    updated = !_hrCounters.TryGetValue(device.Id, out var previous) || previous != advertisement.FrameCounter;
                    _hrCounters[device.Id] = advertisement.FrameCounter;
                }
            }
            DeviceDiscovered?.Invoke(this, device);
            if (updated && advertisement != null) HrBroadcastReceived?.Invoke(this, new PolarHrBroadcastData(device, advertisement));
        }
        private void OnError(object? sender, BleTransportError error) => ErrorOccurred?.Invoke(this, error);
        private void EnsureCurrentRequest(string deviceId, ConnectionRequest request)
        {
            ThrowIfDisposed();
            if (!_requests.TryGetValue(deviceId, out var current) || !ReferenceEquals(current, request))
                throw new OperationCanceledException("Connection request was disconnected or superseded.", request.Cancellation.Token);
        }
        private void ScheduleReconnect(string deviceId, PolarDeviceSession session, ConnectionRequest request)
        {
            ReconnectOperation operation;
            lock (_gate)
            {
                if (_disposed || !_automaticReconnection || !_requests.TryGetValue(deviceId, out var current) || !ReferenceEquals(current, request) ||
                    !_sessions.TryGetValue(deviceId, out var active) || !ReferenceEquals(active, session)) return;
                if (_reconnecting.TryGetValue(deviceId, out var previous) && ReferenceEquals(previous.Request, request) && !previous.Cancellation.IsCancellationRequested) return;
                operation = new ReconnectOperation(request, _lifetime.Token); _reconnecting[deviceId] = operation;
            }
            _ = ReconnectLoopAsync(deviceId, operation);
        }
        private async Task ReconnectLoopAsync(string deviceId, ReconnectOperation operation)
        {
            try
            {
                int attempt = 0;
                while (!operation.Cancellation.IsCancellationRequested && AutomaticReconnection)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, 1 + attempt++ * 2)), operation.Cancellation.Token).ConfigureAwait(false);
                    lock (_gate) { if (!_automaticReconnection) return; EnsureCurrentRequest(deviceId, operation.Request); }
                    try { await ConnectCoreAsync(deviceId, operation.Request, operation.Cancellation.Token).ConfigureAwait(false); return; }
                    catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested) { return; }
                    catch (Exception error) { ErrorOccurred?.Invoke(this, new BleTransportError("reconnect", error)); }
                }
            }
            catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested) { }
            catch (Exception error) { try { ErrorOccurred?.Invoke(this, new BleTransportError("reconnect loop", error)); } catch { } }
            finally
            {
                PolarDeviceSession? disconnectedAgain = null;
                lock (_gate)
                {
                    if (_reconnecting.TryGetValue(deviceId, out var current) && ReferenceEquals(current, operation)) _reconnecting.Remove(deviceId);
                    // A replacement may disconnect while this loop still owns its scheduling slot.
                    if (!_disposed && _automaticReconnection && _requests.TryGetValue(deviceId, out var request) && ReferenceEquals(request, operation.Request) &&
                        _sessions.TryGetValue(deviceId, out var active) && !active.IsConnected) disconnectedAgain = active;
                }
                operation.Cancellation.Dispose();
                if (disconnectedAgain != null) ScheduleReconnect(deviceId, disconnectedAgain, operation.Request);
            }
        }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(PolarClient)); }
        public void Dispose()
        {
            PolarDeviceSession[] sessions; ConnectionRequest[] requests;
            lock (_gate) { if (_disposed) return; _disposed = true; sessions = _sessions.Values.ToArray(); requests = _requests.Values.ToArray(); _sessions.Clear(); _requests.Clear(); _hrCounters.Clear(); }
            _lifetime.Cancel(); foreach (var request in requests) request.Cancellation.Cancel();
            _transport.DeviceDiscovered -= OnDiscovered; _transport.ErrorOccurred -= OnError;
            DisposeRadioState();
            foreach (var session in sessions) session.Dispose(); _transport.Dispose();
        }
        private sealed class ConnectionRequest
        {
            public PolarServices Services; public bool H10 { get; }
            // Kept alive by in-flight operations; cancellation is safe while their linked tokens unwind.
            public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();
            public ConnectionRequest(PolarServices services, bool h10) { Services = services; H10 = h10; }
        }
        private sealed class ReconnectOperation
        {
            public ConnectionRequest Request { get; }
            public CancellationTokenSource Cancellation { get; }
            public ReconnectOperation(ConnectionRequest request, CancellationToken lifetime)
            { Request = request; Cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation.Token, lifetime); }
            public void Cancel() { try { Cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        }
    }
}
