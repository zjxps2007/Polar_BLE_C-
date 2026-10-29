// Ported from Polar BLE SDK. Copyright Polar Electro Oy. See LICENSES/Polar_SDK_License.txt.
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Protocol.Pmd;
using PolarBle.Transport;

namespace PolarBle.Protocol.Telemetry
{
    public sealed class MemfaultConfiguration
    {
        public uint SupportedFeatures { get; } public string DeviceIdentifier { get; }
        public string DataUri { get; }
        /// <summary>Secret header provided by device. Do not log it or send it except to an explicitly approved ingestion service.</summary>
        public string Authorization { get; }
        internal MemfaultConfiguration(uint features, string id, string uri, string authorization)
        { SupportedFeatures = features; DeviceIdentifier = id; DataUri = uri; Authorization = authorization; }
    }
    public sealed class MemfaultChunk : EventArgs
    {
        public byte Sequence { get; } public int DroppedChunks { get; } public byte[] Payload { get; }
        public MemfaultConfiguration Configuration { get; }
        internal MemfaultChunk(byte sequence, int dropped, byte[] payload, MemfaultConfiguration configuration)
        { Sequence = sequence; DroppedChunks = dropped; Payload = payload; Configuration = configuration; }
    }
    /// <summary>Experimental Polar/Memfault diagnostic BLE export. This class does not upload data.</summary>
    public sealed class MemfaultClient : IDisposable
    {
        public static readonly Guid ServiceUuid = new Guid("54220000-f6a5-4007-a371-722f4ebd8436");
        public static readonly Guid FeaturesUuid = new Guid("54220001-f6a5-4007-a371-722f4ebd8436");
        public static readonly Guid DeviceIdentifierUuid = new Guid("54220002-f6a5-4007-a371-722f4ebd8436");
        public static readonly Guid DataUriUuid = new Guid("54220003-f6a5-4007-a371-722f4ebd8436");
        public static readonly Guid AuthorizationUuid = new Guid("54220004-f6a5-4007-a371-722f4ebd8436");
        public static readonly Guid DataExportUuid = new Guid("54220005-f6a5-4007-a371-722f4ebd8436");
        private readonly IBleConnection _connection; private readonly SemaphoreSlim _serial = new SemaphoreSlim(1, 1);
        private readonly object _gate = new object(); private bool _disposed, _streaming; private int _lastSequence = -1;
        private MemfaultConfiguration? _configuration;
        public event EventHandler<MemfaultChunk>? DataReceived;
        public event EventHandler<PmdErrorEventArgs>? ErrorOccurred;
        public MemfaultClient(IBleConnection connection)
        { _connection = connection; connection.NotificationReceived += OnNotification; connection.Disconnected += OnDisconnected; }
        public async Task<MemfaultConfiguration> ReadConfigurationAsync(CancellationToken cancellationToken = default)
        {
            Check();
            // Re-read each call: device consent may have changed since a failed read.
            var featureBytes = await _connection.ReadAsync(ServiceUuid, FeaturesUuid, cancellationToken).ConfigureAwait(false);
            if (featureBytes.Length < 1 || featureBytes.Length > 4) throw new PolarProtocolException("Invalid MDS supported-features length.");
            uint features = (uint)PmdBinary.Unsigned(featureBytes, 0, featureBytes.Length);
            string id = await ReadTextAsync(DeviceIdentifierUuid, cancellationToken).ConfigureAwait(false);
            string uri = await ReadTextAsync(DataUriUuid, cancellationToken).ConfigureAwait(false);
            string auth = await ReadTextAsync(AuthorizationUuid, cancellationToken).ConfigureAwait(false);
            var configuration = new MemfaultConfiguration(features, id, uri, auth); lock (_gate) _configuration = configuration;
            return configuration;
        }
        private async Task<string> ReadTextAsync(Guid characteristic, CancellationToken token)
            => new UTF8Encoding(false, true).GetString(await _connection.ReadAsync(ServiceUuid, characteristic, token).ConfigureAwait(false));
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Check(); if (_streaming) return;
                await ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);
                await _connection.SetNotificationsAsync(ServiceUuid, DataExportUuid, true, cancellationToken).ConfigureAwait(false);
                lock (_gate) { _lastSequence = -1; _streaming = true; }
                try { await _connection.WriteAsync(ServiceUuid, DataExportUuid, new byte[] { 1 }, true, cancellationToken).ConfigureAwait(false); }
                catch
                {
                    lock (_gate) _streaming = false;
                    try { await _connection.SetNotificationsAsync(ServiceUuid, DataExportUuid, false, CancellationToken.None).ConfigureAwait(false); } catch { }
                    throw;
                }
            }
            finally { _serial.Release(); }
        }
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Check(); await _connection.WriteAsync(ServiceUuid, DataExportUuid, new byte[] { 0 }, true, cancellationToken).ConfigureAwait(false);
                lock (_gate) _streaming = false;
                await _connection.SetNotificationsAsync(ServiceUuid, DataExportUuid, false, cancellationToken).ConfigureAwait(false);
            }
            finally { _serial.Release(); }
        }
        private void OnNotification(object? sender, BleNotification args)
        {
            if (_disposed || args.ServiceUuid != ServiceUuid || args.CharacteristicUuid != DataExportUuid) return;
            try
            {
                MemfaultChunk chunk;
                lock (_gate)
                {
                    if (!_streaming || _configuration == null) return;
                    PmdBinary.Require(args.Data, 0, 1); byte sequence = (byte)(args.Data[0] & 31);
                    if (sequence == _lastSequence) return;
                    int dropped = _lastSequence < 0 ? 0 : (sequence - _lastSequence - 1 + 32) % 32;
                    _lastSequence = sequence;
                    chunk = new MemfaultChunk(sequence, dropped, PmdBinary.Slice(args.Data, 1, args.Data.Length - 1), _configuration);
                }
                DataReceived?.Invoke(this, chunk);
            }
            catch (Exception error) { ErrorOccurred?.Invoke(this, new PmdErrorEventArgs(error)); }
        }
        private void OnDisconnected(object? sender, EventArgs args)
        { lock (_gate) { _streaming = false; _lastSequence = -1; _configuration = null; } }
        private void Check()
        { if (_disposed) throw new ObjectDisposedException(nameof(MemfaultClient)); if (!_connection.IsConnected) throw new PolarDisconnectedException(_connection.DeviceId); }
        public void Dispose()
        { if (_disposed) return; _disposed = true; _connection.NotificationReceived -= OnNotification; _connection.Disconnected -= OnDisconnected; lock (_gate) { _configuration = null; _streaming = false; } }
    }
}
