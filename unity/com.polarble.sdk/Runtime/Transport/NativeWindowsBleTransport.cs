#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN || POLAR_NATIVE_TEST
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Transport;

namespace PolarBle.Unity
{
    /// <summary>
    /// Windows native BLE transport for Unity Mono and IL2CPP. The native DLL is authored in C#
    /// and compiled using NativeAOT. Events run on the transport's worker thread; dispatch to
    /// Unity's main thread before accessing UnityEngine objects. Dispose on Play Mode exit.
    /// </summary>
    public sealed class NativeWindowsBleTransport : IBleTransport, IPairingBleTransport, IBleRadioStateProvider
    {
        private readonly object _nativeGate = new object();
        private readonly ConcurrentDictionary<long, PendingRequest> _pending = new ConcurrentDictionary<long, PendingRequest>();
        private readonly ConcurrentDictionary<long, NativeConnection> _connections = new ConcurrentDictionary<long, NativeConnection>();
        private readonly ConcurrentDictionary<long, byte> _earlyDisconnects = new ConcurrentDictionary<long, byte>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _pump;
        private long _handle;
        private long _requestSequence;
        private int _disposed;
        private byte[] _pollBuffer = new byte[4096];
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public event EventHandler<BleDevice>? DeviceDiscovered;
        public event EventHandler<BleTransportError>? ErrorOccurred;
        public event EventHandler<BleRadioStateChanged>? RadioStateChanged;

        public NativeWindowsBleTransport()
        {
            if (Native.AbiVersion() != 1) throw new PolarException("Polar.Native.dll ABI version does not match this Unity package.");
            _handle = Native.Create();
            if (_handle == 0) throw NativeError("Create native BLE transport");
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "Polar BLE events" };
            _pump.Start();
        }

        public async Task StartScanAsync(CancellationToken cancellationToken = default)
        { await RequestAsync(1, 0, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false); }

        public async Task StopScanAsync(CancellationToken cancellationToken = default)
        { await RequestAsync(2, 0, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false); }

        public async Task<BleRadioState> GetRadioStateAsync(CancellationToken cancellationToken = default)
        {
            var reply = await RequestAsync(10, 0, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
            using (var reader = Reader(reply.Data)) return ReadRadioState(reader);
        }

        public async Task PairAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("A discovered device ID is required.", nameof(deviceId));
            await RequestAsync(9, 0, Encode(w => WriteText(w, deviceId)), cancellationToken).ConfigureAwait(false);
        }

        public async Task<IBleConnection> ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("A discovered device ID is required.", nameof(deviceId));
            var reply = await RequestAsync(3, 0, Encode(w => WriteText(w, deviceId)), cancellationToken).ConfigureAwait(false);
            using (var reader = Reader(reply.Data))
            {
                var connection = new NativeConnection(this, reply.ConnectionId, deviceId, reader.ReadInt32());
                lock (_nativeGate)
                {
                    ThrowIfDisposed();
                    _connections[reply.ConnectionId] = connection;
                    if (_earlyDisconnects.TryRemove(reply.ConnectionId, out _))
                    {
                        _connections.TryRemove(reply.ConnectionId, out _);
                        connection.MarkDisconnected();
                        throw new PolarDisconnectedException(deviceId);
                    }
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    connection.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return connection;
            }
        }

        private Task<Reply> RequestAsync(int operation, long connection, byte[] data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            long id = Interlocked.Increment(ref _requestSequence);
            var request = new PendingRequest(operation);
            _pending[id] = request;
            try
            {
                lock (_nativeGate)
                {
                    ThrowIfDisposed();
                    if (Native.Submit(_handle, id, operation, connection, data, data.Length) != 0)
                        throw NativeError("Submit BLE operation " + operation);
                }
                if (cancellationToken.CanBeCanceled)
                {
                    request.SetRegistration(cancellationToken.Register(() =>
                    {
                        request.Completion.TrySetCanceled(cancellationToken);
                        lock (_nativeGate)
                        {
                            if (_handle != 0) Native.Cancel(_handle, id);
                        }
                    }));
                }
                _wake.Set();
                return request.Completion.Task;
            }
            catch (Exception ex)
            {
                _pending.TryRemove(id, out _);
                request.Finish();
                request.Completion.TrySetException(ex);
                return request.Completion.Task;
            }
        }

