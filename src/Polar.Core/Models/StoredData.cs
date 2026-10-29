using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Google.Protobuf;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Models
{
    public sealed class DatedValue<T>
    {
        public DateTime Date { get; }
        public T Value { get; }
        public DatedValue(DateTime date, T value) { Date = date.Date; Value = value; }
    }
    public sealed class StoredMessage<T> where T : IMessage<T>
    {
        public string Path { get; }
        public T Data { get; }
        public StoredMessage(string path, T data) { Path = path; Data = data; }
    }
    public sealed class PolarSleepResult
    {
        public DateTime Date { get; }
        public PbSleepAnalysisResult Analysis { get; }
        public PbSleepSkinTemperatureResult? SkinTemperature { get; }
        public PolarSleepResult(DateTime date, PbSleepAnalysisResult analysis, PbSleepSkinTemperatureResult? skinTemperature)
        { Date = date.Date; Analysis = analysis; SkinTemperature = skinTemperature; }
    }
    public enum PolarCalorieType { Activity, Training, BasalMetabolicRate }
    public enum PolarStoredDataType { Activity, AutomaticSamples, DailySummary, NightlyRecovery, SensorLogs, Sleep, SleepScore, SkinContactChanges, SkinTemperature, Spo2Test }
    public sealed class PolarStoredPpiSample
    {
        public int IntervalMilliseconds { get; }
        public int ErrorEstimateMilliseconds { get; }
        public uint Status { get; }
        public bool SkinContact => (Status & 1) != 0;
        public bool MovementDetected => (Status & 2) != 0;
        public bool OfflinePeriod => (Status & 4) != 0;
        public PolarStoredPpiSample(int interval, int error, uint status) { IntervalMilliseconds = interval; ErrorEstimateMilliseconds = error; Status = status; }
    }
    public sealed class PolarStoredPpiSession
    {
        public DateTime StartTime { get; }
        public int TriggerType { get; }
        public IReadOnlyList<PolarStoredPpiSample> Samples { get; }
        public PolarStoredPpiSession(DateTime start, int trigger, IReadOnlyList<PolarStoredPpiSample> samples) { StartTime = start; TriggerType = trigger; Samples = samples; }
    }
    public sealed class PolarDeviceLog
    {
        public string Path { get; }
        public byte[] Data { get; }
        public PolarDeviceLog(string path, byte[] data) { Path = path; Data = data; }
    }
    public sealed class PolarTrainingSession
    {
        public string Directory { get; }
        public IReadOnlyDictionary<string, IMessage> DecodedFiles { get; }
        public IReadOnlyDictionary<string, byte[]> OtherFiles { get; }
        public PolarTrainingSessionReference? Reference { get; }
        public PbTrainingSession? Summary { get; }
        public IReadOnlyList<PolarTrainingExercise> Exercises { get; }
        public PolarLocalDateTime? Start => Summary?.Start == null ? null : PolarDataTime.Local(Summary.Start);
        public PolarLocalDateTime? End => Summary?.End == null ? null : PolarDataTime.Local(Summary.End);
        public TimeSpan? Duration => Summary?.Duration == null ? (TimeSpan?)null : PolarDataTime.Duration(Summary.Duration);
        public float? DistanceMeters => Summary?.HasDistance == true ? Summary.Distance : (float?)null;
        public uint? Calories => Summary?.HasCalories == true ? Summary.Calories : (uint?)null;
        public PolarTrainingSession(string directory, IReadOnlyDictionary<string, IMessage> decodedFiles, IReadOnlyDictionary<string, byte[]> otherFiles, PolarTrainingSessionReference? reference = null)
        {
            Directory = directory; DecodedFiles = decodedFiles; OtherFiles = otherFiles; Reference = reference;
            Summary = decodedFiles.Values.OfType<PbTrainingSession>().FirstOrDefault();
            var exercises = new List<PolarTrainingExercise>();
            foreach (var group in decodedFiles.Where(x => x.Key.StartsWith(directory, StringComparison.Ordinal)).GroupBy(x => x.Key.Substring(directory.Length).Split('/')[0]))
                if (int.TryParse(group.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    exercises.Add(new PolarTrainingExercise(index, directory + group.Key + "/", group.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)));
            Exercises = exercises.OrderBy(x => x.Index).ToArray();
        }
    }
}
