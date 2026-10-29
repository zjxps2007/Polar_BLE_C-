using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using PolarBle;
using PolarBle.Models;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Protobuf;
using PolarBle.Services;
using PolarBle.Transport;

namespace Polar.Tests
{
    internal static class PftpTests
    {
        public static void Run()
        {
            Framing();
            Exchange().GetAwaiter().GetResult();
            Services().GetAwaiter().GetResult();
            OfflineServices().GetAwaiter().GetResult();
            DataModels();
            ExerciseFiles().GetAwaiter().GetResult();
            StoredDeletion().GetAwaiter().GetResult();
            CancellationAndDisconnect().GetAwaiter().GetResult();
        }
        private static void Framing()
        {
            Assert.SequenceEqual(new byte[] { 0x34, 0x92, 5 }, PftpFraming.Query(0x1234, new byte[] { 5 }));
            Assert.SequenceEqual(new byte[] { 2, 0, 8, 1, 9, 10 }, PftpFraming.Request(new byte[] { 8, 1 }, new byte[] { 9, 10 }));
            Assert.SequenceEqual(new byte[] { 8, 3 }, PftpFraming.Notification(8, new byte[] { 3 }));
            var frames = PftpFraming.Fragment(new byte[] { 1, 2, 3, 4, 5 }, 3).ToArray();
            Assert.SequenceEqual(new byte[] { 6, 1, 2 }, frames[0]);
            Assert.SequenceEqual(new byte[] { 23, 3, 4 }, frames[1]);
            Assert.SequenceEqual(new byte[] { 35, 5 }, frames[2]);
            using var assembly = new PftpAssembler();
            Assert.False(assembly.Add(frames[0])); Assert.False(assembly.Add(frames[1])); Assert.True(assembly.Add(frames[2]));
            Assert.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }, assembly.GetResult());
            using var bad = new PftpAssembler();
            Assert.Throws<PolarProtocolException>(() => bad.Add(frames[1]));
            using var error = new PftpAssembler();
            Assert.Equal((ushort)303, Assert.Throws<PftpResponseException>(() => error.Add(new byte[] { 0, 0x2f, 1 })).ErrorCode);
            using var truncated = new PftpAssembler(); Assert.Throws<PolarProtocolException>(() => truncated.Add(new byte[] { 0, 0 }));
            using var overflow = new PftpAssembler(2); Assert.Throws<PolarProtocolException>(() => overflow.Add(new byte[] { 2, 1, 2, 3 }));
            var payload = Enumerable.Range(0, 400).Select(x => (byte)x).ToArray();
            using var wrap = new PftpAssembler();
            foreach (var frame in PftpFraming.Fragment(payload, 10)) wrap.Add(frame);
            Assert.SequenceEqual(payload, wrap.GetResult());
            Assert.Throws<ArgumentOutOfRangeException>(() => PftpFraming.Query(0x8000));
            // Published protobuf wire layout: GET=0 (field 1), path='/' (field 2).
            Assert.SequenceEqual(new byte[] { 8, 0, 18, 1, 47 }, new PbPFtpOperation { Command = PbPFtpOperation.Types.Command.Get, Path = "/" }.ToByteArray());
        }
        private static async Task Exchange()
        {
            using var connection = new FakeConnection();
            using var client = new PftpClient(connection);
            await client.InitializeAsync();
            Assert.Equal(2, connection.Subscriptions.Count);
            connection.Response = message => Enumerable.Range(0, 100).Select(x => (byte)x).ToArray();
            Assert.SequenceEqual(Enumerable.Range(0, 100).Select(x => (byte)x), await client.QueryAsync(5));
            Assert.SequenceEqual(new byte[] { 5, 128 }, connection.Messages[0]);
            connection.Response = _ => Array.Empty<byte>();
            await client.WriteAsync(new byte[] { 8, 1 }, Enumerable.Repeat((byte)17, 100).ToArray());
            Assert.Equal(104, connection.Messages[1].Length);
            Assert.True(connection.WritesWithResponse > 0);
            var notificationCompletion = new TaskCompletionSource<PftpNotification>();
            client.NotificationReceived += (_, item) => notificationCompletion.TrySetResult(item);
            connection.EmitD2h(PftpFraming.Notification(18, new byte[] { 1, 2, 3 }));
            var notification = await notificationCompletion.Task;
            Assert.Equal((byte)18, notification.Id); Assert.SequenceEqual(new byte[] { 1, 2, 3 }, notification.Parameters);
            // Caller-visible device errors do not poison the transport; the next request can succeed.
            connection.StatusError = 202;
            Assert.Equal((ushort)202, (await Assert.ThrowsAsync<PftpResponseException>(() => client.QueryAsync(4))).ErrorCode);
            connection.StatusError = null;
            Assert.Equal(0, (await client.QueryAsync(4)).Length);
            // Two overlapping application requests serialize, even when the first awaits a notification.
            connection.Response = null;
            var first = client.QueryAsync(1); int count = connection.Messages.Count;
            var second = client.QueryAsync(2);
            Assert.Equal(count, connection.Messages.Count);
            connection.Response = _ => new byte[] { 22 };
            connection.EmitMtu(new byte[] { 2, 11 });
            Assert.SequenceEqual(new byte[] { 11 }, await first);
            Assert.SequenceEqual(new byte[] { 22 }, await second);
        }
        private static async Task Services()
        {
            using var connection = new FakeConnection(); using var client = new PftpClient(connection);
            await client.InitializeAsync(); var files = new PolarFileService(client);
            connection.Response = request =>
            {
                int size = request[0] | (request[1] << 8);
                var operation = PbPFtpOperation.Parser.ParseFrom(request.Skip(2).Take(size).ToArray());
                if (operation.Path == "/")
                {
                    var directory = new PbPFtpDirectory(); directory.Entries.Add(new PbPFtpEntry { Name = "test.BPB", Size = 5 });
                    return directory.ToByteArray();
                }
                if (operation.Path == "/U/0/20261029/DSUM/DSUM.BPB")
                    return new PbDailySummary { ActivityCalories = 123, TrainingCalories = 45, ActivityDistance = 321.5f }.ToByteArray();
                return new byte[] { 5, 6, 7 };
            };
            var listing = await files.ListAsync(); Assert.Equal("/test.BPB", listing[0].Path); Assert.Equal(5UL, listing[0].Size);
            Assert.SequenceEqual(new byte[] { 5, 6, 7 }, await files.ReadAsync("/test.BPB"));
            var stored = new PolarStoredDataService(files);
            var date = new DateTime(2026, 10, 29);
            Assert.Equal(123U, (await stored.GetCaloriesAsync(date, date, PolarCalorieType.Activity))[0].Value);
            Assert.Equal(321.5f, (await stored.GetDistanceAsync(date, date))[0].Value);
            Assert.Throws<ArgumentException>(() => files.ReadAsync("relative"));
            var rest = new PbPftpDHRestApiEvent { Uncompressed = true };
            rest.Event.Add(ByteString.CopyFromUtf8("{\"sleep_recording_state\":{\"enabled\":true}}"));
            var events = PolarRestService.DecodeEvents(rest.ToByteArray());
            Assert.Equal(PolarSleepRecordingStatus.Enabled, PolarRestService.ParseSleepStatus(events[0].Data));
            var missing = Google.Protobuf.JsonParser.Default.Parse<Google.Protobuf.WellKnownTypes.Struct>("{\"sleep_recording_state\":{}}");
            Assert.Equal(PolarSleepRecordingStatus.Unknown, PolarRestService.ParseSleepStatus(missing));
            connection.Response = _ => Array.Empty<byte>(); int beforeTime = connection.Messages.Count;
            await new PolarDeviceService(client, files, h10FileSystem: true).SetLocalTimeAsync(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.FromHours(9)));
            Assert.Equal(beforeTime + 1, connection.Messages.Count); Assert.Equal((byte)3, connection.Messages.Last()[0]);
            var firstUse = new PolarFirstTimeUseConfiguration(PolarGender.Female, new DateTime(1990, 5, 6), 170, 60, 190, 55, 45, 30, PolarTypicalDay.MostlyMoving, 480,
                new DateTimeOffset(2026, 10, 29, 1, 2, 3, TimeSpan.FromHours(9)));
            await new PolarDeviceService(client, files).ConfigureFirstTimeUseAsync(firstUse);
            var userRequest = connection.Messages.Last(request => (request[1] & 0x80) == 0 && Operation(request).Path == "/U/0/USERID.BPB");
            int userHeader = 2 + userRequest[0] + (userRequest[1] << 8);
            var user = PbUserIdentifier.Parser.ParseFrom(userRequest.Skip(userHeader).ToArray());
            Assert.Equal(ulong.MaxValue, user.MasterIdentifier); Assert.Equal(28U, user.UserIdLastModified.Date.Day); Assert.Equal(16U, user.UserIdLastModified.Time.Hour);
        }
        private static async Task CancellationAndDisconnect()
        {
            using (var connection = new FakeConnection())
            using (var client = new PftpClient(connection))
            {
                await client.InitializeAsync(); using var cancel = new CancellationTokenSource();
                var pending = client.QueryAsync(5, null, cancel.Token); cancel.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
                await Assert.ThrowsAsync<PolarFeatureNotAvailableException>(() => client.QueryAsync(5));
                Assert.True(connection.Aborted);
            }
            using (var connection = new FakeConnection())
            using (var client = new PftpClient(connection))
            {
                await client.InitializeAsync(); var pending = client.QueryAsync(5);
                await connection.DisconnectAsync();
                await Assert.ThrowsAsync<PolarDisconnectedException>(() => pending);
            }
        }

        private static async Task OfflineServices()
        {
            const string prefix = "/U/0/20261029/R/123456/";
            var entries = PolarOfflineRecordingService.ParseIndex(Encoding.UTF8.GetBytes("20 " + prefix + "ACC1.REC\n10 " + prefix + "ACC0.REC\n5 " + prefix + "HR.REC\n"));
            Assert.Equal(2, entries.Count); Assert.Equal(30UL, entries[0].Size);
            Assert.Equal(prefix + "ACC0.REC", entries[0].Parts[0].Path);
            Assert.Equal(prefix + "ACC.REC", entries[0].Path);
            Assert.Equal(3, PolarOfflineRecordingService.ParseIndex(Encoding.UTF8.GetBytes("20 " + prefix + "ACC1.REC\n10 " + prefix + "ACC0.REC\n5 " + prefix + "HR.REC\n"), true).Count);
            Assert.Throws<PolarProtocolException>(() => PolarOfflineRecordingService.ParseIndex(Encoding.UTF8.GetBytes("10 " + prefix + "ACC1.REC")));
            Assert.Throws<PolarProtocolException>(() => PolarOfflineRecordingService.ParseIndex(Encoding.UTF8.GetBytes("10 " + prefix + "ACC0.REC\n10 " + prefix + "ACC0.REC")));
            var automatic = new PbAutomaticSampleSessions { Day = new PbDate { Year = 2026, Month = 10, Day = 29 } };
            var ppi = new PbPpIntervalSamples(); ppi.PpiDelta.Add(new[] { 900, -20 }); ppi.PpiErrorEstimateDelta.Add(new[] { 4, 1 }); ppi.Status.Add(new uint[] { 1, 7 });
            automatic.PpiSamples.Add(new PbPpIntervalAutoSamples { RecordingTime = new PbTime { Hour = 9, Minute = 10, Seconds = 0 }, Ppi = ppi });
            var ppiSessions = PolarStoredDataService.DecodePpiSessions(automatic);
            Assert.Equal(880, ppiSessions[0].Samples[1].IntervalMilliseconds); Assert.Equal(5, ppiSessions[0].Samples[1].ErrorEstimateMilliseconds);
            Assert.True(ppiSessions[0].Samples[1].SkinContact); Assert.True(ppiSessions[0].Samples[1].MovementDetected); Assert.True(ppiSessions[0].Samples[1].OfflinePeriod);
            // Real upstream temperature recording bytes transported through the file service and parser.
            int[] source = { 0,43,76,124,61,1,0,0,0,0,0,0,0,-89,-40,-1,-78,50,48,50,50,45,48,49,45,48,50,32,49,55,58,50,53,58,50,57,0,11,0,1,4,0,1,1,32,0,4,1,1,0,42,0,12,-101,61,-62,15,109,16,0,0,0,0,48,-93,65,0,48,-93,65,0,32,-93,65,0,32,-93,65,0,48,-93,65,0,48,-93,65,0,48,-93,65,0,32,-93,65 };
            using var connection = new FakeConnection(); using var client = new PftpClient(connection); await client.InitializeAsync();
            connection.Response = _ => source.Select(x => unchecked((byte)x)).ToArray();
            var entry = PolarOfflineRecordingService.ParseIndex(Encoding.UTF8.GetBytes(source.Length + " " + prefix + "TEMP.REC"))[0];
            var recording = await new PolarOfflineRecordingService(new PolarFileService(client)).DownloadAsync(entry);
            Assert.Equal(8, recording.Batches[0].Samples.Count); Assert.Equal((double)20.398438f, recording.Batches[0].Samples[0].Values[0]);
        }

        private static void DataModels()
        {
            var date = new PbDate { Year = 2026, Month = 10, Day = 29 };
            var local = new PbLocalDateTime { Date = date, Time = new PbTime { Hour = 23, Minute = 59, Seconds = 58, Millis = 123 }, TimeZoneOffset = 540 };
            var converted = PolarDataTime.Local(local);
            Assert.Equal(DateTimeKind.Unspecified, converted.LocalTime.Kind);
            Assert.Equal(TimeSpan.FromHours(9), converted.TimeWithOffset!.Value.Offset);
            Assert.Equal(123, converted.LocalTime.Millisecond);
            local.ClearTimeZoneOffset(); Assert.False(PolarDataTime.Local(local).TimeWithOffset.HasValue);
            Assert.Throws<PolarProtocolException>(() => PolarDataTime.Time(new PbTime { Hour = 24 }));
            Assert.Equal(TimeSpan.FromHours(25) + TimeSpan.FromMilliseconds(3), PolarDataTime.Duration(new PbDuration { Hours = 25, Millis = 3 }));
            var summary = new PolarDailySummary(new DateTime(2026, 10, 29), new PbDailySummary
            { Date = date, Steps = 0, ActivityCalories = 0, ActivityClassTimes = new PbActivityClassTimes { TimeSleep = new PbDuration { Hours = 7, Minutes = 42 } } });
            Assert.Equal(0U, summary.Steps!.Value); Assert.False(summary.TrainingCalories.HasValue);
            Assert.Equal(TimeSpan.FromMinutes(462), summary.ActiveTime!.Sleep!.Value); Assert.False(summary.ActiveTime.NonWear.HasValue);
            var activity = new PbActivitySamples { StartTime = local, MetRecordingInterval = new PbDuration { Seconds = 30 }, StepsRecordingInterval = new PbDuration { Seconds = 60 } };
            activity.ActivityInfo.Add(new PbActivityInfo { Value = (PbActivityInfo.Types.ActivityClass)99, TimeStamp = local, Factor = 0 });
            var activityData = new PolarActivitySamples("/ACT/ASAMPL.BPB", activity);
            Assert.Equal((PolarActivityClass)99, activityData.ActivityChanges[0].Class!.Value); Assert.Equal(0f, activityData.ActivityChanges[0].Factor!.Value);
            var sleep = new PbSleepAnalysisResult
            {
                SleepStartTime = local, SleepEndTime = new PbLocalDateTime { Date = new PbDate { Year = 2026, Month = 10, Day = 30 }, Time = new PbTime { Hour = 7 }, TimeZoneOffset = 540 },
                LastModified = new PbSystemDateTime { Date = date, Time = new PbTime { Hour = 20 }, Trusted = true }, SleepGoalMinutes = 480, BatteryRanOut = false
            };
            sleep.SleepwakePhases.Add(new PbSleepWakePhase { SecondsFromSleepStart = 90, SleepwakeState = (PbSleepWakeState)(-3) });
            var sleepData = new PolarSleepData(new PolarSleepResult(new DateTime(2026, 10, 30), sleep, null));
            Assert.False(sleepData.Start.TimeWithOffset.HasValue); Assert.Equal(TimeSpan.Zero, sleepData.LastModifiedUtc.Offset);
            Assert.Equal(TimeSpan.FromHours(8), sleepData.Goal); Assert.Equal(PolarSleepWakeState.Rem, sleepData.Phases[0].State);
            Assert.Equal(TimeSpan.FromSeconds(90), sleepData.Phases[0].Offset); Assert.False(sleepData.BatteryRanOut!.Value);
            Assert.True(sleepData.Temperature == null);
            var samples = new PbExerciseSamples { RecordingInterval = new PbDuration { Seconds = 5 }, RrSamples = new PbExerciseRRIntervals() };
            samples.RrSamples.RrIntervals.Add(new uint[] { 900, 880 }); samples.HeartRateSamples.Add(70U);
            var exercise = new PolarExerciseData(samples);
            Assert.True(exercise.IsRrInterval); Assert.SequenceEqual(new uint[] { 900, 880 }, exercise.Samples); Assert.Equal(TimeSpan.FromSeconds(5), exercise.RecordingInterval);
            var logProto = PbSensorDataLog.Parser.ParseFrom(new byte[] { 0x08, 0x02, 0xa8, 0x06, 0x07 }); // trigger=exercise + unknown future field101=7
            var logs = new PolarLogConfiguration(logProto) { OhrLogEnabled = false, ExternalHeartRateSensorName = "Polar H10 TEST", WaitForExternalHeartRateSensor = true };
            Assert.False(logs.OhrLogEnabled!.Value); Assert.False(logs.PpiLogEnabled.HasValue);
            Assert.Equal("Polar H10 TEST", logs.ToProto().BleHrSensorConfig.DeviceName.Name);
            logs.OhrLogEnabled = null; Assert.False(logs.ToProto().HasOhrLogEnabled);
            var logBytes = logs.ToProto().ToByteArray(); Assert.SequenceEqual(new byte[] { 0xa8, 0x06, 0x07 }, logBytes.Skip(logBytes.Length - 3));
            var spo2 = new PolarSpo2TestData("/U/0/20261029/SPO2TEST/123456/SPO2TRES.BPB", new PbSpo2TestResult { TimeZoneOffset = 540, BloodOxygenPercent = 0, TestTime = 1 });
            Assert.Equal(12, spo2.TestTime!.Value.Hour); Assert.Equal(TimeSpan.FromHours(9), spo2.TestTime.Value.Offset);
            Assert.Equal(0, spo2.BloodOxygenPercent!.Value); Assert.False(spo2.HeartRateVariabilityMs.HasValue);
            var restDescription = new PolarRestDescription(Google.Protobuf.JsonParser.Default.Parse<Google.Protobuf.WellKnownTypes.Struct>("{\"events\":[\"sleep_recording_state\"],\"cmd\":{\"subscribe\":\"/REST/SLEEP.API?cmd=subscribe\"},\"sleep_recording_state\":{\"details\":[\"enabled\"],\"triggers\":[\"changed\"]}}"));
            Assert.Equal("enabled", restDescription.EventDetails("sleep_recording_state")[0]); Assert.Equal("changed", restDescription.EventTriggers("sleep_recording_state")[0]);
            Assert.Equal(0, restDescription.EventDetails("missing").Count); Assert.True(restDescription.Actions.ContainsKey("subscribe"));
            var configuration = new PolarFirstTimeUseConfiguration(PolarGender.Male, new DateTime(1991, 3, 4), 180, 75, 185, 60, 50, 40, PolarTypicalDay.MostlyStanding, 450,
                new DateTimeOffset(2026, 10, 29, 1, 0, 0, TimeSpan.FromHours(9)));
            var physical = new PolarPhysicalConfiguration(configuration.ToProto());
            Assert.Equal(75f, physical.WeightKilograms!.Value); Assert.Equal(450U, physical.SleepGoalMinutes!.Value);
            Assert.Equal(28, physical.LastModifiedUtc!.Value.Day); Assert.Equal(40, physical.TrainingBackground!.Value);
            Assert.Throws<ArgumentOutOfRangeException>(() => new PolarFirstTimeUseConfiguration(PolarGender.Male, new DateTime(1991, 3, 4), float.NaN, 75, 185, 60, 50, 40, PolarTypicalDay.MostlyStanding, 450, DateTimeOffset.UtcNow));
            var settings = new PolarUserDeviceConfiguration(PbUserDeviceSettings.Parser.ParseFrom(new byte[] { 0xc0, 0x07, 0x07 }))
            { UsbConnectionEnabled = false, AutomaticOhrEnabled = true, TrainingDetectionSensitivity = 0, MinimumTrainingDurationSeconds = 90, TelemetryEnabled = false };
            var settingsData = settings.ToProto(configuration.DeviceTime);
            Assert.Equal(PbUsbConnectionSettings.Types.PbUsbConnectionMode.Off, settingsData.UsbConnectionSettings.Mode);
            Assert.Equal(0U, settings.TrainingDetectionSensitivity!.Value); Assert.False(settings.AutomaticTrainingDetectionEnabled.HasValue);
            Assert.True(settings.AutomaticOhrEnabled!.Value); Assert.False(settings.TelemetryEnabled!.Value);
            var settingsBytes = settingsData.ToByteArray(); Assert.SequenceEqual(new byte[] { 0xc0, 0x07, 0x07 }, settingsBytes.Skip(settingsBytes.Length - 3));
        }

        private static async Task ExerciseFiles()
        {
            using var connection = new FakeConnection(); using var client = new PftpClient(connection); await client.InitializeAsync();
            var files = new PolarFileService(client); using var exercises = new PolarExerciseService(client, files);
            var removals = new List<string>(); var siblings = new[] { "120000/", "130000/" };
            connection.Response = request =>
            {
                var operation = Operation(request);
                if (operation.Command == PbPFtpOperation.Types.Command.Remove) { removals.Add(operation.Path); return Array.Empty<byte>(); }
                if (operation.Path == "/U/0/20261029/E/") return Directory(siblings.Select(x => (x, 0UL)));
                throw new InvalidOperationException("Unexpected file request: " + operation.Path);
            };
            await exercises.RemoveOfflineExerciseAsync("/H10-ID/SAMPLES.BPB");
            await exercises.RemoveOfflineExerciseAsync("/U/0/20261029/E/120000/00/SAMPLES.BPB");
            Assert.SequenceEqual(new[] { "/H10-ID/SAMPLES.BPB", "/U/0/20261029/E/120000/00/SAMPLES.BPB" }, removals);
            removals.Clear(); await exercises.DeleteTrainingSessionAsync("/U/0/20261029/E/120000/");
            Assert.SequenceEqual(new[] { "/U/0/20261029/E/120000/" }, removals);
            removals.Clear(); siblings = new[] { "120000/" };
            await exercises.DeleteTrainingSessionAsync("/U/0/20261029/E/120000/");
            Assert.SequenceEqual(new[] { "/U/0/20261029/E/" }, removals);
            removals.Clear(); siblings = new[] { "130000/" };
            Assert.Equal((ushort)103, (await Assert.ThrowsAsync<PftpResponseException>(() => exercises.DeleteTrainingSessionAsync("/U/0/20261029/E/120000/"))).ErrorCode);
            Assert.Equal(0, removals.Count);
            await Assert.ThrowsAsync<ArgumentException>(() => exercises.DeleteTrainingSessionAsync("/U/0/20261029/E/../"));
            // Full PFTP path: compressed samples and a session summary become typed nested exercises without discarding bytes/unknown files.
            const string root = "/U/0/20261029/E/120000/";
            var session = new PbTrainingSession { ExerciseCount = 1, Calories = 222, Duration = new PbDuration { Minutes = 30 } };
            var sampleData = new PbExerciseSamples { RecordingInterval = new PbDuration { Seconds = 1 } }; sampleData.HeartRateSamples.Add(new uint[] { 70, 71 });
            byte[] gzip;
            using (var stream = new System.IO.MemoryStream())
            { using (var zip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionLevel.Optimal, true)) { var bytes = sampleData.ToByteArray(); zip.Write(bytes, 0, bytes.Length); } gzip = stream.ToArray(); }
            var data = new Dictionary<string, byte[]> { [root + "TSESS.BPB"] = session.ToByteArray(), [root + "00/SAMPLES.GZB"] = gzip, [root + "00/OTHER.BIN"] = new byte[] { 9, 8, 7 } };
            connection.Response = request =>
            {
                var operation = Operation(request);
                if (operation.Path == "/U/0/") return Directory(new[] { ("20261029/", 0UL), ("USERID.BPB", 9UL) });
                if (operation.Path == "/U/0/20261029/E/") return Directory(new[] { ("120000/", 0UL) });
                if (operation.Path == root) return Directory(new[] { ("TSESS.BPB", (ulong)data[root + "TSESS.BPB"].Length), ("00/", 0UL) });
                if (operation.Path == root + "00/") return Directory(new[] { ("SAMPLES.GZB", (ulong)gzip.Length), ("OTHER.BIN", 3UL) });
                return data[operation.Path];
            };
            var progress = new CaptureProgress<PolarTrainingProgress>();
            var result = await exercises.FetchTrainingSessionAsync(root, detailedProgress: progress);
            Assert.Equal(222U, result.Calories!.Value); Assert.Equal(TimeSpan.FromMinutes(30), result.Duration!.Value);
            Assert.Equal(1, result.Exercises.Count); Assert.Equal(0, result.Exercises[0].Index);
            Assert.SequenceEqual(new uint[] { 70, 71 }, result.Exercises[0].Samples!.HeartRateSamples);
            Assert.SequenceEqual(new byte[] { 9, 8, 7 }, result.OtherFiles[root + "00/OTHER.BIN"]);
            Assert.Equal((ulong)data.Values.Sum(x => x.Length), result.Reference!.TotalBytes);
            Assert.Equal(result.Reference.TotalBytes, progress.Values.Last().CompletedBytes); Assert.Equal(100.0, progress.Values.Last().Percent);
            Assert.True(progress.Values.Zip(progress.Values.Skip(1), (a, b) => b.CompletedBytes >= a.CompletedBytes).All(x => x));
            var references = await exercises.GetTrainingSessionReferencesAsync();
            Assert.Equal(1, references.Count); Assert.Equal(root, references[0].Directory); Assert.Equal(1, references[0].Exercises.Count);
            Assert.Equal(0, (await exercises.GetTrainingSessionReferencesAsync(new DateTime(2026, 10, 30))).Count);
        }
        private static PbPFtpOperation Operation(byte[] request) => PbPFtpOperation.Parser.ParseFrom(request.Skip(2).Take(request[0] | (request[1] << 8)).ToArray());
        private static byte[] Directory(IEnumerable<(string Name, ulong Size)> entries)
        { var directory = new PbPFtpDirectory(); foreach (var entry in entries) directory.Entries.Add(new PbPFtpEntry { Name = entry.Name, Size = entry.Size }); return directory.ToByteArray(); }
        private sealed class CaptureProgress<T> : IProgress<T>
        { public List<T> Values { get; } = new List<T>(); public void Report(T value) => Values.Add(value); }

        private static async Task StoredDeletion()
        {
            using var connection = new FakeConnection(); using var client = new PftpClient(connection); await client.InitializeAsync();
            var files = new PolarFileService(client); var offline = new PolarOfflineRecordingService(files);
            const string day = "/U/0/20261029/"; const string root = day + "R/120000/";
            var tree = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            { [root + "ACC0.REC"] = new byte[] { 1 }, [root + "ACC1.REC"] = new byte[] { 2 }, [root + "HR.REC"] = new byte[] { 3 }, [day + "SLEEP/SLEEPRES.BPB"] = new byte[] { 4 } };
            var directories = new HashSet<string>(StringComparer.Ordinal) { "/U/0/", day, day + "R/", root, day + "SLEEP/" };
            var removals = new List<string>();
            connection.Response = request =>
            {
                var operation = Operation(request);
                if (operation.Command == PbPFtpOperation.Types.Command.Remove)
                {
                    removals.Add(operation.Path);
                    if (!tree.Remove(operation.Path)) directories.Remove(operation.Path.TrimEnd('/') + "/");
                    return Array.Empty<byte>();
                }
                if (tree.TryGetValue(operation.Path, out var data)) return data;
                string directory = operation.Path.TrimEnd('/') + "/";
                if (!directories.Contains(directory)) throw new PftpResponseException(103);
                var entries = tree.Where(x => x.Key.StartsWith(directory, StringComparison.Ordinal) && !x.Key.Substring(directory.Length).Contains('/')).Select(x => (x.Key.Substring(directory.Length), (ulong)x.Value.Length)).ToList();
                entries.AddRange(directories.Where(x => x != directory && x.StartsWith(directory, StringComparison.Ordinal) && !x.Substring(directory.Length).TrimEnd('/').Contains('/')).Select(x => (x.Substring(directory.Length), 0UL)));
                return Directory(entries);
            };
            // Removing one split entry removes all current ACC parts while retaining another measurement.
            var entry = PolarOfflineRecordingService.ParseIndex(Encoding.UTF8.GetBytes("1 " + root + "ACC0.REC"), true)[0];
            Assert.Equal(2, await offline.GetSubRecordingCountAsync(entry)); await offline.RemoveAsync(entry);
            Assert.False(tree.ContainsKey(root + "ACC0.REC")); Assert.False(tree.ContainsKey(root + "ACC1.REC")); Assert.True(tree.ContainsKey(root + "HR.REC"));
            var heartRate = PolarOfflineRecordingService.ParseIndex(Encoding.UTF8.GetBytes("1 " + root + "HR.REC"))[0];
            Assert.Equal(1, await offline.GetSubRecordingCountAsync(heartRate));
            Assert.True(await offline.TryRemoveAsync(heartRate)); Assert.False(directories.Contains(root)); Assert.False(directories.Contains(day + "R/"));
            Assert.True(tree.ContainsKey(day + "SLEEP/SLEEPRES.BPB")); Assert.True(directories.Contains(day));
            Assert.Equal(0, await offline.GetSubRecordingCountAsync(heartRate));
            var stored = new PolarStoredDataService(files);
            await stored.DeleteStoredDataAsync(PolarStoredDataType.Sleep, new DateTime(2026, 10, 29));
            Assert.False(tree.ContainsKey(day + "SLEEP/SLEEPRES.BPB")); Assert.False(directories.Contains(day)); Assert.True(directories.Contains("/U/0/"));
            // Typed REST conversion and sleep batches share the raw event without losing unknown fields.
            using var rest = new PolarRestService(client, files); var states = new List<PolarSleepRecordingStatus>(); int received = 0;
            rest.SleepStatusChanged += (_, value) => states.AddRange(value.Statuses);
            using (rest.ObserveEvents(item => item.Json, _ => received++, error => throw error))
            {
                var notification = new PbPftpDHRestApiEvent { Uncompressed = true };
                notification.Event.Add(ByteString.CopyFromUtf8("{\"sleep_recording_state\":{\"enabled\":true}}")); notification.Event.Add(ByteString.CopyFromUtf8("{\"sleep_recording_state\":{}}"));
                connection.EmitD2h(PftpFraming.Notification(18, notification.ToByteArray()));
            }
            Assert.Equal(2, received); Assert.SequenceEqual(new[] { PolarSleepRecordingStatus.Enabled, PolarSleepRecordingStatus.Unknown }, states);
        }

        private sealed class FakeConnection : IBleConnection
        {
            public string DeviceId => "test";
            public bool IsConnected { get; private set; } = true;
            public int MaxWriteSize => 20;
            public event EventHandler<BleNotification>? NotificationReceived;
            public event EventHandler? Disconnected;
            public event EventHandler<BleTransportError>? ErrorOccurred { add { } remove { } }
            public List<Guid> Subscriptions { get; } = new List<Guid>();
            public List<byte[]> Messages { get; } = new List<byte[]>();
            public Func<byte[], byte[]>? Response;
            public ushort? StatusError;
            public int WritesWithResponse;
            public bool Aborted;
            private PftpAssembler? _request;
            public Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<BleGattService>>(Array.Empty<BleGattService>());
            public Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
            public Task SetNotificationsAsync(Guid serviceUuid, Guid characteristicUuid, bool enabled, CancellationToken cancellationToken = default)
            { if (enabled) Subscriptions.Add(characteristicUuid); return Task.CompletedTask; }
            public Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withResponse, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested(); if (withResponse) WritesWithResponse++;
                if (characteristicUuid != PftpClient.MtuUuid) return Task.CompletedTask;
                if (data.Length == 3 && data.All(x => x == 0)) { Aborted = true; return Task.CompletedTask; }
                _request ??= new PftpAssembler();
                if (_request.Add(data))
                {
                    var request = _request.GetResult(); _request.Dispose(); _request = null; Messages.Add(request);
                    if (StatusError.HasValue) EmitMtu(new byte[] { 0, (byte)StatusError.Value, (byte)(StatusError.Value >> 8) });
                    else if (Response != null)
                    {
                        var result = Response(request);
                        if (result.Length == 0) EmitMtu(new byte[] { 0, 0, 0 });
                        else foreach (var frame in PftpFraming.Fragment(result, MaxWriteSize)) EmitMtu(frame);
                    }
                }
                return Task.CompletedTask;
            }
            public void EmitMtu(byte[] packet) => NotificationReceived?.Invoke(this, new BleNotification(PftpClient.ServiceUuid, PftpClient.MtuUuid, packet));
            public void EmitD2h(byte[] message)
            { foreach (var packet in PftpFraming.Fragment(message, MaxWriteSize)) NotificationReceived?.Invoke(this, new BleNotification(PftpClient.ServiceUuid, PftpClient.D2hUuid, packet)); }
            public Task DisconnectAsync(CancellationToken cancellationToken = default) { IsConnected = false; Disconnected?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
            public void Dispose() { _request?.Dispose(); }
        }
    }
}
