using System;
using System.Collections.Generic;
using System.Linq;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Models
{
    /// <summary>Device wall-clock time. A missing offset remains unknown rather than using the computer's time zone.</summary>
    public sealed class PolarLocalDateTime
    {
        public DateTime LocalTime { get; }
        public int? UtcOffsetMinutes { get; }
        public DateTimeOffset? TimeWithOffset => UtcOffsetMinutes.HasValue ? new DateTimeOffset(LocalTime, TimeSpan.FromMinutes(UtcOffsetMinutes.Value)) : (DateTimeOffset?)null;
        internal PolarLocalDateTime(DateTime local, int? offset) { LocalTime = DateTime.SpecifyKind(local, DateTimeKind.Unspecified); UtcOffsetMinutes = offset; }
    }
    /// <summary>Conversions preserve absence and fail on invalid device dates instead of silently using the host clock.</summary>
    public static class PolarDataTime
    {
        public static DateTime Date(PbDate date)
        { if (date == null) throw new PolarProtocolException("Device date is missing."); return new DateTime(checked((int)date.Year), checked((int)date.Month), checked((int)date.Day)); }
        public static DateTime Date(PbDateProto3 date)
        { if (date == null) throw new PolarProtocolException("Device date is missing."); return new DateTime(checked((int)date.Year), checked((int)date.Month), checked((int)date.Day)); }
        public static TimeSpan Time(PbTime time)
        {
            if (time == null || time.Hour > 23 || time.Minute > 59 || time.Seconds > 59 || time.Millis > 999) throw new PolarProtocolException("Invalid device time.");
            return new TimeSpan(0, (int)time.Hour, (int)time.Minute, (int)time.Seconds, (int)time.Millis);
        }
        public static TimeSpan Duration(PbDuration duration)
        {
            if (duration == null) throw new PolarProtocolException("Device duration is missing.");
            return TimeSpan.FromTicks(checked(((long)duration.Hours * 3600000 + (long)duration.Minutes * 60000 + (long)duration.Seconds * 1000 + duration.Millis) * TimeSpan.TicksPerMillisecond));
        }
        public static PolarLocalDateTime Local(PbLocalDateTime time)
        {
            if (time == null) throw new PolarProtocolException("Local device date/time is missing.");
            if (time.HasTimeZoneOffset && (time.TimeZoneOffset < -720 || time.TimeZoneOffset > 840)) throw new PolarProtocolException("Invalid device UTC offset.");
            return new PolarLocalDateTime(Date(time.Date).Add(Time(time.Time)), time.HasTimeZoneOffset ? time.TimeZoneOffset : (int?)null);
        }
        public static DateTimeOffset Utc(PbSystemDateTime time)
        { if (time == null) throw new PolarProtocolException("System device date/time is missing."); return new DateTimeOffset(Date(time.Date).Add(Time(time.Time)), TimeSpan.Zero); }
    }
    public enum PolarActivityClass { Sleep = 1, Sedentary = 2, Light = 3, ContinuousModerate = 4, IntermittentModerate = 5, ContinuousVigorous = 6, IntermittentVigorous = 7, NonWear = 8 }
    public sealed class PolarActivityInfo
    {
        public PolarActivityClass? Class { get; }
        public PolarLocalDateTime? Timestamp { get; }
        public float? Factor { get; }
        public PbActivityInfo Source { get; }
        internal PolarActivityInfo(PbActivityInfo source)
        { Source = source; Class = source.HasValue ? (PolarActivityClass)(int)source.Value : (PolarActivityClass?)null; Timestamp = source.TimeStamp == null ? null : PolarDataTime.Local(source.TimeStamp); Factor = source.HasFactor ? source.Factor : (float?)null; }
    }
    public sealed class PolarActivitySamples
    {
        public string Path { get; }
        public PolarLocalDateTime StartTime { get; }
        public TimeSpan MetInterval { get; }
        public TimeSpan StepsInterval { get; }
        public IReadOnlyList<float> MetSamples { get; }
        public IReadOnlyList<uint> StepsSamples { get; }
        public IReadOnlyList<PolarActivityInfo> ActivityChanges { get; }
        public PbActivitySamples Source { get; }
        public PolarActivitySamples(string path, PbActivitySamples source)
        {
            Path = path; Source = source ?? throw new ArgumentNullException(nameof(source)); StartTime = PolarDataTime.Local(source.StartTime);
            MetInterval = PolarDataTime.Duration(source.MetRecordingInterval); StepsInterval = PolarDataTime.Duration(source.StepsRecordingInterval);
            MetSamples = source.MetSamples.ToArray(); StepsSamples = source.StepsSamples.ToArray(); ActivityChanges = source.ActivityInfo.Select(x => new PolarActivityInfo(x)).ToArray();
        }
    }
    public sealed class PolarActiveTime
    {
        public TimeSpan? NonWear { get; }
        public TimeSpan? Sleep { get; }
        public TimeSpan? Sedentary { get; }
        public TimeSpan? Light { get; }
        public TimeSpan? ContinuousModerate { get; }
        public TimeSpan? IntermittentModerate { get; }
        public TimeSpan? ContinuousVigorous { get; }
        public TimeSpan? IntermittentVigorous { get; }
        public PbActivityClassTimes Source { get; }
        public PolarActiveTime(PbActivityClassTimes source)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source)); NonWear = Read(source.TimeNonWear); Sleep = Read(source.TimeSleep); Sedentary = Read(source.TimeSedentary);
            Light = Read(source.TimeLightActivity); ContinuousModerate = Read(source.TimeContinuousModerate); IntermittentModerate = Read(source.TimeIntermittentModerate);
            ContinuousVigorous = Read(source.TimeContinuousVigorous); IntermittentVigorous = Read(source.TimeIntermittentVigorous);
        }
        internal static TimeSpan? Read(PbDuration? value) => value == null ? (TimeSpan?)null : PolarDataTime.Duration(value);
    }
    public sealed class PolarActivityGoal
    {
        public float Target { get; }
        public float Achieved { get; }
        public TimeSpan? TimeToGoUp { get; }
        public TimeSpan? TimeToGoWalk { get; }
        public TimeSpan? TimeToGoJog { get; }
        public PbActivityGoalSummary Source { get; }
        internal PolarActivityGoal(PbActivityGoalSummary source)
        { Source = source; Target = source.ActivityGoal; Achieved = source.AchievedActivity; TimeToGoUp = PolarActiveTime.Read(source.TimeToGoUp); TimeToGoWalk = PolarActiveTime.Read(source.TimeToGoWalk); TimeToGoJog = PolarActiveTime.Read(source.TimeToGoJog); }
    }
    public sealed class PolarDailySummary
    {
        public DateTime Date { get; }
        public uint? Steps { get; }
        public uint? ActivityCalories { get; }
        public uint? TrainingCalories { get; }
        public uint? BasalMetabolicCalories { get; }
        public float? DistanceMeters { get; }
        public PolarActivityGoal? ActivityGoal { get; }
        public PolarActiveTime? ActiveTime { get; }
        public PbDailyBalanceFeedback? DailyBalanceFeedback { get; }
        public PbReadinessForSpeedAndStrengthTraining? TrainingReadiness { get; }
        public PbDailySummary Source { get; }
        public PolarDailySummary(DateTime date, PbDailySummary source)
        {
            Date = date.Date; Source = source ?? throw new ArgumentNullException(nameof(source)); Steps = source.HasSteps ? source.Steps : (uint?)null;
            ActivityCalories = source.HasActivityCalories ? source.ActivityCalories : (uint?)null; TrainingCalories = source.HasTrainingCalories ? source.TrainingCalories : (uint?)null;
            BasalMetabolicCalories = source.HasBmrCalories ? source.BmrCalories : (uint?)null; DistanceMeters = source.HasActivityDistance ? source.ActivityDistance : (float?)null;
            ActivityGoal = source.ActivityGoalSummary == null ? null : new PolarActivityGoal(source.ActivityGoalSummary);
            ActiveTime = source.ActivityClassTimes == null ? null : new PolarActiveTime(source.ActivityClassTimes);
            DailyBalanceFeedback = source.HasDailyBalanceFeedback ? source.DailyBalanceFeedback : (PbDailyBalanceFeedback?)null;
            TrainingReadiness = source.HasReadinessForSpeedAndStrengthTraining ? source.ReadinessForSpeedAndStrengthTraining : (PbReadinessForSpeedAndStrengthTraining?)null;
        }
    }
    public enum PolarAutomaticSampleTrigger { HighActivity = 1, LowActivity = 2, Timed = 3, Manual = 4 }
    public sealed class PolarAutomaticHeartRateSession
    {
        public DateTime StartTime { get; }
        public PolarAutomaticSampleTrigger Trigger { get; }
        public IReadOnlyList<uint> HeartRates { get; }
        public PbAutomaticHeartRateSamples Source { get; }
        public PolarAutomaticHeartRateSession(DateTime date, PbAutomaticHeartRateSamples source)
        { Source = source ?? throw new ArgumentNullException(nameof(source)); StartTime = date.Date.Add(PolarDataTime.Time(source.Time)); Trigger = (PolarAutomaticSampleTrigger)(int)source.TriggerType; HeartRates = source.HeartRate.ToArray(); }
    }
}
