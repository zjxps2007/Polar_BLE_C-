using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using PolarBle.Models;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Services
{
    public enum PolarSleepRecordingStatus { Unknown, Disabled, Enabled }
    public sealed class PolarRestEvent : EventArgs
    {
        public string Json { get; }
        public Struct Data { get; }
        public PolarRestEvent(string json, Struct data) { Json = json; Data = data; }
    }

    /// <summary>REST-shaped services on the Polar device, transported over PFTP (not HTTP).</summary>
    public sealed class PolarRestService : IDisposable
    {
        private readonly PolarFileService _files;
        private readonly PftpClient _client;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public event EventHandler<PolarRestEvent>? EventReceived;
        public event EventHandler<PolarSleepStatusEvent>? SleepStatusChanged;
        public event EventHandler<Exception>? ErrorOccurred;
        public PolarRestService(PftpClient client, PolarFileService files)
        { _client = client; _files = files; _client.NotificationReceived += OnNotification; }
        public Task<Struct> ListServicesAsync(CancellationToken cancellationToken = default) => GetDescriptionAsync("/REST/SERVICE.API", cancellationToken);
        public async Task<PolarRestServices> GetServicesAsync(CancellationToken cancellationToken = default)
            => new PolarRestServices(await ListServicesAsync(cancellationToken).ConfigureAwait(false));
        public async Task<PolarRestDescription> GetServiceDescriptionAsync(string path, CancellationToken cancellationToken = default)
            => new PolarRestDescription(await GetDescriptionAsync(path, cancellationToken).ConfigureAwait(false));
        public async Task<Struct> GetDescriptionAsync(string path, CancellationToken cancellationToken = default)
            => JsonParser.Default.Parse<Struct>(StrictUtf8.GetString(await _files.ReadAsync(path, cancellationToken).ConfigureAwait(false)));
        public Task PutNotificationAsync(string path, string json, CancellationToken cancellationToken = default)
        {
            // Validate the object before sending a command to the device.
            JsonParser.Default.Parse<Struct>(json);
            return _files.WriteAsync(path, StrictUtf8.GetBytes(json), cancellationToken);
        }
        public Task SubscribeSleepStatusAsync(CancellationToken cancellationToken = default)
            => PutNotificationAsync("/REST/SLEEP.API?cmd=subscribe&event=sleep_recording_state&details=[enabled]", "{}", cancellationToken);
        public Task StopSleepRecordingAsync(CancellationToken cancellationToken = default)
            => PutNotificationAsync("/REST/SLEEP.API?cmd=post&endpoint=stop_sleep_recording", "{}", cancellationToken);
        public Task<PolarSleepRecordingStatus> GetSleepRecordingStatusAsync(CancellationToken cancellationToken = default)
            => GetSleepRecordingStatusAsync(TimeSpan.FromSeconds(30), cancellationToken);
        public async Task<bool> GetSleepRecordingStateAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => await GetSleepRecordingStatusAsync(timeout ?? TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false) == PolarSleepRecordingStatus.Enabled;
        /// <summary>Application-defined conversion avoids runtime JSON reflection and works with Unity IL2CPP/NativeAOT.</summary>
        public IDisposable ObserveEvents<T>(Func<PolarRestEvent, T> convert, System.Action<T> onNext, System.Action<Exception> onError)
        {
            if (convert == null) throw new ArgumentNullException(nameof(convert));
            if (onNext == null) throw new ArgumentNullException(nameof(onNext));
            if (onError == null) throw new ArgumentNullException(nameof(onError));
            return new RestSubscription<T>(this, convert, onNext, onError);
        }
        public async Task<PolarSleepRecordingStatus> GetSleepRecordingStatusAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            var completion = new TaskCompletionSource<PolarSleepRecordingStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? sender, PolarRestEvent args)
            {
                if (args.Data.Fields.ContainsKey("sleep_recording_state")) completion.TrySetResult(ParseSleepStatus(args.Data));
            }
            void ErrorHandler(object? sender, Exception error) => completion.TrySetException(error);
            EventReceived += Handler; ErrorOccurred += ErrorHandler;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancel.CancelAfter(timeout);
            using var registration = cancel.Token.Register(() => completion.TrySetCanceled(cancel.Token));
            try
            {
                // Attach the handler before asking the device to subscribe; first events can arrive immediately.
                await SubscribeSleepStatusAsync(cancel.Token).ConfigureAwait(false);
                return await completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("No sleep status event arrived before the deadline.", ex); }
            finally { EventReceived -= Handler; ErrorOccurred -= ErrorHandler; }
        }
        public static PolarSleepRecordingStatus ParseSleepStatus(Struct data)
        {
            if (!data.Fields.TryGetValue("sleep_recording_state", out var state) || state.KindCase != Value.KindOneofCase.StructValue)
                return PolarSleepRecordingStatus.Unknown;
            if (!state.StructValue.Fields.TryGetValue("enabled", out var enabled) || enabled.KindCase != Value.KindOneofCase.BoolValue)
                return PolarSleepRecordingStatus.Unknown;
            return enabled.BoolValue ? PolarSleepRecordingStatus.Enabled : PolarSleepRecordingStatus.Disabled;
        }
        public static IReadOnlyList<PolarRestEvent> DecodeEvents(byte[] parameters)
        {
            var notification = PbPftpDHRestApiEvent.Parser.ParseFrom(parameters);
            var output = new List<PolarRestEvent>();
            foreach (var item in notification.Event)
            {
                byte[] bytes = item.ToByteArray();
                if (!notification.Uncompressed) bytes = Decompress(bytes, 1024 * 1024);
                string json = StrictUtf8.GetString(bytes);
                output.Add(new PolarRestEvent(json, JsonParser.Default.Parse<Struct>(json)));
            }
            return output;
        }
        internal static byte[] Decompress(byte[] data, int maximumBytes)
        {
            using var input = new MemoryStream(data, false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = gzip.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (output.Length + count > maximumBytes) throw new PolarProtocolException("Compressed payload exceeds configured limit.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        private void OnNotification(object? sender, PftpNotification notification)
        {
            if (notification.Id != 18) return;
            try
            {
                var values = DecodeEvents(notification.Parameters);
                foreach (var value in values) EventReceived?.Invoke(this, value);
                var statuses = values.Where(value => value.Data.Fields.ContainsKey("sleep_recording_state")).Select(value => ParseSleepStatus(value.Data)).ToArray();
                if (statuses.Length != 0) SleepStatusChanged?.Invoke(this, new PolarSleepStatusEvent(statuses));
            }
            catch (Exception ex) { ErrorOccurred?.Invoke(this, ex); }
        }
        public void Dispose() { _client.NotificationReceived -= OnNotification; }
        private sealed class RestSubscription<T> : IDisposable
        {
            private readonly PolarRestService _owner; private readonly Func<PolarRestEvent, T> _convert;
            private readonly System.Action<T> _next; private readonly System.Action<Exception> _error; private int _disposed;
            public RestSubscription(PolarRestService owner, Func<PolarRestEvent, T> convert, System.Action<T> next, System.Action<Exception> error)
            { _owner = owner; _convert = convert; _next = next; _error = error; owner.EventReceived += Receive; owner.ErrorOccurred += Fail; }
            private void Receive(object? sender, PolarRestEvent value) { if (Volatile.Read(ref _disposed) != 0) return; try { _next(_convert(value)); } catch (Exception error) { _error(error); } }
            private void Fail(object? sender, Exception error) { if (Volatile.Read(ref _disposed) == 0) _error(error); }
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _owner.EventReceived -= Receive; _owner.ErrorOccurred -= Fail; }
        }
    }
}
