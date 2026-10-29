using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Features;
using PolarBle.Protocol.Protobuf;
using PolarBle.Services;

namespace PolarBle.Firmware
{
    public sealed class FirmwareSession
    {
        public PftpClient Pftp { get; }
        public PolarFileService Files { get; }
        public PolarDeviceService Device { get; }
        public bool H10FileSystem { get; }
        public bool IsSensor { get; }
        public PolarFeatureClient? Features { get; }
        public FirmwareSession(PftpClient pftp, PolarFileService files, PolarDeviceService device, bool h10FileSystem = false, bool isSensor = true, PolarFeatureClient? features = null)
        { Pftp = pftp; Files = files; Device = device; H10FileSystem = h10FileSystem; IsSensor = isSensor; Features = features; }
    }
    public enum FirmwareUpdateStage { BackingUp, Resetting, Reconnecting, Writing, Restoring, Verifying, Completed }
    public sealed class FirmwareProgress
    {
        public FirmwareUpdateStage Stage { get; }
        public string? File { get; }
        public long Bytes { get; }
        public long TotalBytes { get; }
        public FirmwareProgress(FirmwareUpdateStage stage, string? file = null, long bytes = 0, long totalBytes = 0)
        { Stage = stage; File = file; Bytes = bytes; TotalBytes = totalBytes; }
    }
    public sealed class FirmwareUpdateException : PolarException
    {
        public IReadOnlyList<FirmwareFile> SettingsBackup { get; }
        public Exception? CleanupError { get; }
        public FirmwareUpdateException(string message, Exception inner, IReadOnlyList<FirmwareFile> backup, Exception? cleanupError = null) : base(message, inner)
        { SettingsBackup = backup; CleanupError = cleanupError; }
    }
    /// <summary>Destructive update orchestration. Does not back up recorded measurement data.
    /// The factory must return a fresh initialized PFTP session and feature client when reconnect=true.
    /// Supply Features to temporarily disable and restore multi-BLE mode on supported devices.
    /// Keep the entire device exclusive to this operation. Persist backup before reset via persistBackup.</summary>
    public sealed class FirmwareUpdater
    {
        private readonly Func<bool, CancellationToken, Task<FirmwareSession>> _session;
        private readonly SemaphoreSlim _updates = new SemaphoreSlim(1, 1);
        public TimeSpan RebootDelay { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan ReconnectTimeout { get; set; } = TimeSpan.FromMinutes(6);
        public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public FirmwareUpdater(Func<bool, CancellationToken, Task<FirmwareSession>> session) { _session = session ?? throw new ArgumentNullException(nameof(session)); }
        public async Task UpdateAsync(IReadOnlyList<FirmwareFile> package, string expectedVersion,
            Func<IReadOnlyList<FirmwareFile>, CancellationToken, Task> persistBackup,
            IProgress<FirmwareProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var ordered = ValidatePackage(package);
            FirmwareCatalog.CompareVersions(expectedVersion, expectedVersion);
            if (persistBackup == null) throw new ArgumentNullException(nameof(persistBackup));
            if (RebootDelay < TimeSpan.Zero || ReconnectTimeout <= TimeSpan.Zero || CleanupTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(ReconnectTimeout), "Firmware timeouts must be positive and reboot delay nonnegative.");
            await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<FirmwareFile> backup = Array.Empty<FirmwareFile>();
            bool changedDevice = false, syncStarted = false, restoreMultiConnection = false;
            FirmwareSession? session = null; Exception? failure = null, cleanupFailure = null;
            try
            {
                session = await _session(false, cancellationToken).ConfigureAwait(false);
                if (session.Features != null && ((await session.Features.ReadFeaturesAsync(cancellationToken).ConfigureAwait(false)) & PolarFeatureFlags.MultiConnection) != 0)
                {
                    await session.Features.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    if (await session.Features.GetMultiConnectionAsync(cancellationToken).ConfigureAwait(false))
                    {
                        // Restore even if the disable response is lost after the device applied it.
                        restoreMultiConnection = true;
                        await session.Features.SetMultiConnectionAsync(false, cancellationToken).ConfigureAwait(false);
                    }
                }
                syncStarted = true;
                await session.Device.StartSyncAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report(new FirmwareProgress(FirmwareUpdateStage.BackingUp));
                if (!session.H10FileSystem) backup = await BackupSettingsAsync(session.Files, cancellationToken).ConfigureAwait(false);
                await persistBackup(backup, cancellationToken).ConfigureAwait(false);
                progress?.Report(new FirmwareProgress(FirmwareUpdateStage.Resetting));
                changedDevice = true;
                await session.Device.ResetAsync(false, true, false, true, cancellationToken).ConfigureAwait(false);
                syncStarted = false;
                session = await ReconnectAsync(progress, cancellationToken).ConfigureAwait(false);
                syncStarted = true;
                await session.Device.StartSyncAsync(cancellationToken).ConfigureAwait(false);
                for (int fileIndex = 0; fileIndex < ordered.Length; fileIndex++)
                {
                    var file = ordered[fileIndex];
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new FirmwareProgress(FirmwareUpdateStage.Writing, file.Path, 0, file.Data.LongLength));
                    await session.Pftp.QueryAsync(12, null, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await session.Files.WriteAsync(file.Path, file.Data, cancellationToken,
                            new InlineProgress<long>(bytes => progress?.Report(new FirmwareProgress(FirmwareUpdateStage.Writing, file.Path, bytes, file.Data.LongLength)))).ConfigureAwait(false);
                    }
                    catch (PftpResponseException error) when (error.ErrorCode == 1)
                    {
                        // REBOOTING is a protocol response, not blanket acceptance of a broken transfer.
                        syncStarted = false;
                        if (fileIndex + 1 < ordered.Length)
                        {
                            session = await ReconnectAsync(progress, cancellationToken).ConfigureAwait(false);
                            syncStarted = true;
                            await session.Device.StartSyncAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                syncStarted = false;
                session = await ReconnectAsync(progress, cancellationToken).ConfigureAwait(false);
                syncStarted = true;
                await session.Device.StartSyncAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report(new FirmwareProgress(FirmwareUpdateStage.Restoring));
                await RestoreSettingsAsync(session.Files, backup, cancellationToken).ConfigureAwait(false);
                await session.Device.SetLocalTimeAsync(DateTimeOffset.Now, cancellationToken).ConfigureAwait(false);
                progress?.Report(new FirmwareProgress(FirmwareUpdateStage.Verifying));
                var device = await session.Files.ReadMessageAsync("/DEVICE.BPB", PbDeviceInfo.Parser, cancellationToken).ConfigureAwait(false);
                if (FirmwareCatalog.CompareVersions(FirmwareCatalog.VersionString(device), expectedVersion) != 0)
                    throw new PolarProtocolException("Firmware version after reboot does not match the expected version.");
                if (session.IsSensor) await session.Device.StopSyncAsync(true, cancellationToken).ConfigureAwait(false);
                else { await session.Device.RestartAsync(cancellationToken).ConfigureAwait(false); await ReconnectAsync(progress, cancellationToken).ConfigureAwait(false); }
                syncStarted = false;
            }
            catch (Exception error) { failure = error; }
            finally
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(CleanupTimeout);
                    if (syncStarted && session != null)
                        try { await session.Device.StopSyncAsync(false, cleanup.Token).ConfigureAwait(false); }
                        catch (Exception error) { cleanupFailure = error; }
                    if (restoreMultiConnection)
                    {
                        try
                        {
                            // Old clients may be disconnected or poisoned by a timed-out transaction.
                            var fresh = await _session(true, cleanup.Token).ConfigureAwait(false);
                            if (fresh.Features == null) throw new PolarFeatureNotAvailableException("Multi-BLE restore requires a feature client from the reconnect factory");
                            await fresh.Features.InitializeAsync(cleanup.Token).ConfigureAwait(false);
                            await fresh.Features.SetMultiConnectionAsync(true, cleanup.Token).ConfigureAwait(false);
                        }
                        catch (Exception error) { cleanupFailure = cleanupFailure == null ? error : new AggregateException(cleanupFailure, error); }
                    }
                }
                finally { _updates.Release(); }
            }
            if (failure != null || cleanupFailure != null)
            {
                if (changedDevice || cleanupFailure != null)
                    throw new FirmwareUpdateException("Firmware update did not complete cleanly. Saved settings are available for explicit recovery; inspect CleanupError for sync or multi-BLE restoration failures.", failure ?? cleanupFailure!, backup, cleanupFailure);
                ExceptionDispatchInfo.Capture(failure!).Throw();
            }
            progress?.Report(new FirmwareProgress(FirmwareUpdateStage.Completed));
        }
        private static FirmwareFile[] ValidatePackage(IReadOnlyList<FirmwareFile> package)
        {
            if (package == null || package.Count == 0 || package.Count > 128) throw new ArgumentException("Firmware package must contain 1 to 128 files.", nameof(package));
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new List<FirmwareFile>(); long total = 0;
            foreach (var file in package)
            {
                if (file == null || string.IsNullOrEmpty(file.Path) || file.Path[0] != '/' || file.Path.Length == 1 ||
                    file.Path.Contains("\\") || file.Path.Contains(":") || file.Path.Any(char.IsControl) ||
                    file.Path.Substring(1).Split('/').Any(x => x == "." || x == ".." || x.Length == 0) || !names.Add(file.Path))
                    throw new ArgumentException("Firmware package contains an invalid or duplicate absolute device path.", nameof(package));
                if (file.Data == null || file.Data.Length == 0) throw new ArgumentException("Firmware files cannot be empty.", nameof(package));
                total = checked(total + file.Data.LongLength);
                if (total > 256 * 1024 * 1024) throw new ArgumentException("Firmware package exceeds the size limit.", nameof(package));
                result.Add(new FirmwareFile(file.Path, (byte[])file.Data.Clone()));
            }
            return result.OrderBy(x => x.Path.EndsWith("SYSUPDAT.IMG", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToArray();
        }
        private async Task<FirmwareSession> ReconnectAsync(IProgress<FirmwareProgress>? progress, CancellationToken token)
        {
            progress?.Report(new FirmwareProgress(FirmwareUpdateStage.Reconnecting));
            await Task.Delay(RebootDelay, token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(ReconnectTimeout);
            Exception? last = null;
            while (!timeout.IsCancellationRequested)
            {
                try { return await _session(true, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    last = error;
                    try { await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { break; }
                }
            }
            token.ThrowIfCancellationRequested(); throw new TimeoutException("Device did not reconnect after firmware operation.", last);
        }
        public static async Task<IReadOnlyList<FirmwareFile>> BackupSettingsAsync(PolarFileService files, CancellationToken token = default)
        {
            var patterns = new HashSet<string>(StringComparer.Ordinal) { "/U/*/S/PHYSDATA.BPB", "/U/*/S/UDEVSET.BPB", "/U/*/S/PREFS.BPB", "/U/*/USERID.BPB" };
            try
            {
                string extra = Encoding.UTF8.GetString(await files.ReadAsync("/SYS/BACKUP.TXT", token).ConfigureAwait(false));
                foreach (var line in extra.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) if (!string.IsNullOrWhiteSpace(line)) patterns.Add(line.Trim());
            }
            catch (PftpResponseException error) when (error.ErrorCode == 103) { }
            var output = new Dictionary<string, FirmwareFile>(StringComparer.Ordinal);
            foreach (string pattern in patterns)
            {
                string path = pattern.Replace("/U/*/", "/U/0/");
                var paths = new List<string>();
                if (path.Contains("*"))
                {
                    int slash = path.LastIndexOf('/', path.IndexOf('*'));
                    if (slash < 0) throw new PolarProtocolException("Backup wildcard must use an absolute device path.");
                    string root = path.Substring(0, slash + 1);
                    string expression = "^" + Regex.Escape(path).Replace("\\*", "[^/]*") + (path.EndsWith("/", StringComparison.Ordinal) ? ".*" : "") + "$";
                    var matcher = new Regex(expression, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    try
                    {
                        foreach (var entry in await files.ListAsync(root, true, token).ConfigureAwait(false))
                            if (!entry.IsDirectory && matcher.IsMatch(entry.Path)) paths.Add(entry.Path);
                    }
                    catch (PftpResponseException error) when (error.ErrorCode == 103) { }
                }
                else if (path.EndsWith("/", StringComparison.Ordinal))
                {
                    try { paths.AddRange((await files.ListAsync(path, true, token).ConfigureAwait(false)).Where(x => !x.IsDirectory).Select(x => x.Path)); }
                    catch (PftpResponseException error) when (error.ErrorCode == 103) { }
                }
                else paths.Add(path);
                foreach (var file in paths)
                    try { output[file] = new FirmwareFile(file, await files.ReadAsync(file, token).ConfigureAwait(false)); }
                    catch (PftpResponseException error) when (error.ErrorCode == 103) { }
            }
            return output.Values.ToArray();
        }
        public static async Task RestoreSettingsAsync(PolarFileService files, IReadOnlyList<FirmwareFile> backup, CancellationToken token = default)
        {
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in backup)
            {
                if (file.Path.StartsWith("/U/0/", StringComparison.Ordinal))
                {
                    int at = 3;
                    while ((at = file.Path.IndexOf('/', at + 1)) >= 0)
                    {
                        string directory = file.Path.Substring(0, at + 1);
                        if (directories.Add(directory))
                            try { await files.CreateFolderAsync(directory, token).ConfigureAwait(false); }
                            catch (PftpResponseException error) when (error.ErrorCode == 104) { }
                    }
                }
                await files.WriteAsync(file.Path, file.Data, token).ConfigureAwait(false);
            }
        }
        private sealed class InlineProgress<T> : IProgress<T>
        { private readonly Action<T> _action; public InlineProgress(Action<T> action) { _action = action; } public void Report(T value) => _action(value); }
    }
}
