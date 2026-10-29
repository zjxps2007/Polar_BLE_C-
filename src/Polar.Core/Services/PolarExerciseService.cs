using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using PolarBle.Models;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Services
{
    public enum PolarH10SampleType { HeartRate, RrInterval }
    public sealed class PolarExerciseStatusEvent : EventArgs
    {
        public PbPftpGetExerciseStatusResult Status { get; }
        public PolarExerciseStatus Information => new PolarExerciseStatus(Status);
        public PolarExerciseStatusEvent(PbPftpGetExerciseStatusResult status) { Status = status; }
    }
    public sealed class PolarExerciseService : IDisposable
    {
        private readonly PftpClient _client;
        private readonly PolarFileService _files;
        public event EventHandler<PolarExerciseStatusEvent>? StatusChanged;
        public PolarExerciseService(PftpClient client, PolarFileService files)
        { _client = client; _files = files; _client.NotificationReceived += OnNotification; }
        public async Task StartH10RecordingAsync(string recordingId, PolarH10SampleType type = PolarH10SampleType.HeartRate, uint intervalSeconds = 1, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(recordingId) || recordingId.Length >= 64) throw new ArgumentException("A recording identifier of 1 to 63 characters is required.", nameof(recordingId));
            if (!Enum.IsDefined(typeof(PolarH10SampleType), type)) throw new ArgumentOutOfRangeException(nameof(type));
            if (intervalSeconds != 1 && intervalSeconds != 5) throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
            await _client.QueryAsync(14, new PbPFtpRequestStartRecordingParams
            {
                SampleDataIdentifier = recordingId,
                SampleType = (PbSampleType)(type == PolarH10SampleType.HeartRate ? 1 : 16),
                RecordingInterval = new PbDuration { Seconds = intervalSeconds }
            }.ToByteArray(), cancellationToken).ConfigureAwait(false);
        }
        public async Task StopH10RecordingAsync(CancellationToken cancellationToken = default)
            => await _client.QueryAsync(15, null, cancellationToken).ConfigureAwait(false);
        public async Task<PbRequestRecordingStatusResult> GetH10RecordingStatusAsync(CancellationToken cancellationToken = default)
            => PbRequestRecordingStatusResult.Parser.ParseFrom(await _client.QueryAsync(16, null, cancellationToken).ConfigureAwait(false));
        public async Task<PolarRecordingStatus> GetRecordingStatusAsync(CancellationToken cancellationToken = default)
            => new PolarRecordingStatus(await GetH10RecordingStatusAsync(cancellationToken).ConfigureAwait(false));
        public async Task<IReadOnlyList<PolarFileEntry>> ListOfflineExercisesAsync(string root = "/", CancellationToken cancellationToken = default)
            => (await _files.ListAsync(root, true, cancellationToken).ConfigureAwait(false)).Where(f => f.Path.EndsWith("/SAMPLES.BPB", StringComparison.Ordinal)).ToArray();
        public Task<PbExerciseSamples> FetchOfflineExerciseAsync(string samplePath, CancellationToken cancellationToken = default)
            => _files.ReadMessageAsync(samplePath, PbExerciseSamples.Parser, cancellationToken);
        public async Task<IReadOnlyList<PolarExerciseEntry>> ListExerciseEntriesAsync(string root = "/", CancellationToken cancellationToken = default)
            => (await ListOfflineExercisesAsync(root, cancellationToken).ConfigureAwait(false)).Select(x => new PolarExerciseEntry(x)).ToArray();
        public async Task<PolarExerciseData> FetchExerciseDataAsync(string samplePath, CancellationToken cancellationToken = default)
            => new PolarExerciseData(await FetchOfflineExerciseAsync(samplePath, cancellationToken).ConfigureAwait(false));
        /// <summary>H10 and Offline Exercise V2 both remove exactly the entry path, as in the mobile SDK. Other files are preserved.</summary>
        public Task RemoveOfflineExerciseAsync(string samplePath, CancellationToken cancellationToken = default) => _files.DeleteAsync(samplePath, cancellationToken);
        public async Task StartExerciseAsync(ulong sportIdentifier, CancellationToken cancellationToken = default)
        {
            if (sportIdentifier == 0) throw new ArgumentOutOfRangeException(nameof(sportIdentifier));
            await _client.QueryAsync(21, new PbPFtpStartExerciseParams { SportIdentifier = new PbSportIdentifier { Value = sportIdentifier } }.ToByteArray(), cancellationToken).ConfigureAwait(false);
        }
        public async Task StopExerciseAsync(bool save = true, CancellationToken cancellationToken = default)
            => await _client.QueryAsync(22, new PbPFtpStopExerciseParams { Save = save }.ToByteArray(), cancellationToken).ConfigureAwait(false);
        public async Task PauseExerciseAsync(CancellationToken cancellationToken = default)
            => await _client.QueryAsync(23, null, cancellationToken).ConfigureAwait(false);
        public async Task ResumeExerciseAsync(CancellationToken cancellationToken = default)
        {
            var bytes = await _client.QueryAsync(24, null, cancellationToken).ConfigureAwait(false);
            if (bytes.Length != 0)
            {
                var result = PbPftpResumeExerciseResult.Parser.ParseFrom(bytes);
                if ((int)result.Result != 1) throw new PolarException("Exercise resume rejected: " + result.Result);
            }
        }
        public async Task<PbPftpGetExerciseStatusResult> GetExerciseStatusAsync(CancellationToken cancellationToken = default)
            => PbPftpGetExerciseStatusResult.Parser.ParseFrom(await _client.QueryAsync(25, null, cancellationToken).ConfigureAwait(false));
        public async Task<PolarExerciseStatus> GetExerciseInformationAsync(CancellationToken cancellationToken = default)
            => new PolarExerciseStatus(await GetExerciseStatusAsync(cancellationToken).ConfigureAwait(false));
        public async Task<PbPftpStartDmExerciseResult> StartOfflineExerciseV2Async(ulong sportIdentifier, CancellationToken cancellationToken = default)
        {
            if (sportIdentifier == 0) throw new ArgumentOutOfRangeException(nameof(sportIdentifier));
            return PbPftpStartDmExerciseResult.Parser.ParseFrom(await _client.QueryAsync(26,
                new PbPFtpStartDmExerciseParams { SportIdentifier = new PbSportIdentifier { Value = sportIdentifier } }.ToByteArray(), cancellationToken).ConfigureAwait(false));
        }
        public async Task<PolarOfflineExerciseStartResult> StartOfflineExerciseAsync(ulong sportIdentifier, CancellationToken cancellationToken = default)
            => new PolarOfflineExerciseStartResult(await StartOfflineExerciseV2Async(sportIdentifier, cancellationToken).ConfigureAwait(false));
        public Task StopOfflineExerciseV2Async(CancellationToken cancellationToken = default) => StopExerciseAsync(true, cancellationToken);
        public async Task<bool> GetOfflineExerciseV2StatusAsync(CancellationToken cancellationToken = default)
        {
            var status = await GetExerciseStatusAsync(cancellationToken).ConfigureAwait(false);
            return (int)status.ExerciseType == 4 && (int)status.ExerciseState == 1;
        }
        public async Task<bool> IsOfflineExerciseV2SupportedAsync(CancellationToken cancellationToken = default)
        {
            try { return (await _files.ReadMessageAsync("/DEVICE.BPB", PbDeviceInfo.Parser, cancellationToken).ConfigureAwait(false)).Capabilities.Contains("dm_exercise"); }
            catch (PftpResponseException error) when (error.ErrorCode == 202)
            {
                // A running Data Merge exercise proves support. Any other busy state is unknown.
                if (await GetOfflineExerciseV2StatusAsync(cancellationToken).ConfigureAwait(false)) return true;
                throw;
            }
        }
        public async Task<IReadOnlyList<string>> ListTrainingSessionsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        {
            var paths = new List<string>();
            foreach (var day in PolarStoredDataService.Dates(from, to))
            {
                IReadOnlyList<PolarFileEntry> entries;
                try { entries = await _files.ListAsync(PolarStoredDataService.DayPath(day) + "E/", true, cancellationToken).ConfigureAwait(false); }
                catch (PftpResponseException error) when (error.ErrorCode == 103) { continue; }
                paths.AddRange(entries.Where(e => e.Path.EndsWith("/TSESS.BPB", StringComparison.Ordinal)).Select(e => e.Path.Substring(0, e.Path.Length - "TSESS.BPB".Length)));
            }
            return paths;
        }
        public async Task<IReadOnlyList<PolarTrainingSessionReference>> GetTrainingSessionReferencesAsync(DateTime? from = null, DateTime? to = null, CancellationToken cancellationToken = default)
        {
            var output = new List<PolarTrainingSessionReference>();
            if (from.HasValue && to.HasValue) PolarStoredDataService.ValidateDates(from.Value, to.Value);
            foreach (var day in await _files.ListAsync("/U/0/", false, cancellationToken).ConfigureAwait(false))
            {
                var match = Regex.Match(day.Path, @"^/U/0/(\d{8})/$", RegexOptions.CultureInvariant);
                if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ||
                    (from.HasValue && date < from.Value.Date) || (to.HasValue && date > to.Value.Date)) continue;
                IReadOnlyList<PolarFileEntry> entries;
                try { entries = await _files.ListAsync(day.Path + "E/", true, cancellationToken).ConfigureAwait(false); }
                catch (PftpResponseException error) when (error.ErrorCode == 103) { continue; }
                foreach (var summary in entries.Where(e => e.Path.EndsWith("/TSESS.BPB", StringComparison.Ordinal)))
                {
                    string path = summary.Path.Substring(0, summary.Path.Length - "TSESS.BPB".Length);
                    output.Add(new PolarTrainingSessionReference(path, entries.Where(e => e.Path.StartsWith(path, StringComparison.Ordinal))));
                }
            }
            return output;
        }
        public async Task<PolarTrainingSession> FetchTrainingSessionAsync(string sessionDirectory, CancellationToken cancellationToken = default, IProgress<long>? progress = null, IProgress<PolarTrainingProgress>? detailedProgress = null)
        {
            string directory = ValidateTrainingPath(sessionDirectory);
            var listing = await _files.ListAsync(directory, true, cancellationToken).ConfigureAwait(false);
            var reference = new PolarTrainingSessionReference(directory, listing);
            var decoded = new Dictionary<string, IMessage>(StringComparer.Ordinal);
            var other = new Dictionary<string, byte[]>(StringComparer.Ordinal); long completed = 0;
            foreach (var entry in listing.Where(e => !e.IsDirectory))
            {
                long offset = completed;
                var current = new InlineProgress(value =>
                {
                    long total = checked(offset + value); progress?.Report(total);
                    detailedProgress?.Report(new PolarTrainingProgress(reference.TotalBytes, checked((ulong)total), entry.Path));
                });
                byte[] bytes = await _files.ReadAsync(entry.Path, cancellationToken, current).ConfigureAwait(false);
                completed = checked(completed + bytes.Length); progress?.Report(completed);
                detailedProgress?.Report(new PolarTrainingProgress(reference.TotalBytes, checked((ulong)completed), entry.Path));
                string name = entry.Path.Substring(entry.Path.LastIndexOf('/') + 1);
                if (name.EndsWith(".GZB", StringComparison.Ordinal))
                { bytes = PolarRestService.Decompress(bytes, 64 * 1024 * 1024); name = name.Substring(0, name.Length - 4) + ".BPB"; }
                IMessage? value = name switch
                {
                    "TSESS.BPB" => PbTrainingSession.Parser.ParseFrom(bytes),
                    "BASE.BPB" => PbExerciseBase.Parser.ParseFrom(bytes),
                    "SAMPLES.BPB" => PbExerciseSamples.Parser.ParseFrom(bytes),
                    "SAMPLES2.BPB" => PbExerciseSamples2.Parser.ParseFrom(bytes),
                    "ROUTE.BPB" => PbExerciseRouteSamples.Parser.ParseFrom(bytes),
                    "ROUTE2.BPB" => PbExerciseRouteSamples2.Parser.ParseFrom(bytes),
                    _ => null
                };
                if (value == null) other.Add(entry.Path, bytes); else decoded.Add(entry.Path, value);
            }
            if (!decoded.Values.OfType<PbTrainingSession>().Any()) throw new PolarProtocolException("Training session summary TSESS.BPB was missing.");
            return new PolarTrainingSession(directory, decoded, other, reference);
        }
        public async Task DeleteTrainingSessionAsync(string sessionDirectory, CancellationToken cancellationToken = default)
        {
            string directory = ValidateTrainingPath(sessionDirectory);
            string parent = directory.Substring(0, directory.Length - 7); // /U/0/yyyyMMdd/E/
            var siblings = await _files.ListAsync(parent, false, cancellationToken).ConfigureAwait(false);
            // A stale reference must never cause the remaining *different* session to be removed.
            if (!siblings.Any(x => x.Path == directory)) throw new PftpResponseException(103);
            // Upstream removes E/ when this is the only session; the date folder and other daily data survive.
            await _files.DeleteAsync(siblings.Count <= 1 ? parent : directory, cancellationToken).ConfigureAwait(false);
        }
        private static string ValidateTrainingPath(string path)
        {
            string directory = PolarFileService.DirectoryPath(path);
            if (!Regex.IsMatch(directory, @"^/U/0/\d{8}/E/\d{6}/$", RegexOptions.CultureInvariant))
                throw new ArgumentException("Expected /U/0/yyyyMMdd/E/HHmmss/.", nameof(path));
            return directory;
        }
        private void OnNotification(object? sender, PftpNotification notification)
        {
            if (notification.Id == 19)
                StatusChanged?.Invoke(this, new PolarExerciseStatusEvent(PbPftpGetExerciseStatusResult.Parser.ParseFrom(notification.Parameters)));
        }
        public void Dispose() => _client.NotificationReceived -= OnNotification;
        private sealed class InlineProgress : IProgress<long>
        { private readonly Action<long> _callback; public InlineProgress(Action<long> callback) { _callback = callback; } public void Report(long value) => _callback(value); }
    }
}
