using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Models
{
    public sealed class PolarNightlyRechargeData
    {
        public DateTime Date { get; }
        public PbNightlyRecoveryStatus Source { get; }
        public DateTimeOffset CreatedUtc => PolarDataTime.Utc(Source.CreatedTimestamp);
        public DateTimeOffset? ModifiedUtc => Source.ModifiedTimestamp == null ? (DateTimeOffset?)null : PolarDataTime.Utc(Source.ModifiedTimestamp);
        public DateTime? SleepResultDate => Source.SleepResultDate == null ? (DateTime?)null : PolarDataTime.Date(Source.SleepResultDate);
        public float? AnsStatus => Source.HasAnsStatus ? Source.AnsStatus : (float?)null;
        public int? RecoveryIndicator => Source.HasRecoveryIndicator ? Source.RecoveryIndicator : (int?)null;
        public int? RecoveryIndicatorSubLevel => Source.HasRecoveryIndicatorSubLevel ? Source.RecoveryIndicatorSubLevel : (int?)null;
        public int? AnsRate => Source.HasAnsRate ? Source.AnsRate : (int?)null;
        public int? ScoreRateOBSOLETE => Source.HasScoreRateOBSOLETE ? Source.ScoreRateOBSOLETE : (int?)null;
        public int? MeanNightlyRecoveryRRI => Source.HasMeanNightlyRecoveryRRI ? Source.MeanNightlyRecoveryRRI : (int?)null;
        public int? MeanNightlyRecoveryRMSSD => Source.HasMeanNightlyRecoveryRMSSD ? Source.MeanNightlyRecoveryRMSSD : (int?)null;
        public int? MeanNightlyRecoveryRespirationInterval => Source.HasMeanNightlyRecoveryRespirationInterval ? Source.MeanNightlyRecoveryRespirationInterval : (int?)null;
        public int? MeanBaselineRRI => Source.HasMeanBaselineRRI ? Source.MeanBaselineRRI : (int?)null;
        public int? SdBaselineRRI => Source.HasSdBaselineRRI ? Source.SdBaselineRRI : (int?)null;
        public int? MeanBaselineRMSSD => Source.HasMeanBaselineRMSSD ? Source.MeanBaselineRMSSD : (int?)null;
        public int? SdBaselineRMSSD => Source.HasSdBaselineRMSSD ? Source.SdBaselineRMSSD : (int?)null;
        public int? MeanBaselineRespirationInterval => Source.HasMeanBaselineRespirationInterval ? Source.MeanBaselineRespirationInterval : (int?)null;
        public int? SdBaselineRespirationInterval => Source.HasSdBaselineRespirationInterval ? Source.SdBaselineRespirationInterval : (int?)null;
        public string? SleepTip => Source.HasSleepTip ? Source.SleepTip : (string?)null;
        public string? VitalityTip => Source.HasVitalityTip ? Source.VitalityTip : (string?)null;
        public string? ExerciseTip => Source.HasExerciseTip ? Source.ExerciseTip : (string?)null;
        public PolarNightlyRechargeData(DateTime date, PbNightlyRecoveryStatus source) { Date = date.Date; Source = source ?? throw new ArgumentNullException(nameof(source)); }
    }
    public sealed class PolarTemperatureSample
    {
        public ulong OffsetMilliseconds { get; }
        public float Celsius { get; }
        internal PolarTemperatureSample(TemperatureMeasurementSample source) { OffsetMilliseconds = source.RecordingTimeDeltaMilliseconds; Celsius = source.TemperatureCelsius; }
    }
    public sealed class PolarSkinTemperatureData
    {
        public DateTime Date { get; }
        public string DeviceId => Source.SourceDeviceId;
        public SensorLocation SensorLocation => Source.SensorLocation;
        public TemperatureMeasurementType MeasurementType => Source.MeasurementType;
        public IReadOnlyList<PolarTemperatureSample> Samples { get; }
        public TemperatureMeasurementPeriod Source { get; }
        public PolarSkinTemperatureData(DateTime date, TemperatureMeasurementPeriod source)
        { Date = date.Date; Source = source ?? throw new ArgumentNullException(nameof(source)); Samples = source.TemperatureMeasurementSamples.Select(x => new PolarTemperatureSample(x)).ToArray(); }
    }
    public sealed class PolarSpo2TestData
    {
        public string Path { get; }
        public PbSpo2TestResult Source { get; }
        public string DeviceId => Source.RecordingDevice;
        public DateTimeOffset? TestTime { get; }
        public PbSpo2TestStatus Status => Source.TestStatus;
        public int? BloodOxygenPercent => Source.HasBloodOxygenPercent ? Source.BloodOxygenPercent : (int?)null;
        public PbSpo2Class? Spo2Class => Source.HasSpo2Class ? Source.Spo2Class : (PbSpo2Class?)null;
        public PbDeviationFromBaseline? Spo2ValueDeviationFromBaseline => Source.HasSpo2ValueDeviationFromBaseline ? Source.Spo2ValueDeviationFromBaseline : (PbDeviationFromBaseline?)null;
        public float? Spo2QualityAveragePercent => Source.HasSpo2QualityAveragePercent ? Source.Spo2QualityAveragePercent : (float?)null;
        public uint? AverageHeartRateBpm => Source.HasAverageHeartRateBpm ? Source.AverageHeartRateBpm : (uint?)null;
        public float? HeartRateVariabilityMs => Source.HasHeartRateVariabilityMs ? Source.HeartRateVariabilityMs : (float?)null;
        public PbDeviationFromBaseline? Spo2HrvDeviationFromBaseline => Source.HasSpo2HrvDeviationFromBaseline ? Source.Spo2HrvDeviationFromBaseline : (PbDeviationFromBaseline?)null;
        public float? AltitudeMeters => Source.HasAltitudeMeters ? Source.AltitudeMeters : (float?)null;
        public PolarSpo2TestData(string path, PbSpo2TestResult source)
        {
            Path = path; Source = source ?? throw new ArgumentNullException(nameof(source));
            if (source.TimeZoneOffset < -720 || source.TimeZoneOffset > 840) throw new PolarProtocolException("Invalid SpO2 UTC offset.");
            var offset = TimeSpan.FromMinutes(source.TimeZoneOffset);
            var match = Regex.Match(path, @"/(\d{8})/SPO2TEST/(\d{6})/", RegexOptions.CultureInvariant);
            if (match.Success && DateTime.TryParseExact(match.Groups[1].Value + match.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                TestTime = new DateTimeOffset(local, offset);
            else if (source.TestTime != 0) TestTime = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)source.TestTime)).ToOffset(offset);
        }
    }
}
