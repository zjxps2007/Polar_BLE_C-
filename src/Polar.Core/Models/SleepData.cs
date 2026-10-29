using System;
using System.Collections.Generic;
using System.Linq;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Models
{
    public enum PolarSleepWakeState { Unknown = 0, Wake = -2, Rem = -3, NonRem12 = -5, NonRem3 = -6 }
    public sealed class PolarSleepPhase
    {
        public TimeSpan Offset { get; }
        public PolarSleepWakeState State { get; }
        internal PolarSleepPhase(PbSleepWakePhase source) { Offset = TimeSpan.FromSeconds(source.SecondsFromSleepStart); State = (PolarSleepWakeState)(int)source.SleepwakeState; }
    }
    public sealed class PolarSleepCycle
    {
        public TimeSpan Offset { get; }
        public float Depth { get; }
        internal PolarSleepCycle(PbSleepCycle source) { Offset = TimeSpan.FromSeconds(source.SecondsFromSleepStart); Depth = source.SleepDepthStart; }
    }
    public sealed class PolarSleepTemperature
    {
        public DateTime Date { get; }
        public float Celsius { get; }
        public float DeviationFromBaselineCelsius { get; }
        public bool BaselineAvailable => DeviationFromBaselineCelsius != -1000f;
        public PbSleepSkinTemperatureResult Source { get; }
        internal PolarSleepTemperature(PbSleepSkinTemperatureResult source)
        { Source = source; Date = PolarDataTime.Date(source.SleepDate); Celsius = source.SleepSkinTemperatureCelsius; DeviationFromBaselineCelsius = source.DeviationFromBaselineCelsius; }
    }
    public sealed class PolarSleepData
    {
        public DateTime Date { get; }
        public PolarLocalDateTime Start { get; }
        public PolarLocalDateTime End { get; }
        public DateTimeOffset LastModifiedUtc { get; }
        public DateTimeOffset? CreatedUtc { get; }
        public TimeSpan Goal { get; }
        public IReadOnlyList<PolarSleepPhase> Phases { get; }
        public IReadOnlyList<PolarSleepCycle> Cycles { get; }
        public IReadOnlyList<PolarLocalDateTime> Snoozes { get; }
        public PolarLocalDateTime? AlarmTime { get; }
        public int? StartOffsetSeconds { get; }
        public int? EndOffsetSeconds { get; }
        public PbSleepUserRating? UserRating { get; }
        public string? DeviceId { get; }
        public bool? BatteryRanOut { get; }
        public PolarLocalDateTime? OriginalStart { get; }
        public PolarLocalDateTime? OriginalEnd { get; }
        public PolarSleepTemperature? Temperature { get; }
        public PolarSleepResult Source { get; }
        public PolarSleepData(PolarSleepResult source)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source)); var data = source.Analysis;
            Date = data.SleepResultDate == null ? source.Date : PolarDataTime.Date(data.SleepResultDate);
            Start = PolarDataTime.Local(data.SleepStartTime); End = PolarDataTime.Local(data.SleepEndTime); LastModifiedUtc = PolarDataTime.Utc(data.LastModified);
            CreatedUtc = data.CreatedTimestamp == null ? (DateTimeOffset?)null : PolarDataTime.Utc(data.CreatedTimestamp); Goal = TimeSpan.FromMinutes(data.SleepGoalMinutes);
            Phases = data.SleepwakePhases.Select(x => new PolarSleepPhase(x)).ToArray(); Cycles = data.SleepCycles.Select(x => new PolarSleepCycle(x)).ToArray();
            Snoozes = data.SnoozeTime.Select(PolarDataTime.Local).ToArray(); AlarmTime = data.AlarmTime == null ? null : PolarDataTime.Local(data.AlarmTime);
            StartOffsetSeconds = data.HasSleepStartOffsetSeconds ? data.SleepStartOffsetSeconds : (int?)null; EndOffsetSeconds = data.HasSleepEndOffsetSeconds ? data.SleepEndOffsetSeconds : (int?)null;
            UserRating = data.HasUserSleepRating ? data.UserSleepRating : (PbSleepUserRating?)null; DeviceId = data.RecordingDevice?.DeviceId;
            BatteryRanOut = data.HasBatteryRanOut ? data.BatteryRanOut : (bool?)null;
            OriginalStart = data.OriginalSleepRange?.StartTime == null ? null : PolarDataTime.Local(data.OriginalSleepRange.StartTime);
            OriginalEnd = data.OriginalSleepRange?.EndTime == null ? null : PolarDataTime.Local(data.OriginalSleepRange.EndTime);
            Temperature = source.SkinTemperature == null ? null : new PolarSleepTemperature(source.SkinTemperature);
        }
    }
}
