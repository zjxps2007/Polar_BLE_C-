using System;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Models
{
    public enum PolarGender { Male = 1, Female = 2 }
    public enum PolarTypicalDay { MostlySitting = 1, MostlyStanding = 2, MostlyMoving = 3 }
    public sealed class PolarPhysicalConfiguration
    {
        public PbUserPhysData Source { get; }
        public PolarGender? Gender => Source.Gender == null ? (PolarGender?)null : (PolarGender)(int)Source.Gender.Value;
        public DateTime? BirthDate => Source.Birthday?.Value == null ? (DateTime?)null : PolarDataTime.Date(Source.Birthday.Value);
        public float? HeightCentimeters => Source.Height?.Value;
        public float? WeightKilograms => Source.Weight?.Value;
        public uint? MaximumHeartRate => Source.MaximumHeartrate?.Value;
        public uint? RestingHeartRate => Source.RestingHeartrate?.Value;
        public uint? Vo2Max => Source.Vo2Max?.Value;
        public int? TrainingBackground => Source.TrainingBackground == null ? (int?)null : (int)Source.TrainingBackground.Value;
        public PolarTypicalDay? TypicalDay => Source.TypicalDay == null ? (PolarTypicalDay?)null : (PolarTypicalDay)(int)Source.TypicalDay.Value;
        public uint? SleepGoalMinutes => Source.SleepGoal?.HasSleepGoalMinutes == true ? Source.SleepGoal.SleepGoalMinutes : (uint?)null;
        public DateTimeOffset? LastModifiedUtc => Source.LastModified == null ? (DateTimeOffset?)null : PolarDataTime.Utc(Source.LastModified);
        public PolarPhysicalConfiguration(PbUserPhysData source) { Source = source ?? throw new ArgumentNullException(nameof(source)); }
    }
    public sealed class PolarFirstTimeUseConfiguration
    {
        public PolarGender Gender { get; }
        public DateTime BirthDate { get; }
        public float HeightCentimeters { get; }
        public float WeightKilograms { get; }
        public uint MaximumHeartRate { get; }
        public uint RestingHeartRate { get; }
        public uint Vo2Max { get; }
        public int TrainingBackground { get; }
        public PolarTypicalDay TypicalDay { get; }
        public uint SleepGoalMinutes { get; }
        public DateTimeOffset DeviceTime { get; }
        public PolarFirstTimeUseConfiguration(PolarGender gender, DateTime birthDate, float heightCentimeters, float weightKilograms,
            uint maximumHeartRate, uint restingHeartRate, uint vo2Max, int trainingBackground, PolarTypicalDay typicalDay, uint sleepGoalMinutes, DateTimeOffset deviceTime)
        {
            if (!Enum.IsDefined(typeof(PolarGender), gender)) throw new ArgumentOutOfRangeException(nameof(gender));
            if (!Enum.IsDefined(typeof(PolarTypicalDay), typicalDay)) throw new ArgumentOutOfRangeException(nameof(typicalDay));
            if (!(heightCentimeters >= 90 && heightCentimeters <= 240)) throw new ArgumentOutOfRangeException(nameof(heightCentimeters));
            if (!(weightKilograms >= 15 && weightKilograms <= 300)) throw new ArgumentOutOfRangeException(nameof(weightKilograms));
            if (maximumHeartRate < 100 || maximumHeartRate > 240) throw new ArgumentOutOfRangeException(nameof(maximumHeartRate));
            if (restingHeartRate < 20 || restingHeartRate > 120) throw new ArgumentOutOfRangeException(nameof(restingHeartRate));
            if (vo2Max < 10 || vo2Max > 95) throw new ArgumentOutOfRangeException(nameof(vo2Max));
            if (trainingBackground < 10 || trainingBackground > 60 || trainingBackground % 10 != 0) throw new ArgumentOutOfRangeException(nameof(trainingBackground));
            if (sleepGoalMinutes < 300 || sleepGoalMinutes > 660) throw new ArgumentOutOfRangeException(nameof(sleepGoalMinutes));
            Gender = gender; BirthDate = birthDate.Date; HeightCentimeters = heightCentimeters; WeightKilograms = weightKilograms;
            MaximumHeartRate = maximumHeartRate; RestingHeartRate = restingHeartRate; Vo2Max = vo2Max; TrainingBackground = trainingBackground;
            TypicalDay = typicalDay; SleepGoalMinutes = sleepGoalMinutes; DeviceTime = deviceTime;
        }
        public PbUserPhysData ToProto()
        {
            var stamp = Timestamp(DeviceTime);
            return new PbUserPhysData
            {
                Birthday = new PbUserBirthday { Value = new PbDate { Year = (uint)BirthDate.Year, Month = (uint)BirthDate.Month, Day = (uint)BirthDate.Day }, LastModified = stamp.Clone() },
                Gender = new PbUserGender { Value = (PbUserGender.Types.Gender)(int)Gender, LastModified = stamp.Clone() },
                Height = new PbUserHeight { Value = HeightCentimeters, LastModified = stamp.Clone() }, Weight = new PbUserWeight { Value = WeightKilograms, LastModified = stamp.Clone() },
                MaximumHeartrate = new PbUserHrAttribute { Value = MaximumHeartRate, LastModified = stamp.Clone() }, RestingHeartrate = new PbUserHrAttribute { Value = RestingHeartRate, LastModified = stamp.Clone() },
                Vo2Max = new PbUserVo2Max { Value = Vo2Max, LastModified = stamp.Clone() },
                TrainingBackground = new PbUserTrainingBackground { Value = (PbUserTrainingBackground.Types.TrainingBackground)TrainingBackground, LastModified = stamp.Clone() },
                TypicalDay = new PbUserTypicalDay { Value = (PbUserTypicalDay.Types.TypicalDay)(int)TypicalDay, LastModified = stamp.Clone() },
                SleepGoal = new PbSleepGoal { SleepGoalMinutes = SleepGoalMinutes, LastModified = stamp.Clone() }, LastModified = stamp
            };
        }
        internal static PbSystemDateTime Timestamp(DateTimeOffset value)
        {
            var utc = value.UtcDateTime;
            return new PbSystemDateTime { Date = new PbDate { Year = (uint)utc.Year, Month = (uint)utc.Month, Day = (uint)utc.Day },
                Time = new PbTime { Hour = (uint)utc.Hour, Minute = (uint)utc.Minute, Seconds = (uint)utc.Second, Millis = (uint)utc.Millisecond }, Trusted = true };
        }
    }
    /// <summary>Convenient optional settings backed by a cloned protobuf; unknown fields survive updates.</summary>
    public sealed class PolarUserDeviceConfiguration
    {
        private readonly PbUserDeviceSettings _source;
        public PolarUserDeviceConfiguration(PbUserDeviceSettings? source = null) { _source = source?.Clone() ?? new PbUserDeviceSettings(); }
        public PbUserDeviceSettings ToProto(DateTimeOffset? modified = null)
        { var result = _source.Clone(); result.LastModified = PolarFirstTimeUseConfiguration.Timestamp(modified ?? DateTimeOffset.UtcNow); return result; }
        public PbDeviceLocation? Location
        {
            get => _source.GeneralSettings?.HasDeviceLocation == true ? _source.GeneralSettings.DeviceLocation : (PbDeviceLocation?)null;
            set { _source.GeneralSettings ??= new PbUserDeviceGeneralSettings(); if (value.HasValue) _source.GeneralSettings.DeviceLocation = value.Value; else _source.GeneralSettings.ClearDeviceLocation(); }
        }
        public bool? UsbConnectionEnabled
        {
            get => _source.UsbConnectionSettings?.HasMode == true ? _source.UsbConnectionSettings.Mode == PbUsbConnectionSettings.Types.PbUsbConnectionMode.On : (bool?)null;
            set { _source.UsbConnectionSettings ??= new PbUsbConnectionSettings(); if (value.HasValue) _source.UsbConnectionSettings.Mode = value.Value ? PbUsbConnectionSettings.Types.PbUsbConnectionMode.On : PbUsbConnectionSettings.Types.PbUsbConnectionMode.Off; else _source.UsbConnectionSettings.ClearMode(); }
        }
        public bool? TelemetryEnabled
        {
            get => _source.TelemetrySettings?.HasTelemetryEnabled == true ? _source.TelemetrySettings.TelemetryEnabled : (bool?)null;
            set { _source.TelemetrySettings ??= new PbUserDeviceTelemetrySettings(); if (value.HasValue) _source.TelemetrySettings.TelemetryEnabled = value.Value; else _source.TelemetrySettings.ClearTelemetryEnabled(); }
        }
        private PbAutomaticTrainingDetectionSettings? Training => _source.AutomaticMeasurementSettings?.AutomaticTrainingDetectionSettings;
        private PbAutomaticTrainingDetectionSettings EnsureTraining()
        { _source.AutomaticMeasurementSettings ??= new PbUserAutomaticMeasurementSettings(); return _source.AutomaticMeasurementSettings.AutomaticTrainingDetectionSettings ??= new PbAutomaticTrainingDetectionSettings(); }
        public bool? AutomaticTrainingDetectionEnabled
        {
            get => Training?.HasState == true ? Training.State == PbAutomaticTrainingDetectionSettings.Types.PbAutomaticTrainingDetectionState.On : (bool?)null;
            set { if (value.HasValue) EnsureTraining().State = value.Value ? PbAutomaticTrainingDetectionSettings.Types.PbAutomaticTrainingDetectionState.On : PbAutomaticTrainingDetectionSettings.Types.PbAutomaticTrainingDetectionState.Off; else EnsureTraining().ClearState(); }
        }
        public uint? TrainingDetectionSensitivity
        {
            get => Training?.HasSensitivity == true ? Training.Sensitivity : (uint?)null;
            set { if (value > 100) throw new ArgumentOutOfRangeException(nameof(value)); if (value.HasValue) EnsureTraining().Sensitivity = value.Value; else EnsureTraining().ClearSensitivity(); }
        }
        public uint? MinimumTrainingDurationSeconds
        { get => Training?.HasMinimumTrainingDurationSeconds == true ? Training.MinimumTrainingDurationSeconds : (uint?)null; set { if (value.HasValue) EnsureTraining().MinimumTrainingDurationSeconds = value.Value; else EnsureTraining().ClearMinimumTrainingDurationSeconds(); } }
        public bool? AutomaticOhrEnabled
        {
            get => _source.AutomaticMeasurementSettings?.AutomaticOhrMeasurement?.HasState == true ? _source.AutomaticMeasurementSettings.AutomaticOhrMeasurement.State != PbAutomaticMeasurementSettings.Types.PbAutomaticMeasurementState.Off : (bool?)null;
            set
            {
                _source.AutomaticMeasurementSettings ??= new PbUserAutomaticMeasurementSettings(); _source.AutomaticMeasurementSettings.AutomaticOhrMeasurement ??= new PbAutomaticMeasurementSettings();
                if (value.HasValue) _source.AutomaticMeasurementSettings.AutomaticOhrMeasurement.State = value.Value ? PbAutomaticMeasurementSettings.Types.PbAutomaticMeasurementState.AlwaysOn : PbAutomaticMeasurementSettings.Types.PbAutomaticMeasurementState.Off;
                else _source.AutomaticMeasurementSettings.AutomaticOhrMeasurement.ClearState();
            }
        }
    }
}