        private void PumpLoop()
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    int count = 0;
                    while (count++ < 2048 && TryPoll(out var reply)) Dispatch(reply);
                    if (count > 2048) Thread.Yield();
                    else _wake.WaitOne(5);
                }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _disposed) != 0) break;
                    ReportError("Poll native BLE events", ex);
                    // A malformed native event or failing poll must not leave pending tasks hung.
                    foreach (var pair in _pending)
                        if (_pending.TryRemove(pair.Key, out var pending))
                        { pending.Finish(); pending.Completion.TrySetException(ex); }
                    Dispose();
                }
            }
        }

        private bool TryPoll(out Reply reply)
        {
            lock (_nativeGate)
            {
                reply = null!;
                if (_handle == 0) return false;
                int kind, required;
                long request, connection;
                int status = Native.Poll(_handle, out kind, out request, out connection, _pollBuffer, _pollBuffer.Length, out required);
                if (status == 0) return false;
                if (status == 2)
                {
                    if (required < 0 || required > 16 * 1024 * 1024) throw new InvalidDataException("Invalid native event payload size.");
                    _pollBuffer = new byte[required];
                    status = Native.Poll(_handle, out kind, out request, out connection, _pollBuffer, _pollBuffer.Length, out required);
                }
                if (status != 1) throw NativeError("Poll BLE event");
                if (required < 0 || required > _pollBuffer.Length) throw new InvalidDataException("Native event exceeds destination buffer.");
                var data = new byte[required];
                Buffer.BlockCopy(_pollBuffer, 0, data, 0, required);
                reply = new Reply(kind, request, connection, data);
                return true;
            }
        }

        private void Dispatch(Reply reply)
        {
            switch (reply.Kind)
            {
                case 1:
                case 2:
                    if (_pending.TryRemove(reply.RequestId, out var request))
                    {
                        request.Finish();
                        if (reply.Kind == 1)
                        {
                            if (!request.Completion.TrySetResult(reply) && request.Operation == 3)
                                ForgetDisconnect(reply.ConnectionId); // Cancellation raced successful connection.
                        }
                        else
                        {
                            if (request.Operation == 3) _earlyDisconnects.TryRemove(reply.ConnectionId, out _);
                            var error = DecodeError(reply.Data, out var category, out _);
                            if (category == 1) request.Completion.TrySetCanceled();
                            else request.Completion.TrySetException(error);
                        }
                    }
                    break;
                case 3:
                    using (var reader = Reader(reply.Data))
                    {
                        string id = ReadText(reader), name = ReadText(reader);
                        ulong address = reader.ReadUInt64(); short rssi = reader.ReadInt16();
                        var manufacturer = new Dictionary<ushort, byte[]>();
                        int count = ReadCount(reader);
                        for (int i = 0; i < count; ++i) manufacturer.Add(reader.ReadUInt16(), ReadBytes(reader));
                        var services = new Guid[ReadCount(reader)];
                        for (int i = 0; i < services.Length; ++i) services[i] = ReadGuid(reader);
                        InvokeUser(() => DeviceDiscovered?.Invoke(this, new BleDevice(id, name, address, rssi, manufacturer, services)), "DeviceDiscovered");
                    }
                    break;
                case 4:
                    if (_connections.TryGetValue(reply.ConnectionId, out var recipient))
                    {
                        using (var reader = Reader(reply.Data))
                            recipient.Receive(new BleNotification(ReadGuid(reader), ReadGuid(reader), ReadBytes(reader)));
                    }
                    break;
                case 5:
                    NativeConnection? lost;
                    lock (_nativeGate)
                    {
                        if (!_connections.TryRemove(reply.ConnectionId, out lost)) _earlyDisconnects[reply.ConnectionId] = 0;
                    }
                    lost?.MarkDisconnected();
                    break;
                case 6:
                    var background = DecodeError(reply.Data, out _, out var operation);
                    ReportError(operation, background);
                    break;
                case 8:
                    using (var reader = Reader(reply.Data))
                    {
                        var state = ReadRadioState(reader);
                        InvokeUser(() => RadioStateChanged?.Invoke(this, new BleRadioStateChanged(state)), "RadioStateChanged");
                    }
                    break;
                default: throw new InvalidDataException("Unknown native event kind " + reply.Kind);
            }
        }

        private static Exception DecodeError(byte[] payload, out int category, out string operation)
        {
            using (var reader = Reader(payload))
            {
                category = reader.ReadInt32(); operation = ReadText(reader);
                string type = ReadText(reader), message = ReadText(reader);
                if (category == 1) return new OperationCanceledException(message);
                return new NativeBleException(operation, type, message);
            }
        }

        private static NativeBleException NativeError(string operation)
        {
            int length = Native.LastError(null, 0);
            if (length < 0 || length > 1024 * 1024) return new NativeBleException(operation, "NativeInterop", "No native error details available.");
            var bytes = new byte[length];
            Native.LastError(bytes, bytes.Length);
            return new NativeBleException(operation, "NativeInterop", Utf8.GetString(bytes));
        }

        private void InvokeUser(Action action, string operation)
        {
            try { action(); } catch (Exception ex) { ReportError(operation, ex); }
        }
        private void ReportError(string operation, Exception error)
        {
            try { ErrorOccurred?.Invoke(this, new BleTransportError(operation, error)); } catch { }
        }

        private async void ForgetDisconnect(long connection)
        {
            try
            {
                await RequestAsync(8, connection, Array.Empty<byte>(), CancellationToken.None).ConfigureAwait(false);
                _earlyDisconnects.TryRemove(connection, out _);
            }
            catch (Exception ex) { if (Volatile.Read(ref _disposed) == 0) ReportError("Disconnect", ex); }
        }

        private int QueryMaxWrite(long connection, int fallback)
        {
            lock (_nativeGate)
            {
                if (_handle == 0) return fallback;
                int result = Native.MaxWriteSize(_handle, connection);
                return result > 0 ? result : fallback;
            }
        }

        private void ThrowIfDisposed()
        { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(NativeWindowsBleTransport)); }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_nativeGate)
            {
                long handle = _handle; _handle = 0;
                if (handle != 0 && Native.Destroy(handle) != 0) ReportError("Dispose native BLE transport", NativeError("Dispose"));
            }
            foreach (var pair in _pending)
                if (_pending.TryRemove(pair.Key, out var pending))
                { pending.Finish(); pending.Completion.TrySetException(new ObjectDisposedException(nameof(NativeWindowsBleTransport))); }
            foreach (var connection in _connections.Values) connection.MarkDisconnected();
            _connections.Clear(); _earlyDisconnects.Clear();
            _wake.Set();
            if (Thread.CurrentThread != _pump) _pump.Join(1000);
            // The NativeAOT library remains loaded for the process lifetime; never FreeLibrary it.
        }

        private static byte[] Encode(Action<BinaryWriter> action)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Utf8, true)) action(writer);
                return stream.ToArray();
            }
        }
        private static BinaryReader Reader(byte[] bytes) => new BinaryReader(new MemoryStream(bytes, false), Utf8, false);
        private static void WriteText(BinaryWriter writer, string text) => WriteBytes(writer, Utf8.GetBytes(text));
        private static void WriteBytes(BinaryWriter writer, byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
        private static string ReadText(BinaryReader reader) => Utf8.GetString(ReadBytes(reader));
        private static byte[] ReadBytes(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid native byte count.");
            return reader.ReadBytes(count);
        }
        private static int ReadCount(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 65536) throw new InvalidDataException("Invalid native collection count.");
            return count;
        }
        private static Guid ReadGuid(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(16);
            if (bytes.Length != 16) throw new EndOfStreamException();
            return new Guid(bytes);
        }

        private static BleRadioState ReadRadioState(BinaryReader reader)
        {
            int state = reader.ReadInt32();
            if (state < 0 || state > 4) throw new InvalidDataException("Invalid Bluetooth radio state.");
            return (BleRadioState)state;
        }

        private sealed class Reply
        {
            internal readonly int Kind;
            internal readonly long RequestId, ConnectionId;
            internal readonly byte[] Data;
            internal Reply(int kind, long requestId, long connectionId, byte[] data)
            { Kind = kind; RequestId = requestId; ConnectionId = connectionId; Data = data; }
        }

        private sealed class PendingRequest
        {
            internal readonly int Operation;
            internal readonly TaskCompletionSource<Reply> Completion = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _gate = new object();
            private CancellationTokenRegistration _registration;
            private bool _finished;
            internal PendingRequest(int operation) { Operation = operation; }
            internal void SetRegistration(CancellationTokenRegistration registration)
            {
                lock (_gate)
                {
                    if (_finished) registration.Dispose();
                    else _registration = registration;
                }
            }
            internal void Finish()
            {
                CancellationTokenRegistration registration;
                lock (_gate) { _finished = true; registration = _registration; }
                registration.Dispose();
            }
        }

        private sealed class NativeConnection : IBleConnection
        {
            private readonly NativeWindowsBleTransport _owner;
            private readonly long _id;
            private int _connected = 1;
            private int _maxWrite;
            public string DeviceId { get; }
            public bool IsConnected => Volatile.Read(ref _connected) != 0;
            public int MaxWriteSize => _maxWrite = _owner.QueryMaxWrite(_id, _maxWrite);
            public event EventHandler<BleNotification>? NotificationReceived;
            public event EventHandler? Disconnected;
            public event EventHandler<BleTransportError>? ErrorOccurred;
            internal NativeConnection(NativeWindowsBleTransport owner, long id, string deviceId, int maxWrite)
            { _owner = owner; _id = id; DeviceId = deviceId; _maxWrite = maxWrite; }

            public async Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(CancellationToken cancellationToken = default)
            {
                CheckConnected();
                var reply = await _owner.RequestAsync(4, _id, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
                using (var reader = Reader(reply.Data))
                {
                    _maxWrite = reader.ReadInt32();
                    var services = new BleGattService[ReadCount(reader)];
                    for (int i = 0; i < services.Length; ++i)
                    {
                        var uuid = ReadGuid(reader);
                        var characteristics = new BleGattCharacteristic[ReadCount(reader)];
                        for (int j = 0; j < characteristics.Length; ++j)
                            characteristics[j] = new BleGattCharacteristic(ReadGuid(reader), (BleCharacteristicProperties)reader.ReadInt32());
                        services[i] = new BleGattService(uuid, characteristics);
                    }
                    return services;
                }
            }
            public async Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default)
            {
                CheckConnected();
                var reply = await _owner.RequestAsync(5, _id, Encode(w => { w.Write(serviceUuid.ToByteArray()); w.Write(characteristicUuid.ToByteArray()); }), cancellationToken).ConfigureAwait(false);
                return reply.Data;
            }
            public async Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withResponse, CancellationToken cancellationToken = default)
            {
                if (data == null) throw new ArgumentNullException(nameof(data));
                CheckConnected();
                await _owner.RequestAsync(6, _id, Encode(w =>
                { w.Write(serviceUuid.ToByteArray()); w.Write(characteristicUuid.ToByteArray()); w.Write((byte)(withResponse ? 1 : 0)); WriteBytes(w, data); }), cancellationToken).ConfigureAwait(false);
            }
            public async Task SetNotificationsAsync(Guid serviceUuid, Guid characteristicUuid, bool enabled, CancellationToken cancellationToken = default)
            {
                CheckConnected();
                await _owner.RequestAsync(7, _id, Encode(w =>
                { w.Write(serviceUuid.ToByteArray()); w.Write(characteristicUuid.ToByteArray()); w.Write((byte)(enabled ? 1 : 0)); }), cancellationToken).ConfigureAwait(false);
            }
            public async Task DisconnectAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsConnected) return;
                await _owner.RequestAsync(8, _id, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
                MarkDisconnected();
            }
            internal void Receive(BleNotification notification)
            {
                if (IsConnected) InvokeConnectionUser(() => NotificationReceived?.Invoke(this, notification), "NotificationReceived");
            }
            internal void MarkDisconnected()
            {
                if (Interlocked.Exchange(ref _connected, 0) != 0)
                    InvokeConnectionUser(() => Disconnected?.Invoke(this, EventArgs.Empty), "Disconnected");
            }
            private void InvokeConnectionUser(Action action, string operation)
            {
                try { action(); }
                catch (Exception ex)
                {
                    try { ErrorOccurred?.Invoke(this, new BleTransportError(operation, ex)); } catch { }
                    _owner.ReportError(operation, ex);
                }
            }
            private void CheckConnected() { if (!IsConnected) throw new PolarDisconnectedException(DeviceId); }
            public void Dispose()
            {
                if (!IsConnected) return;
                MarkDisconnected();
                _owner.ForgetDisconnect(_id);
            }
        }

        private static class Native
        {
            private const string Library = "Polar.Native";
            [DllImport(Library, EntryPoint = "polar_abi_version", CallingConvention = CallingConvention.Cdecl)] internal static extern int AbiVersion();
            [DllImport(Library, EntryPoint = "polar_create", CallingConvention = CallingConvention.Cdecl)] internal static extern long Create();
            [DllImport(Library, EntryPoint = "polar_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern int Destroy(long handle);
            [DllImport(Library, EntryPoint = "polar_submit", CallingConvention = CallingConvention.Cdecl)] internal static extern int Submit(long handle, long request, int operation, long connection, byte[] data, int length);
            [DllImport(Library, EntryPoint = "polar_cancel", CallingConvention = CallingConvention.Cdecl)] internal static extern int Cancel(long handle, long request);
            [DllImport(Library, EntryPoint = "polar_max_write_size", CallingConvention = CallingConvention.Cdecl)] internal static extern int MaxWriteSize(long handle, long connection);
            [DllImport(Library, EntryPoint = "polar_poll", CallingConvention = CallingConvention.Cdecl)] internal static extern int Poll(long handle, out int kind, out long request, out long connection, [Out] byte[] buffer, int capacity, out int required);
            [DllImport(Library, EntryPoint = "polar_last_error", CallingConvention = CallingConvention.Cdecl)] internal static extern int LastError([Out] byte[]? buffer, int capacity);
        }
    }

    public sealed class NativeBleException : PolarException
    {
        public string Operation { get; }
        public string NativeExceptionType { get; }
        public NativeBleException(string operation, string nativeExceptionType, string message)
            : base(operation + ": " + message) { Operation = operation; NativeExceptionType = nativeExceptionType; }
    }
}
#endif
