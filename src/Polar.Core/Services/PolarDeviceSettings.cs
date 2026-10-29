using System;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Models;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Services
{
    public sealed partial class PolarDeviceService
    {
        public Task SetLedConfigurationAsync(bool sdkModeLedEnabled, bool ppiModeLedEnabled, CancellationToken cancellationToken = default)
            => _files.WriteAsync("/LEDCFG.BIN", new byte[] { sdkModeLedEnabled ? (byte)1 : (byte)0, ppiModeLedEnabled ? (byte)1 : (byte)0 }, cancellationToken);
        public Task<PbUserDeviceSettings> GetUserSettingsAsync(CancellationToken cancellationToken = default)
            => _files.ReadMessageAsync(_settingsPath, PbUserDeviceSettings.Parser, cancellationToken);
        public async Task<PolarUserDeviceConfiguration> GetUserConfigurationAsync(CancellationToken cancellationToken = default)
            => new PolarUserDeviceConfiguration(await GetUserSettingsAsync(cancellationToken).ConfigureAwait(false));
        public Task SetUserConfigurationAsync(PolarUserDeviceConfiguration settings, CancellationToken cancellationToken = default)
            => SetUserSettingsAsync((settings ?? throw new ArgumentNullException(nameof(settings))).ToProto(), cancellationToken);
        public async Task SetUserSettingsAsync(PbUserDeviceSettings settings, CancellationToken cancellationToken = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await _files.WriteMessageAsync(_settingsPath, settings, cancellationToken).ConfigureAwait(false); }
            finally { _settingsLock.Release(); }
        }
        /// <summary>Reads, changes and writes the complete protobuf, preserving unknown fields.</summary>
        public async Task UpdateUserSettingsAsync(Action<PbUserDeviceSettings> change, CancellationToken cancellationToken = default)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var settings = await GetUserSettingsAsync(cancellationToken).ConfigureAwait(false);
                change(settings);
                await _files.WriteMessageAsync(_settingsPath, settings, cancellationToken).ConfigureAwait(false);
            }
            finally { _settingsLock.Release(); }
        }
        public Task SetDeviceLocationAsync(PbDeviceLocation location, CancellationToken cancellationToken = default)
            => UpdateUserSettingsAsync(s => { s.GeneralSettings ??= new PbUserDeviceGeneralSettings(); s.GeneralSettings.DeviceLocation = location; }, cancellationToken);
        public Task SetUsbConnectionModeAsync(bool enabled, CancellationToken cancellationToken = default)
            => UpdateUserSettingsAsync(s => s.UsbConnectionSettings = new PbUsbConnectionSettings
            { Mode = enabled ? PbUsbConnectionSettings.Types.PbUsbConnectionMode.On : PbUsbConnectionSettings.Types.PbUsbConnectionMode.Off }, cancellationToken);
        public Task SetTelemetryEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
            => UpdateUserSettingsAsync(s => s.TelemetrySettings = new PbUserDeviceTelemetrySettings { TelemetryEnabled = enabled }, cancellationToken);
        public Task SetAutomaticOhrMeasurementAsync(bool enabled, CancellationToken cancellationToken = default)
            => UpdateUserSettingsAsync(s =>
            {
                s.AutomaticMeasurementSettings ??= new PbUserAutomaticMeasurementSettings();
                s.AutomaticMeasurementSettings.AutomaticOhrMeasurement ??= new PbAutomaticMeasurementSettings();
                s.AutomaticMeasurementSettings.AutomaticOhrMeasurement.State = enabled
                    ? PbAutomaticMeasurementSettings.Types.PbAutomaticMeasurementState.AlwaysOn
                    : PbAutomaticMeasurementSettings.Types.PbAutomaticMeasurementState.Off;
            }, cancellationToken);
        public Task SetAutomaticTrainingDetectionAsync(bool enabled, uint sensitivity, uint minimumDurationSeconds, CancellationToken cancellationToken = default)
        {
            if (sensitivity > 100) throw new ArgumentOutOfRangeException(nameof(sensitivity));
            return UpdateUserSettingsAsync(s =>
            {
                s.AutomaticMeasurementSettings ??= new PbUserAutomaticMeasurementSettings();
                s.AutomaticMeasurementSettings.AutomaticTrainingDetectionSettings = new PbAutomaticTrainingDetectionSettings
                {
                    State = enabled ? PbAutomaticTrainingDetectionSettings.Types.PbAutomaticTrainingDetectionState.On : PbAutomaticTrainingDetectionSettings.Types.PbAutomaticTrainingDetectionState.Off,
                    Sensitivity = sensitivity, MinimumTrainingDurationSeconds = minimumDurationSeconds
                };
            }, cancellationToken);
        }
        public Task<PbUserPhysData> GetPhysicalConfigurationAsync(CancellationToken cancellationToken = default)
            => _files.ReadMessageAsync("/U/0/S/PHYSDATA.BPB", PbUserPhysData.Parser, cancellationToken);
        public async Task<PolarPhysicalConfiguration?> GetPhysicalDataAsync(CancellationToken cancellationToken = default)
        {
            try { return new PolarPhysicalConfiguration(await GetPhysicalConfigurationAsync(cancellationToken).ConfigureAwait(false)); }
            catch (PftpResponseException error) when (error.ErrorCode == 103) { return null; }
        }
        public async Task<bool> IsFirstTimeUseCompleteAsync(CancellationToken cancellationToken = default)
        {
            try { return (await _files.ReadMessageAsync("/U/0/USERID.BPB", PbUserIdentifier.Parser, cancellationToken).ConfigureAwait(false)).HasMasterIdentifier; }
            catch (PftpResponseException error) when (error.ErrorCode == 103) { return false; }
        }
        public Task FirstTimeUseAsync(PbUserPhysData physicalData, ulong userIdentifier, DateTimeOffset deviceTime, CancellationToken cancellationToken = default)
        {
            if (physicalData == null) throw new ArgumentNullException(nameof(physicalData));
            return RunSyncAsync(async token =>
            {
                await SetLocalTimeAsync(deviceTime, token).ConfigureAwait(false);
                await _files.WriteMessageAsync("/U/0/S/PHYSDATA.BPB", physicalData, token).ConfigureAwait(false);
                await _files.WriteMessageAsync("/U/0/USERID.BPB", new PbUserIdentifier { MasterIdentifier = userIdentifier, UserIdLastModified = PolarFirstTimeUseConfiguration.Timestamp(deviceTime) }, token).ConfigureAwait(false);
            }, cancellationToken);
        }
        public Task ConfigureFirstTimeUseAsync(PolarFirstTimeUseConfiguration configuration, ulong userIdentifier = ulong.MaxValue, CancellationToken cancellationToken = default)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return FirstTimeUseAsync(configuration.ToProto(), userIdentifier, configuration.DeviceTime, cancellationToken);
        }
    }
}
