using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Models;

namespace PolarBle.Services
{
    public sealed partial class PolarStoredDataService
    {
        public async Task<IReadOnlyList<PolarDailySummary>> GetDailySummaryDataAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => (await GetDailySummariesAsync(from, to, cancellationToken).ConfigureAwait(false)).Select(x => new PolarDailySummary(x.Date, x.Value)).ToArray();
        public async Task<IReadOnlyList<DatedValue<PolarActiveTime>>> GetActiveTimeDataAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => (await GetActiveTimeAsync(from, to, cancellationToken).ConfigureAwait(false)).Select(x => new DatedValue<PolarActiveTime>(x.Date, new PolarActiveTime(x.Value))).ToArray();
        public async Task<IReadOnlyList<DatedValue<IReadOnlyList<PolarActivitySamples>>>> GetActivityDataAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => (await GetActivitySamplesAsync(from, to, cancellationToken).ConfigureAwait(false)).Select(day =>
                new DatedValue<IReadOnlyList<PolarActivitySamples>>(day.Date, day.Value.Select(x => new PolarActivitySamples(x.Path, x.Data)).ToArray())).ToArray();
        public async Task<IReadOnlyList<DatedValue<IReadOnlyList<PolarAutomaticHeartRateSession>>>> Get247HeartRateDataAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => (await Get247HrSamplesAsync(from, to, cancellationToken).ConfigureAwait(false)).Select(day =>
                new DatedValue<IReadOnlyList<PolarAutomaticHeartRateSession>>(day.Date, day.Value.Select(x => new PolarAutomaticHeartRateSession(day.Date, x)).ToArray())).ToArray();
        public async Task<IReadOnlyList<PolarSleepData>> GetSleepDataAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => (await GetSleepAsync(from, to, cancellationToken).ConfigureAwait(false)).Select(x => new PolarSleepData(x)).ToArray();
        public async Task<IReadOnlyList<PolarNightlyRechargeData>> GetNightlyRechargeDataAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => (await GetNightlyRechargeAsync(from, to, cancellationToken).ConfigureAwait(false)).Select(x => new PolarNightlyRechargeData(x.Date, x.Value)).ToArray();
        public async Task<IReadOnlyList<PolarSkinTemperatureData>> GetSkinTemperatureDataAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => (await GetSkinTemperatureAsync(from, to, cancellationToken).ConfigureAwait(false)).Select(x => new PolarSkinTemperatureData(x.Date, x.Value)).ToArray();
        public async Task<IReadOnlyList<PolarSpo2TestData>> GetSpo2DataAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => (await GetSpo2TestsAsync(from, to, cancellationToken).ConfigureAwait(false)).Select(x => new PolarSpo2TestData(x.Path, x.Data)).ToArray();
    }
}
