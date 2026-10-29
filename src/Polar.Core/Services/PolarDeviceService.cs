using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Services
{
    public sealed class PolarDiskSpace
    {
        public ulong TotalBytes { get; }
        public ulong FreeBytes { get; }
        public PolarDiskSpace(PbPFtpDiskSpaceResult data)
        { TotalBytes = checked(data.TotalFragments * data.FragmentSize); FreeBytes = checked(data.FreeFragments * data.FragmentSize); }
    }

    /// <summary>Device commands. Unsupported commands preserve the device's PFTP error code.</summary>
    public sealed partial class PolarDeviceService
    {
        private readonly PftpClient _client;
        private readonly PolarFileService _files;
        private readonly string _settingsPath;
        private readonly bool _h10FileSystem;
        private readonly SemaphoreSlim _settingsLock = new SemaphoreSlim(1, 1);
        public PolarDeviceService(PftpClient client, PolarFileService files, bool h10FileSystem = false)
        { _client = client; _files = files; _h10FileSystem = h10FileSystem; _settingsPath = h10FileSystem ? "/UDEVSET.BPB" : "/U/0/S/UDEVSET.BPB"; }
        public async Task SetSystemTimeAsync(DateTimeOffset time, bool trusted = true, CancellationToken cancellationToken = default)
        {
            var utc = time.UtcDateTime;
            await _client.QueryAsync(1, new PbPFtpSetSystemTimeParams { Date = ToDate(utc), Time = ToTime(utc), Trusted = trusted }.ToByteArray(), cancellationToken).ConfigureAwait(false);
        }
        public async Task SetLocalTimeAsync(DateTimeOffset time, CancellationToken cancellationToken = default)
        {
            // The explicit offset avoids inheriting the execution host's timezone.
            if (!_h10FileSystem)
            {
                try { await SetSystemTimeAsync(time, true, cancellationToken).ConfigureAwait(false); }
                catch (PftpResponseException error) when (error.ErrorCode == 201) { } // NOT_IMPLEMENTED
            }
            await _client.QueryAsync(3, new PbPFtpSetLocalTimeParams
            { Date = ToDate(time.DateTime), Time = ToTime(time.DateTime), TzOffset = (int)time.Offset.TotalMinutes }.ToByteArray(), cancellationToken).ConfigureAwait(false);
        }
        public async Task<DateTimeOffset> GetLocalTimeAsync(CancellationToken cancellationToken = default)
        {
            var value = PbPFtpSetLocalTimeParams.Parser.ParseFrom(await _client.QueryAsync(4, null, cancellationToken).ConfigureAwait(false));
            return new DateTimeOffset((int)value.Date.Year, (int)value.Date.Month, (int)value.Date.Day,
                (int)value.Time.Hour, (int)value.Time.Minute, (int)value.Time.Seconds, (int)value.Time.Millis, TimeSpan.FromMinutes(value.TzOffset));
        }
        public async Task<PolarDiskSpace> GetDiskSpaceAsync(CancellationToken cancellationToken = default)
            => new PolarDiskSpace(PbPFtpDiskSpaceResult.Parser.ParseFrom(await _client.QueryAsync(5, null, cancellationToken).ConfigureAwait(false)));

        public async Task StartSyncAsync(CancellationToken cancellationToken = default)
        {
            await _client.QueryAsync(13, null, cancellationToken).ConfigureAwait(false);
            await _client.SendNotificationAsync(8, null, cancellationToken).ConfigureAwait(false);
            await _client.SendNotificationAsync(0, null, cancellationToken).ConfigureAwait(false);
        }
        public async Task StopSyncAsync(bool completed = true, CancellationToken cancellationToken = default)
        {
            await _client.SendNotificationAsync(1, new PbPFtpStopSyncParams { Completed = completed }.ToByteArray(), cancellationToken).ConfigureAwait(false);
            await _client.SendNotificationAsync(9, null, cancellationToken).ConfigureAwait(false);
        }
        public async Task RunSyncAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            await StartSyncAsync(cancellationToken).ConfigureAwait(false);
            bool completed = false;
            try { await work(cancellationToken).ConfigureAwait(false); completed = true; }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (completed) await StopSyncAsync(true, cleanup.Token).ConfigureAwait(false);
                else { try { await StopSyncAsync(false, cleanup.Token).ConfigureAwait(false); } catch { /* Preserve the primary transfer failure. */ } }
            }
        }
        public Task RestartAsync(CancellationToken cancellationToken = default) => ResetAsync(false, false, false, false, cancellationToken);
        public Task FactoryResetAsync(CancellationToken cancellationToken = default) => ResetAsync(false, true, false, false, cancellationToken);
        public Task TurnOffAsync(CancellationToken cancellationToken = default) => ResetAsync(true, false, false, false, cancellationToken);
        public Task HibernateAsync(CancellationToken cancellationToken = default) => ResetAsync(true, false, true, false, cancellationToken);
        public Task WarehouseSleepAsync(CancellationToken cancellationToken = default) => ResetAsync(true, true, false, false, cancellationToken);
        public Task ResetAsync(bool sleep, bool factoryDefaults, bool hibernate, bool preservePairing, CancellationToken cancellationToken = default)
            => _client.SendNotificationAsync(2, new PbPFtpFactoryResetParams
            { Sleep = sleep, DoFactoryDefaults = factoryDefaults, Hibernate = hibernate, OtaFwupdate = preservePairing }.ToByteArray(), cancellationToken);
        internal static PbDate ToDate(DateTime value) => new PbDate { Year = (uint)value.Year, Month = (uint)value.Month, Day = (uint)value.Day };
        internal static PbTime ToTime(DateTime value) => new PbTime { Hour = (uint)value.Hour, Minute = (uint)value.Minute, Seconds = (uint)value.Second, Millis = (uint)value.Millisecond };
    }
}
