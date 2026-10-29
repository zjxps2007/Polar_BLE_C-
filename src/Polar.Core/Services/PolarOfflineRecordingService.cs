using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Offline;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Pmd;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Services
{
    public sealed class PolarOfflineRecordingEntry
    {
        public string Path { get; }
        /// <summary>Date encoded in the device path; no timezone is implied.</summary>
        public DateTime Date { get; }
        public PmdMeasurementType Type { get; }
        public ulong Size { get; }
        public IReadOnlyList<PolarFileEntry> Parts { get; }
        internal PolarOfflineRecordingEntry(string path, DateTime date, PmdMeasurementType type, IReadOnlyList<PolarFileEntry> parts)
        { Path = path; Date = date; Type = type; Parts = parts; Size = parts.Aggregate(0UL, (sum, item) => checked(sum + item.Size)); }
    }

    public sealed class PolarOfflineRecordingService
    {
        private readonly PolarFileService _files;
        private readonly PmdClient? _pmd;
        private static readonly Regex RecordingPath = new Regex(@"^/U/0/(?<date>\d{8})/R/(?<time>\d{6})/(?<type>[A-Z]+)(?<part>\d*)\.REC$", RegexOptions.CultureInvariant);
        public PolarOfflineRecordingService(PolarFileService files, PmdClient? pmd = null) { _files = files; _pmd = pmd; }
        public Task<PmdSettings> GetSettingsAsync(PmdMeasurementType type, bool full = false, CancellationToken cancellationToken = default)
            => RequirePmd().GetSettingsAsync(type, true, full, cancellationToken);
        public Task StartAsync(PmdMeasurementType type, PmdSettings? settings = null, PmdSecret? secret = null, CancellationToken cancellationToken = default)
            => RequirePmd().StartAsync(type, settings ?? new PmdSettings(), true, secret, cancellationToken);
        public async Task StopAsync(PmdMeasurementType type, CancellationToken cancellationToken = default)
        {
            var pmd = RequirePmd(); await pmd.StopAsync(type, cancellationToken).ConfigureAwait(false);
            for (int attempt = 0; attempt < 50; attempt++)
            {
                var state = await pmd.GetMeasurementStatusAsync(cancellationToken).ConfigureAwait(false);
                if (!state.TryGetValue(type, out var status) || (status & PmdMeasurementStatus.Offline) == 0) return;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("Device did not confirm offline recording stopped.");
        }
        public async Task<IReadOnlyCollection<PmdMeasurementType>> GetStatusAsync(CancellationToken cancellationToken = default)
            => (await RequirePmd().GetMeasurementStatusAsync(cancellationToken).ConfigureAwait(false)).Where(x => (x.Value & PmdMeasurementStatus.Offline) != 0).Select(x => x.Key).ToArray();
        public async Task<IReadOnlyCollection<PmdMeasurementType>> GetAvailableDataTypesAsync(CancellationToken cancellationToken = default)
            => (await RequirePmd().ReadFeaturesAsync(cancellationToken).ConfigureAwait(false)).Where(x => x != PmdMeasurementType.SdkMode && x != PmdMeasurementType.OfflineRecording && x != PmdMeasurementType.Unknown).ToArray();
        public Task<PmdOfflineTrigger> GetTriggerAsync(CancellationToken cancellationToken = default) => RequirePmd().GetOfflineTriggerAsync(cancellationToken);
        public Task SetTriggerAsync(PmdOfflineTrigger trigger, PmdSecret? secret = null, CancellationToken cancellationToken = default) => RequirePmd().SetOfflineTriggerAsync(trigger, secret, cancellationToken);

        public async Task<IReadOnlyList<PolarOfflineRecordingEntry>> ListAsync(bool split = false, CancellationToken cancellationToken = default)
        {
            byte[]? index = null;
            try { index = await _files.ReadAsync("/PMDFILES.TXT", cancellationToken).ConfigureAwait(false); }
            catch (PftpResponseException error) when (error.ErrorCode == 103 || error.ErrorCode == 201) { }
            if (index != null && index.Length != 0) return ParseIndex(index, split);
            var parts = new List<PolarFileEntry>();
            foreach (var day in await _files.ListAsync("/U/0/", false, cancellationToken).ConfigureAwait(false))
            {
                if (!Regex.IsMatch(day.Path, @"^/U/0/\d{8}/$", RegexOptions.CultureInvariant)) continue;
                try
                {
                    parts.AddRange((await _files.ListAsync(day.Path + "R/", true, cancellationToken).ConfigureAwait(false))
                        .Where(e => !e.IsDirectory && e.Path.EndsWith(".REC", StringComparison.Ordinal)));
                }
                catch (PftpResponseException error) when (error.ErrorCode == 103) { }
            }
            return GroupEntries(parts, split);
        }
        public static IReadOnlyList<PolarOfflineRecordingEntry> ParseIndex(byte[] index, bool split = false)
        {
            var parts = new List<PolarFileEntry>();
            string text = new UTF8Encoding(false, true).GetString(index);
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var columns = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 2 || !columns[1].EndsWith(".REC", StringComparison.Ordinal)) continue;
                if (!ulong.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                    throw new PolarProtocolException("Invalid recording size in PMDFILES.TXT.");
                if (size == 0) continue;
                parts.Add(new PolarFileEntry(columns[1], new PbPFtpEntry { Name = columns[1].Substring(columns[1].LastIndexOf('/') + 1), Size = size }));
            }
            return GroupEntries(parts, split);
        }
        public async Task<OfflineRecording> DownloadAsync(PolarOfflineRecordingEntry entry, PmdSecret? secret = null,
            CancellationToken cancellationToken = default, IProgress<long>? progress = null, IReadOnlyCollection<int>? derivedMethods = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (entry.Parts.Count == 0) throw new PolarProtocolException("Offline recording has no file parts.");
            var batches = new List<PmdDataBatch>(); OfflineRecording? first = null; ulong previousTimestamp = 0; long completed = 0;
            foreach (var part in entry.Parts)
            {
                long offset = completed;
                var bytes = await _files.ReadAsync(part.Path, cancellationToken,
                    progress == null ? null : new InlineProgress(value => progress.Report(checked(offset + value)))).ConfigureAwait(false);
                var record = OfflineRecordingParser.Parse(bytes, secret, previousTimestamp, derivedMethods);
                first ??= record;
                if (record.Version != first.Version || record.FirmwareHash != first.FirmwareHash)
                    throw new PolarProtocolException("Offline parts contain different firmware/header versions.");
                foreach (var batch in record.Batches)
                    if (batch.Type != entry.Type && batch.Type != PmdMeasurementType.DerivedMeasurement)
                        throw new PolarProtocolException("Offline data measurement does not match its filename.");
                batches.AddRange(record.Batches);
                if (record.Batches.Count > 0) previousTimestamp = record.LastTimestampNanoseconds;
                completed = checked(completed + bytes.Length); progress?.Report(completed);
            }
            return new OfflineRecording(first!.Version, first.FirmwareHash, first.HeaderFree, first.StartTimeUtc, first.Settings, batches);
        }
        public async Task<OfflineRecording> DownloadFileAsync(string path, PmdSecret? secret = null, CancellationToken cancellationToken = default)
        {
            if (!RecordingPath.IsMatch(path)) throw new ArgumentException("Invalid Polar offline recording path.", nameof(path));
            return OfflineRecordingParser.Parse(await _files.ReadAsync(path, cancellationToken).ConfigureAwait(false), secret);
        }
        public async Task<int> GetSubRecordingCountAsync(PolarOfflineRecordingEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            try { return (await FindCurrentPartsAsync(entry, cancellationToken).ConfigureAwait(false)).Count; }
            catch (PftpResponseException error) when (error.ErrorCode == 103) { return 0; }
        }
        public async Task RemoveAsync(PolarOfflineRecordingEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            // Re-list: a split entry represents the recording type for removal, and more parts may have appeared since listing.
            foreach (var part in await FindCurrentPartsAsync(entry, cancellationToken).ConfigureAwait(false))
                await _files.DeleteAsync(part.Path, cancellationToken).ConfigureAwait(false);
            string directory = entry.Path.Substring(0, entry.Path.LastIndexOf('/') + 1);
            while (directory != "/U/0/" && directory.StartsWith("/U/0/", StringComparison.Ordinal))
            {
                var remaining = await _files.ListAsync(directory, false, cancellationToken).ConfigureAwait(false);
                if (remaining.Count != 0) break;
                await _files.DeleteAsync(directory.TrimEnd('/'), cancellationToken).ConfigureAwait(false);
                directory = directory.TrimEnd('/'); directory = directory.Substring(0, directory.LastIndexOf('/') + 1);
            }
        }
        /// <summary>Boolean variant of removeOfflineRecords. Cancellation still propagates to the caller.</summary>
        public async Task<bool> TryRemoveAsync(PolarOfflineRecordingEntry entry, CancellationToken cancellationToken = default)
        {
            try { await RemoveAsync(entry, cancellationToken).ConfigureAwait(false); return true; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return false; }
        }
        private async Task<IReadOnlyList<PolarFileEntry>> FindCurrentPartsAsync(PolarOfflineRecordingEntry entry, CancellationToken cancellationToken)
        {
            string directory = entry.Path.Substring(0, entry.Path.LastIndexOf('/') + 1);
            string type = RecordingPath.Match(entry.Path).Groups["type"].Value;
            var pattern = new Regex("^" + Regex.Escape(directory + type) + @"\d*\.REC$", RegexOptions.CultureInvariant);
            return (await _files.ListAsync(directory, false, cancellationToken).ConfigureAwait(false)).Where(item => !item.IsDirectory && pattern.IsMatch(item.Path)).ToArray();
        }
        private static IReadOnlyList<PolarOfflineRecordingEntry> GroupEntries(IEnumerable<PolarFileEntry> parts, bool split)
        {
            var grouped = new Dictionary<string, List<(PolarFileEntry File, int Index)>>(StringComparer.Ordinal);
            foreach (var file in parts)
            {
                if (file.Size == 0) continue;
                var match = RecordingPath.Match(file.Path);
                if (!match.Success) throw new PolarProtocolException("Invalid offline recording path: " + file.Path);
                int number = match.Groups["part"].Length == 0 ? -1 : int.Parse(match.Groups["part"].Value, CultureInfo.InvariantCulture);
                string key = split ? file.Path : file.Path.Substring(0, file.Path.LastIndexOf('/') + 1) + match.Groups["type"].Value + ".REC";
                if (!grouped.TryGetValue(key, out var files)) grouped.Add(key, files = new List<(PolarFileEntry, int)>());
                if (files.Any(f => f.Index == number)) throw new PolarProtocolException("Duplicate offline recording part.");
                files.Add((file, number));
            }
            var output = new List<PolarOfflineRecordingEntry>();
            foreach (var group in grouped)
            {
                var files = group.Value.OrderBy(p => p.Index).ToArray();
                if (!split && (files.Length > 1 || files[0].Index >= 0))
                    for (int i = 0; i < files.Length; i++)
                        if (files[i].Index != i) throw new PolarProtocolException("Offline recording has missing or mixed numbered parts.");
                var match = RecordingPath.Match(group.Key);
                if (!DateTime.TryParseExact(match.Groups["date"].Value + match.Groups["time"].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new PolarProtocolException("Invalid recording date.");
                var type = match.Groups["type"].Value switch
                {
                    "ACC" => PmdMeasurementType.Accelerometer, "GYRO" => PmdMeasurementType.Gyroscope, "MAG" => PmdMeasurementType.Magnetometer,
                    "PPG" => PmdMeasurementType.Ppg, "PPI" => PmdMeasurementType.Ppi, "HR" => PmdMeasurementType.OfflineHeartRate,
                    "TEMP" => PmdMeasurementType.Temperature, "SKINTEMP" => PmdMeasurementType.SkinTemperature,
                    _ => throw new PolarProtocolException("Unsupported offline recording name: " + match.Groups["type"].Value)
                };
                output.Add(new PolarOfflineRecordingEntry(group.Key, date, type, files.Select(p => p.File).ToArray()));
            }
            return output.OrderBy(e => e.Date).ThenBy(e => e.Path, StringComparer.Ordinal).ToArray();
        }
        private PmdClient RequirePmd() => _pmd ?? throw new PolarFeatureNotAvailableException("PMD");
        private sealed class InlineProgress : IProgress<long>
        { private readonly System.Action<long> _callback; public InlineProgress(System.Action<long> callback) { _callback = callback; } public void Report(long value) => _callback(value); }
    }
}
