using System;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Models
{
    /// <summary>Nullable settings distinguish omitted fields from false. Unknown protobuf fields survive read/modify/write.</summary>
    public sealed class PolarLogConfiguration
    {
        private readonly PbSensorDataLog _source;
        public PolarLogConfiguration(PbSensorDataLog? source = null) { _source = source?.Clone() ?? new PbSensorDataLog(); }
        public PbSensorDataLog ToProto() => _source.Clone();
        public bool? AccelerationLogEnabled
        { get => _source.HasAccelerationLogEnabled ? _source.AccelerationLogEnabled : (bool?)null; set { if (value.HasValue) _source.AccelerationLogEnabled = value.Value; else _source.ClearAccelerationLogEnabled(); } }
        public bool? CaloriesLogEnabled
        { get => _source.HasCaloriesLogEnabled ? _source.CaloriesLogEnabled : (bool?)null; set { if (value.HasValue) _source.CaloriesLogEnabled = value.Value; else _source.ClearCaloriesLogEnabled(); } }
        public bool? GpsLogEnabled
        { get => _source.HasGpsLogEnabled ? _source.GpsLogEnabled : (bool?)null; set { if (value.HasValue) _source.GpsLogEnabled = value.Value; else _source.ClearGpsLogEnabled(); } }
        public bool? GpsNmeaLogEnabled
        { get => _source.HasGpsNmeaLogEnabled ? _source.GpsNmeaLogEnabled : (bool?)null; set { if (value.HasValue) _source.GpsNmeaLogEnabled = value.Value; else _source.ClearGpsNmeaLogEnabled(); } }
        public bool? OhrLogEnabled
        { get => _source.HasOhrLogEnabled ? _source.OhrLogEnabled : (bool?)null; set { if (value.HasValue) _source.OhrLogEnabled = value.Value; else _source.ClearOhrLogEnabled(); } }
        public bool? MagnetometerLogEnabled
        { get => _source.HasMagnetometerLogEnabled ? _source.MagnetometerLogEnabled : (bool?)null; set { if (value.HasValue) _source.MagnetometerLogEnabled = value.Value; else _source.ClearMagnetometerLogEnabled(); } }
        public bool? TapLogEnabled
        { get => _source.HasTapLogEnabled ? _source.TapLogEnabled : (bool?)null; set { if (value.HasValue) _source.TapLogEnabled = value.Value; else _source.ClearTapLogEnabled(); } }
        public bool? BarometerLogEnabled
        { get => _source.HasBarometerLogEnabled ? _source.BarometerLogEnabled : (bool?)null; set { if (value.HasValue) _source.BarometerLogEnabled = value.Value; else _source.ClearBarometerLogEnabled(); } }
        public bool? GyroscopeLogEnabled
        { get => _source.HasGyroscopeLogEnabled ? _source.GyroscopeLogEnabled : (bool?)null; set { if (value.HasValue) _source.GyroscopeLogEnabled = value.Value; else _source.ClearGyroscopeLogEnabled(); } }
        public bool? SleepLogEnabled
        { get => _source.HasSleepLogEnabled ? _source.SleepLogEnabled : (bool?)null; set { if (value.HasValue) _source.SleepLogEnabled = value.Value; else _source.ClearSleepLogEnabled(); } }
        public bool? SlopeLogEnabled
        { get => _source.HasSlopeLogEnabled ? _source.SlopeLogEnabled : (bool?)null; set { if (value.HasValue) _source.SlopeLogEnabled = value.Value; else _source.ClearSlopeLogEnabled(); } }
        public bool? AmbientLightLogEnabled
        { get => _source.HasAmbientLightLogEnabled ? _source.AmbientLightLogEnabled : (bool?)null; set { if (value.HasValue) _source.AmbientLightLogEnabled = value.Value; else _source.ClearAmbientLightLogEnabled(); } }
        public bool? TlrLogEnabled
        { get => _source.HasTlrLogEnabled ? _source.TlrLogEnabled : (bool?)null; set { if (value.HasValue) _source.TlrLogEnabled = value.Value; else _source.ClearTlrLogEnabled(); } }
        public bool? PpiLogEnabled
        { get => _source.HasPpiLogEnabled ? _source.PpiLogEnabled : (bool?)null; set { if (value.HasValue) _source.PpiLogEnabled = value.Value; else _source.ClearPpiLogEnabled(); } }
        public bool? OndemandLogEnabled
        { get => _source.HasOndemandLogEnabled ? _source.OndemandLogEnabled : (bool?)null; set { if (value.HasValue) _source.OndemandLogEnabled = value.Value; else _source.ClearOndemandLogEnabled(); } }
        public bool? CapsenseLogEnabled
        { get => _source.HasCapsenseLogEnabled ? _source.CapsenseLogEnabled : (bool?)null; set { if (value.HasValue) _source.CapsenseLogEnabled = value.Value; else _source.ClearCapsenseLogEnabled(); } }
        public bool? FusionLogEnabled
        { get => _source.HasFusionLogEnabled ? _source.FusionLogEnabled : (bool?)null; set { if (value.HasValue) _source.FusionLogEnabled = value.Value; else _source.ClearFusionLogEnabled(); } }
        public bool? MetLogEnabled
        { get => _source.HasMetLogEnabled ? _source.MetLogEnabled : (bool?)null; set { if (value.HasValue) _source.MetLogEnabled = value.Value; else _source.ClearMetLogEnabled(); } }
        public bool? VerticalAccLogEnabled
        { get => _source.HasVerticalAccLogEnabled ? _source.VerticalAccLogEnabled : (bool?)null; set { if (value.HasValue) _source.VerticalAccLogEnabled = value.Value; else _source.ClearVerticalAccLogEnabled(); } }
        public bool? AmdLogEnabled
        { get => _source.HasAmdLogEnabled ? _source.AmdLogEnabled : (bool?)null; set { if (value.HasValue) _source.AmdLogEnabled = value.Value; else _source.ClearAmdLogEnabled(); } }
        public bool? SkinTemperatureLogEnabled
        { get => _source.HasSkinTemperatureLogEnabled ? _source.SkinTemperatureLogEnabled : (bool?)null; set { if (value.HasValue) _source.SkinTemperatureLogEnabled = value.Value; else _source.ClearSkinTemperatureLogEnabled(); } }
        public bool? CompassLogEnabled
        { get => _source.HasCompassLogEnabled ? _source.CompassLogEnabled : (bool?)null; set { if (value.HasValue) _source.CompassLogEnabled = value.Value; else _source.ClearCompassLogEnabled(); } }
        public bool? Speed3DLogEnabled
        { get => _source.HasSpeed3DLogEnabled ? _source.Speed3DLogEnabled : (bool?)null; set { if (value.HasValue) _source.Speed3DLogEnabled = value.Value; else _source.ClearSpeed3DLogEnabled(); } }
        public bool? RetainSettingsOverBoot
        { get => _source.HasRetainSettingsOverBoot ? _source.RetainSettingsOverBoot : (bool?)null; set { if (value.HasValue) _source.RetainSettingsOverBoot = value.Value; else _source.ClearRetainSettingsOverBoot(); } }
        public PbSensorDataLog.Types.PbLogTrigger? LogTrigger
        { get => _source.HasLogTrigger ? _source.LogTrigger : (PbSensorDataLog.Types.PbLogTrigger?)null; set { if (value.HasValue) _source.LogTrigger = value.Value; else _source.ClearLogTrigger(); } }
        public PbSensorDataLog.Types.PbMagnetometerLogFrequency? MagnetometerLogFrequency
        { get => _source.HasMagnetometerLogFrequency ? _source.MagnetometerLogFrequency : (PbSensorDataLog.Types.PbMagnetometerLogFrequency?)null; set { if (value.HasValue) _source.MagnetometerLogFrequency = value.Value; else _source.ClearMagnetometerLogFrequency(); } }
        public string? ExternalHeartRateSensorName
        {
            get => _source.BleHrSensorConfig?.DeviceName?.Name;
            set { _source.BleHrSensorConfig ??= new PbBleHrSensorConfig(); _source.BleHrSensorConfig.DeviceName = value == null ? null : new PbBleDeviceName { Name = value }; }
        }
        public bool? WaitForExternalHeartRateSensor
        {
            get => _source.BleHrSensorConfig?.HasWaitForConnect == true ? _source.BleHrSensorConfig.WaitForConnect : (bool?)null;
            set { _source.BleHrSensorConfig ??= new PbBleHrSensorConfig(); if (value.HasValue) _source.BleHrSensorConfig.WaitForConnect = value.Value; else _source.BleHrSensorConfig.ClearWaitForConnect(); }
        }
    }
}
