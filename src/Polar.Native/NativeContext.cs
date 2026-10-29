using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Transport;
using PolarBle.Windows;

namespace PolarBle.Native
{
    internal sealed record NativeEvent(int Kind, long RequestId, long ConnectionId, byte[] Payload);

    internal sealed class NativeContext : IDisposable
    {
        private const int MaxQueuedBytes = 32 * 1024 * 1024;
        private const int MaxQueuedEvents = 32768;
        private readonly IBleTransport _transport;
        private readonly ConcurrentDictionary<long, IBleConnection> _connections = new();
        private readonly ConcurrentDictionary<long, CancellationTokenSource> _requests = new();
        private readonly Queue<NativeEvent> _events = new();
        private readonly object _eventGate = new();
        private long _nextConnection;
        private int _queuedBytes;
        private bool _overflow;
        private NativeEvent? _overflowEvent;
        private int _disposed;

        internal NativeContext() : this(new WindowsBleTransport()) { }

        internal NativeContext(IBleTransport transport)
        {
            _transport = transport;
            _transport.DeviceDiscovered += (_, device) => Enqueue(new NativeEvent(3, 0, 0, Wire.Encode(w =>
            {
                Wire.Text(w, device.Id); Wire.Text(w, device.Name); w.Write(device.Address); w.Write(device.Rssi);
                w.Write(device.ManufacturerData.Count);
                foreach (var pair in device.ManufacturerData) { w.Write(pair.Key); Wire.Bytes(w, pair.Value); }
                w.Write(device.ServiceUuids.Count);
                foreach (var uuid in device.ServiceUuids) w.Write(uuid.ToByteArray());
            })));
            _transport.ErrorOccurred += (_, error) => Enqueue(ErrorEvent(6, 0, 0, error.Exception, error.Operation), true);
            if (_transport is IBleRadioStateProvider radio)
                radio.RadioStateChanged += (_, change) => Enqueue(new NativeEvent(8, 0, 0, Wire.Encode(w => w.Write((int)change.State))));
        }

        internal void Submit(long requestId, int operation, long connectionId, byte[] payload)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
            if (operation < 1 || operation > 10) throw new ArgumentOutOfRangeException(nameof(operation));
            lock (_eventGate)
                if (_events.Count >= MaxQueuedEvents || _queuedBytes >= MaxQueuedBytes || _requests.Count >= 1024)
                    throw new InvalidOperationException("Native BLE queue is full. Drain events before submitting more requests.");
            var cancellation = new CancellationTokenSource();
            if (!_requests.TryAdd(requestId, cancellation))
            {
                cancellation.Dispose();
                throw new InvalidOperationException("Request ID is already active.");
            }
            _ = ExecuteAsync(requestId, operation, connectionId, payload, cancellation);
        }

