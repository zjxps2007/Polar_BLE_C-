using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using PolarBle;
using PolarBle.Firmware;
using PolarBle.Protocol.Features;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Protobuf;
using PolarBle.Services;
using PolarBle.Transport;

namespace Polar.Tests
{
    internal static class FirmwareTests
    {
        public static void Run()
        {
            Catalog(); Preflight().GetAwaiter().GetResult(); BackupAndRestore().GetAwaiter().GetResult();
            UpdateLifecycle().GetAwaiter().GetResult(); FailureCleanup().GetAwaiter().GetResult();
        }
        private static byte[] Zip(params (string Name, byte[] Data)[] files)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var file in files) { using var entry = archive.CreateEntry(file.Name).Open(); entry.Write(file.Data, 0, file.Data.Length); }
            return output.ToArray();
        }
        private static void Catalog()
        {
            Assert.True(FirmwareCatalog.IsNewer("2.0.0", "2.0.1")); Assert.False(FirmwareCatalog.IsNewer("3.0.0", "2.9.9"));
            Assert.Equal(0, FirmwareCatalog.CompareVersions("1.2", "1.2.0"));
            Assert.Throws<ArgumentException>(() => FirmwareCatalog.CompareVersions("1.+2.3", "1.0.0"));
            Assert.Throws<ArgumentException>(() => FirmwareCatalog.CompareVersions("1..3", "1.0.0"));
            Assert.Throws<ArgumentException>(() => FirmwareCatalog.CompareVersions("", "1.0.0"));
            Assert.Equal("0e030000-0084-0000-0000-0000abcdef12", FirmwareCatalog.DeviceUuid("ABCDEF12"));
            var package = FirmwareCatalog.ParsePackage(Zip(("SYSUPDAT.IMG", new byte[] { 1 }), ("readme.txt", new byte[] { 0 }), ("BTUPDAT.BIN", new byte[] { 2 })));
            Assert.Equal(2, package.Count); Assert.Equal("/BTUPDAT.BIN", package[0].Path); Assert.Equal("/SYSUPDAT.IMG", package[1].Path);
            Assert.Throws<InvalidDataException>(() => FirmwareCatalog.ParsePackage(Zip(("../SYSUPDAT.IMG", new byte[] { 1 }))));
            Assert.Throws<InvalidDataException>(() => FirmwareCatalog.ParsePackage(Zip(("SYSUPDAT.IMG", new byte[] { 1 }), ("sysupdat.img", new byte[] { 2 }))));
            Assert.Throws<InvalidDataException>(() => FirmwareCatalog.ParsePackage(Zip(("readme.txt", new byte[] { 0 }))));
            Assert.Throws<InvalidDataException>(() => FirmwareCatalog.ParsePackage(new byte[] { 1, 2, 3 }));
        }
        private static async Task Preflight()
        {
            int sessions = 0;
            var updater = new FirmwareUpdater((_, __) => { sessions++; throw new InvalidOperationException("Device must not be touched."); });
            Task Save(IReadOnlyList<FirmwareFile> _, CancellationToken __) => Task.CompletedTask;
            foreach (var package in new[]
            {
                new[] { new FirmwareFile("SYSUPDAT.IMG", new byte[] { 1 }) },
                new[] { new FirmwareFile("/../SYSUPDAT.IMG", new byte[] { 1 }) },
                new[] { new FirmwareFile("/SYSUPDAT.IMG", Array.Empty<byte>()) },
                new[] { new FirmwareFile("/SYSUPDAT.IMG", null!) },
                new[] { new FirmwareFile("/SYSUPDAT.IMG", new byte[] { 1 }), new FirmwareFile("/sysupdat.img", new byte[] { 2 }) },
                new FirmwareFile[] { null! }
            }) await Assert.ThrowsAsync<ArgumentException>(() => updater.UpdateAsync(package, "1.2.3", Save));
            await Assert.ThrowsAsync<ArgumentException>(() => updater.UpdateAsync(new[] { new FirmwareFile("/SYSUPDAT.IMG", new byte[] { 1 }) }, "invalid", Save));
            Assert.Equal(0, sessions);
        }
        private static async Task BackupAndRestore()
        {
            using var device = new FirmwareFakeDevice();
            device.Files["/SYS/BACKUP.TXT"] = Encoding.UTF8.GetBytes("/LOG/*/S/\n/ABSENT/\n");
            device.Files["/U/0/USERID.BPB"] = new byte[] { 1 };
            device.Files["/LOG/A/S/first.bin"] = new byte[] { 2 };
            device.Files["/LOG/B/S/deep/second.bin"] = new byte[] { 3 };
            device.Files["/LOG/B/skip.bin"] = new byte[] { 4 };
            var session = await device.SessionAsync(false, CancellationToken.None);
            var backup = await FirmwareUpdater.BackupSettingsAsync(session.Files);
            Assert.Equal(3, backup.Count); Assert.True(backup.Any(x => x.Path == "/LOG/B/S/deep/second.bin"));
            Assert.False(backup.Any(x => x.Path == "/LOG/B/skip.bin"));
            device.RejectExistingDirectories = true;
            await FirmwareUpdater.RestoreSettingsAsync(session.Files, new[]
            {
                new FirmwareFile("/U/0/USERID.BPB", new byte[] { 9 }),
                new FirmwareFile("/U/0/S/PHYSDATA.BPB", new byte[] { 8 }),
                new FirmwareFile("/U/0/S/PREFS.BPB", new byte[] { 7 })
            });
            Assert.SequenceEqual(new[] { "/U/0/", "/U/0/S/" }, device.Writes.Where(x => x.Path.EndsWith("/", StringComparison.Ordinal)).Select(x => x.Path));
            Assert.SequenceEqual(new byte[] { 9 }, device.Files["/U/0/USERID.BPB"]);
            Assert.SequenceEqual(new byte[] { 8 }, device.Files["/U/0/S/PHYSDATA.BPB"]);
        }
        private static async Task UpdateLifecycle()
        {
            using var device = new FirmwareFakeDevice { MultiConnection = true, RebootOnFirmware = true };
            device.Files["/U/0/USERID.BPB"] = new byte[] { 42 };
            var progress = new List<FirmwareUpdateStage>(); byte[] image = { 10, 20 };
            var updater = new FirmwareUpdater(device.SessionAsync) { RebootDelay = TimeSpan.Zero };
            await updater.UpdateAsync(new[] { new FirmwareFile("/SYSUPDAT.IMG", image) }, "1.2.3", (backup, _) =>
            {
                Assert.Equal(1, backup.Count); image[0] = 99; return Task.CompletedTask;
            }, new InlineProgress(value => progress.Add(value.Stage)));
            Assert.Equal(FirmwareUpdateStage.Completed, progress.Last());
            Assert.SequenceEqual(new byte[] { 10, 20 }, device.Files["/SYSUPDAT.IMG"]);
            Assert.SequenceEqual(new byte[] { 42 }, device.Files["/U/0/USERID.BPB"]);
            Assert.True(device.MultiConnection); Assert.Equal(1, device.ResetCount);
            Assert.SequenceEqual(new[] { false, true }, device.MultiWrites);
            Assert.True(device.Sessions.Count >= 4); // Factory reset, reboot response, final verification, feature restoration use fresh clients.
            Assert.True(device.Notifications.Any(x => x[0] == 1 && PbPFtpStopSyncParams.Parser.ParseFrom(x.Skip(1).ToArray()).Completed));
        }
        private static async Task FailureCleanup()
        {
            using (var device = new FirmwareFakeDevice { MultiConnection = true })
            {
                var updater = new FirmwareUpdater(device.SessionAsync) { RebootDelay = TimeSpan.Zero };
                var original = new IOException("Backup persistence failed.");
                var error = await Assert.ThrowsAsync<IOException>(() => updater.UpdateAsync(Package(), "1.2.3", (_, __) => throw original));
                Assert.True(ReferenceEquals(original, error)); Assert.Equal(0, device.ResetCount); Assert.True(device.MultiConnection);
                Assert.True(device.Notifications.Any(x => x[0] == 1 && !PbPFtpStopSyncParams.Parser.ParseFrom(x.Skip(1).ToArray()).Completed));
                Assert.True(device.Notifications.Any(x => x[0] == 9));
            }
            using (var device = new FirmwareFakeDevice { MultiConnection = true, FirmwareError = 204 })
            {
                device.Files["/U/0/USERID.BPB"] = new byte[] { 55 };
                var stages = new List<FirmwareUpdateStage>(); var updater = new FirmwareUpdater(device.SessionAsync) { RebootDelay = TimeSpan.Zero };
                var error = await Assert.ThrowsAsync<FirmwareUpdateException>(() => updater.UpdateAsync(Package(), "1.2.3", (_, __) => Task.CompletedTask, new InlineProgress(value => stages.Add(value.Stage))));
                Assert.True(error.InnerException is PftpResponseException); Assert.Equal(1, error.SettingsBackup.Count);
                Assert.True(device.MultiConnection); Assert.False(stages.Contains(FirmwareUpdateStage.Completed));
            }
            using (var device = new FirmwareFakeDevice { MultiConnection = true })
            using (var cancel = new CancellationTokenSource())
            {
                var updater = new FirmwareUpdater(device.SessionAsync) { RebootDelay = TimeSpan.Zero };
                await Assert.ThrowsAsync<OperationCanceledException>(() => updater.UpdateAsync(Package(), "1.2.3", (_, token) =>
                { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cancellationToken: cancel.Token));
                Assert.True(device.MultiConnection); Assert.Equal(0, device.ResetCount);
            }
            using (var device = new FirmwareFakeDevice())
            {
                var updater = new FirmwareUpdater(device.SessionAsync) { RebootDelay = TimeSpan.Zero };
                var error = await Assert.ThrowsAsync<FirmwareUpdateException>(() => updater.UpdateAsync(Package(), "9.9.9", (_, __) => Task.CompletedTask));
                Assert.True(error.InnerException is PolarProtocolException);
                Assert.True(device.Notifications.Any(x => x[0] == 1 && !PbPFtpStopSyncParams.Parser.ParseFrom(x.Skip(1).ToArray()).Completed));
            }
            using (var device = new FirmwareFakeDevice { MultiConnection = true })
            {
                async Task<FirmwareSession> Factory(bool reconnect, CancellationToken token)
                { if (reconnect) throw new IOException("Restoration connection failed."); return await device.SessionAsync(false, token); }
                var updater = new FirmwareUpdater(Factory) { RebootDelay = TimeSpan.Zero };
                var error = await Assert.ThrowsAsync<FirmwareUpdateException>(() => updater.UpdateAsync(Package(), "1.2.3", (_, __) => throw new InvalidOperationException("Persist failed.")));
                Assert.True(error.InnerException is InvalidOperationException); Assert.True(error.CleanupError is IOException); Assert.Equal(0, device.ResetCount);
            }
            using (var device = new FirmwareFakeDevice())
            {
                async Task<FirmwareSession> Factory(bool reconnect, CancellationToken token)
                { if (reconnect) await Task.Delay(Timeout.Infinite, token); return await device.SessionAsync(false, token); }
                var updater = new FirmwareUpdater(Factory) { RebootDelay = TimeSpan.Zero, ReconnectTimeout = TimeSpan.FromMilliseconds(10) };
                var error = await Assert.ThrowsAsync<FirmwareUpdateException>(() => updater.UpdateAsync(Package(), "1.2.3", (_, __) => Task.CompletedTask));
                Assert.True(error.InnerException is TimeoutException);
            }
        }
        private static FirmwareFile[] Package() => new[] { new FirmwareFile("/SYSUPDAT.IMG", new byte[] { 1 }) };
        private sealed class InlineProgress : IProgress<FirmwareProgress>
        { private readonly Action<FirmwareProgress> _handler; public InlineProgress(Action<FirmwareProgress> handler) { _handler = handler; } public void Report(FirmwareProgress value) => _handler(value); }
    }

    // In-memory BLE peer: no Windows transport or physical device is used in these tests.
    internal sealed class FirmwareFakeDevice : IDisposable
    {
        public Dictionary<string, byte[]> Files { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        public List<FirmwareFile> Writes { get; } = new List<FirmwareFile>();
        public List<byte[]> Notifications { get; } = new List<byte[]>();
        public List<bool> MultiWrites { get; } = new List<bool>();
        public List<FirmwareSession> Sessions { get; } = new List<FirmwareSession>();
        private readonly List<FirmwareFakeConnection> _connections = new List<FirmwareFakeConnection>();
        public bool MultiConnection, RejectExistingDirectories, RebootOnFirmware;
        public ushort? FirmwareError;
        public int ResetCount;
        public FirmwareFakeDevice() => Files["/DEVICE.BPB"] = Version("1.0.0");
        private static byte[] Version(string text)
        { var pieces = text.Split('.'); return new PbDeviceInfo { DeviceVersion = new PbVersion { Major = uint.Parse(pieces[0]), Minor = uint.Parse(pieces[1]), Patch = uint.Parse(pieces[2]) }, HardwareCode = "1234.01" }.ToByteArray(); }
        public async Task<FirmwareSession> SessionAsync(bool reconnect, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var connection = new FirmwareFakeConnection { Device = this }; _connections.Add(connection);
            var pftp = new PftpClient(connection); await pftp.InitializeAsync(token);
            var files = new PolarFileService(pftp); var features = new PolarFeatureClient(connection);
            var session = new FirmwareSession(pftp, files, new PolarDeviceService(pftp, files), features: features); Sessions.Add(session); return session;
        }
        internal byte[] Handle(PbPFtpOperation operation, byte[] payload)
        {
            if (operation.Command == PbPFtpOperation.Types.Command.Put)
            {
                Writes.Add(new FirmwareFile(operation.Path, (byte[])payload.Clone()));
                if (operation.Path.EndsWith("/", StringComparison.Ordinal))
                { if (RejectExistingDirectories) throw new PftpResponseException(104); return Array.Empty<byte>(); }
                if (operation.Path == "/SYSUPDAT.IMG" && FirmwareError.HasValue) throw new PftpResponseException(FirmwareError.Value);
                Files[operation.Path] = payload;
                if (operation.Path == "/SYSUPDAT.IMG") { Files["/DEVICE.BPB"] = Version("1.2.3"); if (RebootOnFirmware) throw new PftpResponseException(1); }
                return Array.Empty<byte>();
            }
            if (Files.TryGetValue(operation.Path, out var found)) return found;
            if (operation.Path.EndsWith("/", StringComparison.Ordinal))
            {
                var children = Files.Keys.Where(x => x.StartsWith(operation.Path, StringComparison.Ordinal)).Select(x => x.Substring(operation.Path.Length)).Where(x => x.Length > 0)
                    .Select(x => x.Contains("/") ? x.Substring(0, x.IndexOf('/') + 1) : x).Distinct().ToArray();
                if (children.Length == 0) throw new PftpResponseException(103);
                var result = new PbPFtpDirectory(); foreach (string child in children) result.Entries.Add(new PbPFtpEntry { Name = child }); return result.ToByteArray();
            }
            throw new PftpResponseException(103);
        }
        internal void HandleNotification(byte[] message)
        {
            Notifications.Add(message);
            if (message[0] == 2)
            {
                var reset = PbPFtpFactoryResetParams.Parser.ParseFrom(message.Skip(1).ToArray());
                if (reset.DoFactoryDefaults) { ResetCount++; foreach (var key in Files.Keys.Where(x => x.StartsWith("/U/", StringComparison.Ordinal)).ToArray()) Files.Remove(key); }
            }
        }
        public void Dispose()
        { foreach (var session in Sessions) { session.Features?.Dispose(); session.Pftp.Dispose(); } foreach (var connection in _connections) connection.Dispose(); }
    }

    internal sealed class FirmwareFakeConnection : IBleConnection
    {
        public FirmwareFakeDevice? Device;
        public Func<PbPFtpOperation, byte[], byte[]?>? OnOperation;
        public List<PbPFtpOperation> Operations { get; } = new List<PbPFtpOperation>();
        public string DeviceId => "fake-firmware-device";
        public bool IsConnected { get; private set; } = true;
        public int MaxWriteSize => 512;
        public event EventHandler<BleNotification>? NotificationReceived;
        public event EventHandler? Disconnected;
        public event EventHandler<BleTransportError>? ErrorOccurred { add { } remove { } }
        private PftpAssembler? _request, _notification;
        public Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BleGattService>>(Array.Empty<BleGattService>());
        public Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default) => Task.FromResult(new byte[] { 128, 0 });
        public Task SetNotificationsAsync(Guid serviceUuid, Guid characteristicUuid, bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withResponse, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (serviceUuid == PolarFeatureClient.ServiceUuid)
            {
                if (data[0] == 8) { Device!.MultiConnection = data[1] != 0; Device.MultiWrites.Add(Device.MultiConnection); }
                Emit(PolarFeatureClient.ServiceUuid, PolarFeatureClient.ControlPointUuid, data[0] == 9 ? new byte[] { 240, data[0], 1, (byte)(Device!.MultiConnection ? 1 : 0) } : new byte[] { 240, data[0], 1 });
                return Task.CompletedTask;
            }
            if (characteristicUuid == PftpClient.H2dUuid)
            {
                _notification ??= new PftpAssembler();
                if (_notification.Add(data)) { var message = _notification.GetResult(); _notification.Dispose(); _notification = null; Device?.HandleNotification(message); }
                return Task.CompletedTask;
            }
            if (data.Length == 3 && data.All(x => x == 0)) return Task.CompletedTask;
            _request ??= new PftpAssembler();
            if (_request.Add(data))
            {
                var message = _request.GetResult(); _request.Dispose(); _request = null;
                try
                {
                    if ((message[1] & 128) != 0) Respond(Array.Empty<byte>());
                    else
                    {
                        int length = message[0] | (message[1] << 8); var operation = PbPFtpOperation.Parser.ParseFrom(message.Skip(2).Take(length).ToArray());
                        var payload = message.Skip(2 + length).ToArray(); Operations.Add(operation);
                        var result = OnOperation != null ? OnOperation(operation, payload) : Device!.Handle(operation, payload);
                        if (result != null) Respond(result);
                    }
                }
                catch (PftpResponseException error) { Emit(PftpClient.ServiceUuid, PftpClient.MtuUuid, new byte[] { 0, (byte)error.ErrorCode, (byte)(error.ErrorCode >> 8) }); }
            }
            return Task.CompletedTask;
        }
        private void Emit(Guid service, Guid characteristic, byte[] data) => NotificationReceived?.Invoke(this, new BleNotification(service, characteristic, data));
        public void Respond(byte[] data)
        {
            if (data.Length == 0) Emit(PftpClient.ServiceUuid, PftpClient.MtuUuid, new byte[] { 0, 0, 0 });
            else foreach (var frame in PftpFraming.Fragment(data, MaxWriteSize)) Emit(PftpClient.ServiceUuid, PftpClient.MtuUuid, frame);
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { IsConnected = false; Disconnected?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public void Dispose() { _request?.Dispose(); _notification?.Dispose(); }
    }
}
