using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Protocol.HeartRate;
using PolarBle.Transport;
using UnityEngine;

namespace PolarBle.Unity
{
    /// <summary>Optional Unity lifecycle host. Exposes main-thread discovery/HR/error events.
    /// Other protocol client events are worker-thread events; dispatch Unity API work yourself.</summary>
    public sealed class PolarUnityClient : MonoBehaviour
    {
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private CancellationTokenSource _lifetime;
        private PolarClient _client;
        private int _queued, _overflow;
        public PolarClient Client => _client;
        public PolarDeviceSession Session { get; private set; }
        public event Action<BleDevice> DeviceFound;
        public event Action<HeartRateData> HeartRate;
        public event Action<Exception> Error;
        private void Awake()
        {
            _lifetime = new CancellationTokenSource();
            try
            {
                _client = new PolarClient(new NativeWindowsBleTransport());
                _client.DeviceDiscovered += (_, device) => Dispatch(() => DeviceFound?.Invoke(device));
                _client.ErrorOccurred += (_, error) => Dispatch(() => Error?.Invoke(error.Exception));
            }
            catch (Exception error) { Debug.LogException(error, this); enabled = false; }
        }
        public Task ScanAsync() => _client.StartScanAsync(_lifetime.Token);
        public Task StopScanAsync() => _client.StopScanAsync(_lifetime.Token);
        public async Task<PolarDeviceSession> ConnectAsync(string transportDeviceId, bool initializeSensorStreaming = false)
        {
            var token = _lifetime.Token;
            await _client.StopScanAsync(token);
            var features = PolarServices.HeartRate | PolarServices.DeviceInformation | PolarServices.Battery;
            if (initializeSensorStreaming) features |= PolarServices.Pmd;
            var session = await _client.ConnectAsync(transportDeviceId, features, false, token);
            token.ThrowIfCancellationRequested();
            if (Session != null) Session.HeartRateReceived -= OnHeartRate;
            Session = session; session.HeartRateReceived += OnHeartRate;
            await session.StartHeartRateAsync(token);
            return session;
        }
        private void OnHeartRate(object sender, HeartRateData data) => Dispatch(() => HeartRate?.Invoke(data));
        public void Dispatch(Action action)
        {
            if (_lifetime == null || _lifetime.IsCancellationRequested) return;
            if (Interlocked.Increment(ref _queued) > 4096) { Interlocked.Decrement(ref _queued); Interlocked.Increment(ref _overflow); return; }
            _mainThread.Enqueue(action);
        }
        private void Update()
        {
            int lost = Interlocked.Exchange(ref _overflow, 0);
            if (lost != 0) { var error = new InvalidOperationException($"Unity dispatch queue overflow: {lost} callbacks rejected."); Debug.LogException(error, this); Error?.Invoke(error); }
            for (int i = 0; i < 256 && _mainThread.TryDequeue(out var action); i++)
            { Interlocked.Decrement(ref _queued); try { action(); } catch (Exception error) { Debug.LogException(error, this); } }
        }
        private void OnDestroy()
        {
            if (_lifetime == null) return;
            _lifetime.Cancel();
            if (Session != null) Session.HeartRateReceived -= OnHeartRate;
            _client?.Dispose(); Session = null;
            while (_mainThread.TryDequeue(out _)) { }
            _lifetime.Dispose(); _lifetime = null;
        }
    }
}