        private async Task ExecuteAsync(long requestId, int operation, long connectionId, byte[] payload, CancellationTokenSource cancellation)
        {
            // Always leave the native entrypoint before running WinRT and callbacks.
            await Task.Yield();
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                using var reader = new BinaryReader(new MemoryStream(payload, false), Wire.Utf8, false);
                byte[] result = Array.Empty<byte>();
                switch (operation)
                {
                    case 1:
                        Wire.End(reader);
                        await _transport.StartScanAsync(cancellation.Token).ConfigureAwait(false);
                        break;
                    case 2:
                        Wire.End(reader);
                        await _transport.StopScanAsync(cancellation.Token).ConfigureAwait(false);
                        break;
                    case 3:
                    {
                        var deviceId = Wire.Text(reader); Wire.End(reader);
                        var connection = await _transport.ConnectAsync(deviceId, cancellation.Token).ConfigureAwait(false);
                        if (Volatile.Read(ref _disposed) != 0 || cancellation.IsCancellationRequested)
                        { connection.Dispose(); cancellation.Token.ThrowIfCancellationRequested(); throw new ObjectDisposedException(nameof(NativeContext)); }
                        connectionId = Interlocked.Increment(ref _nextConnection);
                        var newId = connectionId;
                        var disconnectGate = new object();
                        bool disconnectReported = false;
                        void ReportDisconnection()
                        {
                            lock (disconnectGate)
                            {
                                if (disconnectReported) return;
                                disconnectReported = true;
                                _connections.TryRemove(newId, out _);
                                Enqueue(new NativeEvent(5, 0, newId, Array.Empty<byte>()), true);
                            }
                        }
                        connection.NotificationReceived += (_, notification) => Enqueue(new NativeEvent(4, 0, newId, Wire.Encode(w =>
                        { w.Write(notification.ServiceUuid.ToByteArray()); w.Write(notification.CharacteristicUuid.ToByteArray()); Wire.Bytes(w, notification.Data); })));
                        connection.Disconnected += (_, _) => ReportDisconnection();
                        try
                        {
                            // Transport forwards connection callback errors, so no second subscription here.
                            _connections[newId] = connection;
                            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(NativeContext));
                            if (!connection.IsConnected) throw new PolarDisconnectedException(deviceId);
                            result = Wire.Encode(w => w.Write(connection.MaxWriteSize));
                        }
                        catch
                        {
                            _connections.TryRemove(newId, out _);
                            try { connection.Dispose(); }
                            finally
                            {
                                // A failed connect has one disconnect event before its error
                                // completion, even if the OS callback is delayed or never arrives.
                                ReportDisconnection();
                            }
                            throw;
                        }
                        break;
                    }
                    case 4:
                    {
                        Wire.End(reader);
                        var connection = Connection(connectionId);
                        var services = await connection.DiscoverServicesAsync(cancellation.Token).ConfigureAwait(false);
                        result = Wire.Encode(w =>
                        {
                            w.Write(connection.MaxWriteSize); w.Write(services.Count);
                            foreach (var service in services)
                            {
                                w.Write(service.Uuid.ToByteArray()); w.Write(service.Characteristics.Count);
                                foreach (var characteristic in service.Characteristics)
                                { w.Write(characteristic.Uuid.ToByteArray()); w.Write((int)characteristic.Properties); }
                            }
                        });
                        break;
                    }
                    case 5:
                    {
                        var service = Wire.Guid(reader); var characteristic = Wire.Guid(reader); Wire.End(reader);
                        result = await Connection(connectionId).ReadAsync(service, characteristic, cancellation.Token).ConfigureAwait(false);
                        break;
                    }
                    case 6:
                    {
                        var service = Wire.Guid(reader); var characteristic = Wire.Guid(reader);
                        var withResponse = reader.ReadByte(); if (withResponse > 1) throw new InvalidDataException("Invalid boolean.");
                        var bytes = Wire.Bytes(reader); Wire.End(reader);
                        await Connection(connectionId).WriteAsync(service, characteristic, bytes, withResponse != 0, cancellation.Token).ConfigureAwait(false);
                        break;
                    }
                    case 7:
                    {
                        var service = Wire.Guid(reader); var characteristic = Wire.Guid(reader);
                        var enabled = reader.ReadByte(); if (enabled > 1) throw new InvalidDataException("Invalid boolean."); Wire.End(reader);
                        await Connection(connectionId).SetNotificationsAsync(service, characteristic, enabled != 0, cancellation.Token).ConfigureAwait(false);
                        break;
                    }
                    case 8:
                        Wire.End(reader);
                        if (_connections.TryRemove(connectionId, out var closing))
                        {
                            try { await closing.DisconnectAsync(cancellation.Token).ConfigureAwait(false); }
                            finally { closing.Dispose(); }
                        }
                        break;
                    case 9:
                    {
                        var deviceId = Wire.Text(reader); Wire.End(reader);
                        if (_transport is not IPairingBleTransport pairing)
                            throw new PolarFeatureNotAvailableException("BLE pairing");
                        await pairing.PairAsync(deviceId, cancellation.Token).ConfigureAwait(false);
                        break;
                    }
                    case 10:
                    {
                        Wire.End(reader);
                        if (_transport is not IBleRadioStateProvider radio)
                            throw new PolarFeatureNotAvailableException("Bluetooth radio state");
                        var state = await radio.GetRadioStateAsync(cancellation.Token).ConfigureAwait(false);
                        result = Wire.Encode(w => w.Write((int)state));
                        break;
                    }
                }
                Enqueue(new NativeEvent(1, requestId, connectionId, result), true);
            }
            catch (Exception ex) { Enqueue(ErrorEvent(2, requestId, connectionId, ex, "operation " + operation), true); }
            finally
            {
                _requests.TryRemove(requestId, out _);
                cancellation.Dispose();
            }
        }

        internal int MaxWriteSize(long connectionId) => Connection(connectionId).MaxWriteSize;

        private IBleConnection Connection(long id)
        {
            if (!_connections.TryGetValue(id, out var connection)) throw new InvalidOperationException("Unknown or disconnected connection handle.");
            return connection;
        }

        internal bool Cancel(long requestId)
        {
            if (!_requests.TryGetValue(requestId, out var source)) return false;
            try { source.Cancel(); return true; } catch (ObjectDisposedException) { return false; }
        }

        private static NativeEvent ErrorEvent(int kind, long requestId, long connectionId, Exception exception, string operation)
            => new(kind, requestId, connectionId, Wire.Encode(w =>
            {
                // Error category 1 is cancellation; 2 is disconnected; 3 is all other failures.
                w.Write(exception is OperationCanceledException ? 1 : exception is PolarDisconnectedException ? 2 : 3);
                Wire.Text(w, operation); Wire.Text(w, exception.GetType().FullName ?? exception.GetType().Name); Wire.Text(w, exception.Message);
            }));

        private void Enqueue(NativeEvent item, bool completion = false)
        {
            lock (_eventGate)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                // Completions must not be discarded or a managed await would never finish.
                if (!completion && (_queuedBytes + item.Payload.Length > MaxQueuedBytes || _events.Count >= MaxQueuedEvents))
                { _overflow = true; return; }
                _events.Enqueue(item);
                _queuedBytes += item.Payload.Length;
            }
        }

        internal NativeEvent? Peek()
        {
            lock (_eventGate)
            {
                if (_overflow)
                {
                    _overflowEvent ??= ErrorEvent(6, 0, 0, new IOException("Native BLE event queue overflowed. Samples were lost; drain events more frequently."), "event queue");
                    return _overflowEvent;
                }
                return _events.Count > 0 ? _events.Peek() : null;
            }
        }

        internal void Dequeue(NativeEvent expected)
        {
            lock (_eventGate)
            {
                if (ReferenceEquals(_overflowEvent, expected)) { _overflowEvent = null; _overflow = false; }
                else if (_events.Count > 0 && ReferenceEquals(_events.Peek(), expected))
                { _events.Dequeue(); _queuedBytes -= expected.Payload.Length; }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var source in _requests.Values) { try { source.Cancel(); } catch (ObjectDisposedException) { } }
            foreach (var connection in _connections.Values) connection.Dispose();
            _connections.Clear();
            _transport.Dispose();
            lock (_eventGate) { _events.Clear(); _queuedBytes = 0; }
            // In-flight request continuations own and dispose their CancellationTokenSources.
        }
    }
}
