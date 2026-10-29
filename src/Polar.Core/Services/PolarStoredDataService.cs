using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>Typed, lossless protobuf access to stored activity, sleep and test data.</summary>
    public sealed partial class PolarStoredDataService
    {
        private readonly PolarFileService _files;
        public PolarStoredDataService(PolarFileService files) { _files = files; }
        public Task<IReadOnlyList<DatedValue<PbDailySummary>>> GetDailySummariesAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => ReadDaysAsync(from, to, "DSUM/DSUM.BPB", PbDailySummary.Parser, cancellationToken);
        public Task<IReadOnlyList<DatedValue<PbNightlyRecoveryStatus>>> GetNightlyRechargeAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => ReadDaysAsync(from, to, "NR/NR.BPB", PbNightlyRecoveryStatus.Parser, cancellationToken);
        public Task<IReadOnlyList<DatedValue<TemperatureMeasurementPeriod>>> GetSkinTemperatureAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => ReadDaysAsync(from, to, "SKINTEMP/TEMPCONT.BPB", TemperatureMeasurementPeriod.Parser, cancellationToken);
        public async Task<IReadOnlyList<PolarSleepResult>> GetSleepAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        {
            var output = new List<PolarSleepResult>();
            foreach (var day in Dates(from, to))
            {
                PbSleepAnalysisResult analysis;
                try { analysis = await _files.ReadMessageAsync(DayPath(day) + "SLEEP/SLEEPRES.BPB", PbSleepAnalysisResult.Parser, cancellationToken).ConfigureAwait(false); }
                catch (PftpResponseException error) when (error.ErrorCode == 103) { continue; }
                PbSleepSkinTemperatureResult? temperature = null;
                try { temperature = await _files.ReadMessageAsync(DayPath(day) + "NSTRESUL/NSTRCONT.BPB", PbSleepSkinTemperatureResult.Parser, cancellationToken).ConfigureAwait(false); }
                catch (PftpResponseException error) when (error.ErrorCode == 103) { }
                output.Add(new PolarSleepResult(day, analysis, temperature));
            }
            return output;
        }
        public async Task<IReadOnlyList<DatedValue<IReadOnlyList<StoredMessage<PbActivitySamples>>>>> GetActivitySamplesAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        {
            var output = new List<DatedValue<IReadOnlyList<StoredMessage<PbActivitySamples>>>>();
            foreach (var day in Dates(from, to))
            {
                var files = await ListExistingAsync(DayPath(day) + "ACT/", cancellationToken).ConfigureAwait(false);
                if (files == null) continue;
                var data = new List<StoredMessage<PbActivitySamples>>();
                foreach (var file in files.Where(f => !f.IsDirectory && f.Path.EndsWith(".BPB", StringComparison.Ordinal)))
                    data.Add(new StoredMessage<PbActivitySamples>(file.Path, await _files.ReadMessageAsync(file.Path, PbActivitySamples.Parser, cancellationToken).ConfigureAwait(false)));
                output.Add(new DatedValue<IReadOnlyList<StoredMessage<PbActivitySamples>>>(day, data));
            }
            return output;
        }
        public async Task<IReadOnlyList<DatedValue<ulong>>> GetStepsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        {
            var values = await GetActivitySamplesAsync(from, to, cancellationToken).ConfigureAwait(false);
            return values.Select(day => new DatedValue<ulong>(day.Date, day.Value.Aggregate(0UL, (total, file) => checked(total + file.Data.StepsSamples.Aggregate(0UL, (sum, value) => checked(sum + value)))))).ToArray();
        }
        public async Task<IReadOnlyList<DatedValue<float>>> GetDistanceAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => (await GetDailySummariesAsync(from, to, cancellationToken).ConfigureAwait(false)).Select(x => new DatedValue<float>(x.Date, x.Value.ActivityDistance)).ToArray();
        public async Task<IReadOnlyList<DatedValue<PbActivityClassTimes>>> GetActiveTimeAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => (await GetDailySummariesAsync(from, to, cancellationToken).ConfigureAwait(false)).Where(x => x.Value.ActivityClassTimes != null).Select(x => new DatedValue<PbActivityClassTimes>(x.Date, x.Value.ActivityClassTimes)).ToArray();
        public async Task<IReadOnlyList<DatedValue<uint>>> GetCaloriesAsync(DateTime from, DateTime to, PolarCalorieType type, CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(typeof(PolarCalorieType), type)) throw new ArgumentOutOfRangeException(nameof(type));
            return (await GetDailySummariesAsync(from, to, cancellationToken).ConfigureAwait(false)).Select(x => new DatedValue<uint>(x.Date,
                type == PolarCalorieType.Activity ? x.Value.ActivityCalories : type == PolarCalorieType.Training ? x.Value.TrainingCalories : x.Value.BmrCalories)).ToArray();
        }
        /// <summary>Includes both 24/7 HR and PPI sample collections, preserving timing and quality metadata.</summary>
        public async Task<IReadOnlyList<StoredMessage<PbAutomaticSampleSessions>>> GetAutomaticSamplesAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        {
            ValidateDates(from, to); var output = new List<StoredMessage<PbAutomaticSampleSessions>>();
            var files = await ListExistingAsync("/U/0/AUTOS/", cancellationToken).ConfigureAwait(false);
            if (files == null) return output;
            foreach (var file in files.Where(f => Regex.IsMatch(f.Path, @"/AUTOS\d{3}\.BPB$", RegexOptions.CultureInvariant)))
            {
                var data = await _files.ReadMessageAsync(file.Path, PbAutomaticSampleSessions.Parser, cancellationToken).ConfigureAwait(false);
                var day = new DateTime((int)data.Day.Year, (int)data.Day.Month, (int)data.Day.Day);
                if (day >= from.Date && day <= to.Date) output.Add(new StoredMessage<PbAutomaticSampleSessions>(file.Path, data));
            }
            return output;
        }
        public async Task<IReadOnlyList<StoredMessage<PbSpo2TestResult>>> GetSpo2TestsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        {
            var output = new List<StoredMessage<PbSpo2TestResult>>();
            foreach (var day in Dates(from, to))
            {
                var files = await ListExistingAsync(DayPath(day) + "SPO2TEST/", cancellationToken).ConfigureAwait(false);
                if (files == null) continue;
                foreach (var file in files.Where(f => !f.IsDirectory && f.Path.EndsWith("/SPO2TRES.BPB", StringComparison.Ordinal)))
                    output.Add(new StoredMessage<PbSpo2TestResult>(file.Path, await _files.ReadMessageAsync(file.Path, PbSpo2TestResult.Parser, cancellationToken).ConfigureAwait(false)));
            }
            return output;
        }
        public async Task<IReadOnlyList<DatedValue<IReadOnlyList<PbAutomaticHeartRateSamples>>>> Get247HrSamplesAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => (await GetAutomaticSamplesAsync(from, to, cancellationToken).ConfigureAwait(false)).Select(x =>
                new DatedValue<IReadOnlyList<PbAutomaticHeartRateSamples>>(new DateTime((int)x.Data.Day.Year, (int)x.Data.Day.Month, (int)x.Data.Day.Day), x.Data.Samples.ToArray())).ToArray();
        public async Task<IReadOnlyList<PolarStoredPpiSession>> Get247PpiSamplesAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        {
            var output = new List<PolarStoredPpiSession>();
            foreach (var file in await GetAutomaticSamplesAsync(from, to, cancellationToken).ConfigureAwait(false))
                output.AddRange(DecodePpiSessions(file.Data));
            return output;
        }
        public static IReadOnlyList<PolarStoredPpiSession> DecodePpiSessions(PbAutomaticSampleSessions data)
        {
            if (data.Day == null) throw new PolarProtocolException("Automatic sample date is missing.");
            var date = new DateTime((int)data.Day.Year, (int)data.Day.Month, (int)data.Day.Day, 0, 0, 0, DateTimeKind.Unspecified);
            var output = new List<PolarStoredPpiSession>();
            foreach (var session in data.PpiSamples)
            {
                if (session.Ppi == null || session.RecordingTime == null) throw new PolarProtocolException("PPI session is missing samples or time.");
                var values = session.Ppi;
                if (values.PpiDelta.Count != values.PpiErrorEstimateDelta.Count || values.PpiDelta.Count != values.Status.Count)
                    throw new PolarProtocolException("PPI sample arrays have different lengths.");
                var samples = new List<PolarStoredPpiSample>(); int interval = 0, error = 0;
                for (int i = 0; i < values.PpiDelta.Count; i++)
                {
                    interval = checked(interval + values.PpiDelta[i]); error = checked(error + values.PpiErrorEstimateDelta[i]);
                    if (interval < 0 || error < 0) throw new PolarProtocolException("Negative decoded PPI interval or uncertainty.");
                    samples.Add(new PolarStoredPpiSample(interval, error, values.Status[i]));
                }
                var time = session.RecordingTime;
                var start = date.AddHours(time.Hour).AddMinutes(time.Minute).AddSeconds(time.Seconds).AddMilliseconds(time.Millis);
                output.Add(new PolarStoredPpiSession(start, (int)session.TriggerType, samples));
            }
            return output;
        }
        public async Task DeleteStoredDataAsync(PolarStoredDataType type, DateTime untilInclusive, CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(typeof(PolarStoredDataType), type)) throw new ArgumentOutOfRangeException(nameof(type));
            if (type == PolarStoredDataType.AutomaticSamples)
            {
                foreach (var file in await GetAutomaticSamplesAsync(DateTime.MinValue, untilInclusive, cancellationToken).ConfigureAwait(false))
                    await _files.DeleteAsync(file.Path, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (type == PolarStoredDataType.SensorLogs)
            {
                var logs = await ListExistingAsync("/SDLOGS/", cancellationToken).ConfigureAwait(false);
                if (logs != null)
                    foreach (var file in logs.Where(f => !f.IsDirectory && (f.Path.EndsWith(".SLG", StringComparison.Ordinal) || f.Path.EndsWith(".TXT", StringComparison.Ordinal))))
                        await _files.DeleteAsync(file.Path, cancellationToken).ConfigureAwait(false);
                return;
            }
            string folder = type switch
            {
                PolarStoredDataType.Activity => "ACT", PolarStoredDataType.DailySummary => "DSUM", PolarStoredDataType.NightlyRecovery => "NR",
                PolarStoredDataType.Sleep => "SLEEP", PolarStoredDataType.SleepScore => "SLEEPSCO", PolarStoredDataType.SkinContactChanges => "SKINCONT",
                PolarStoredDataType.SkinTemperature => "SKINTEMP", PolarStoredDataType.Spo2Test => "SPO2TEST",
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
            foreach (var dayEntry in await _files.ListAsync("/U/0/", false, cancellationToken).ConfigureAwait(false))
            {
                var match = Regex.Match(dayEntry.Path, @"^/U/0/(\d{8})/$", RegexOptions.CultureInvariant);
                if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day > untilInclusive.Date) continue;
                var files = await ListExistingAsync(dayEntry.Path + folder + "/", cancellationToken).ConfigureAwait(false);
                if (files == null) continue;
                var removed = files.Where(f => !f.IsDirectory && f.Path.EndsWith(".BPB", StringComparison.Ordinal) && !f.Path.Contains("HIST")).ToArray();
                foreach (var file in removed)
                    await _files.DeleteAsync(file.Path, cancellationToken).ConfigureAwait(false);
                var directories = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in removed)
                {
                    string path = file.Path.Substring(0, file.Path.LastIndexOf('/') + 1);
                    while (path != "/U/0/" && path.StartsWith("/U/0/", StringComparison.Ordinal))
                    { directories.Add(path); path = path.TrimEnd('/'); path = path.Substring(0, path.LastIndexOf('/') + 1); }
                }
                foreach (var path in directories.OrderByDescending(x => x.Length))
                {
                    try { if ((await _files.ListAsync(path, false, cancellationToken).ConfigureAwait(false)).Count == 0) await _files.DeleteAsync(path.TrimEnd('/'), cancellationToken).ConfigureAwait(false); }
                    catch (PftpResponseException error) when (error.ErrorCode == 103) { }
                }
            }
        }
        public async Task DeleteDateFoldersAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        {
            foreach (var day in Dates(from, to))
            {
                try { await _files.DeleteAsync(DayPath(day).TrimEnd('/'), cancellationToken).ConfigureAwait(false); }
                catch (PftpResponseException error) when (error.ErrorCode == 103) { }
            }
        }
        private async Task<IReadOnlyList<DatedValue<T>>> ReadDaysAsync<T>(DateTime from, DateTime to, string suffix, MessageParser<T> parser, CancellationToken cancellationToken) where T : IMessage<T>
        {
            var output = new List<DatedValue<T>>();
            foreach (var day in Dates(from, to))
            {
                try { output.Add(new DatedValue<T>(day, await _files.ReadMessageAsync(DayPath(day) + suffix, parser, cancellationToken).ConfigureAwait(false))); }
                catch (PftpResponseException error) when (error.ErrorCode == 103) { }
            }
            return output;
        }
        private async Task<IReadOnlyList<PolarFileEntry>?> ListExistingAsync(string directory, CancellationToken cancellationToken)
        {
            try { return await _files.ListAsync(directory, true, cancellationToken).ConfigureAwait(false); }
            catch (PftpResponseException error) when (error.ErrorCode == 103) { return null; }
        }
        internal static string DayPath(DateTime day) => "/U/0/" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "/";
        internal static void ValidateDates(DateTime from, DateTime to)
        { if (to.Date < from.Date) throw new ArgumentOutOfRangeException(nameof(to), "Use an ordered date range."); }
        internal static IEnumerable<DateTime> Dates(DateTime from, DateTime to)
        {
            ValidateDates(from, to);
            if ((to.Date - from.Date).TotalDays > 36600) throw new ArgumentOutOfRangeException(nameof(to), "Daily file enumeration is limited to 100 years.");
            for (DateTime day = from.Date; ; day = day.AddDays(1)) { yield return day; if (day == to.Date) yield break; }
        }
    }
}
