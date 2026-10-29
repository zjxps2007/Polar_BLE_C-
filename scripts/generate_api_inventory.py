#!/usr/bin/env python3
"""Rebuild the reviewed Android+iOS feature API mapping; no network/dependencies.

Scope: all callable declarations in the top-level sdk/api files (including
callbacks, factories, default overloads), PolarBleApi properties, and the iOS
REST protocol declared outside sdk/api. Model helpers are deliberately excluded.
Each source declaration is retained, so overloads and callback default bodies
are not silently collapsed. Any new unmapped method makes this script fail.
"""
from __future__ import annotations
import argparse
import csv
import io
import json
import re
import subprocess
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ANDROID = "sources/Android/android-communications/library/src/sdk/java/com/polar/sdk/api"
IOS = "sources/iOS/ios-communications/Sources/PolarBleSdk/sdk/api"
REST = "sources/iOS/ios-communications/Sources/PolarBleSdk/sdk/impl/PolarBleApiImpl+RestService.swift"
IMPLEMENTED = "implemented-hardware-unverified"
PARTIAL = "partial"
PLANNED = "planned"
PLATFORM = "platform-replacement"
RULES: dict[str, dict] = {}


def add(names: str, symbols: str = "", status: str = IMPLEMENTED, note: str = ""):
    for name in names.split():
        if name in RULES:
            raise ValueError("Duplicate mapping: " + name)
        RULES[name] = {"status": status, "symbols": symbols.split(), "notes": note}


# Symbols name the actual callable/property/event in this port, not a promised wrapper.
add("defaultImplementation polarImplementation", "PolarClient.PolarClient WindowsBleTransport.WindowsBleTransport", PLATFORM,
    "Construct one PolarClient with WindowsBleTransport; mobile context/queue and iOS state restoration do not apply.")
add("versionInfo", "PolarSdkVersion.Version PolarSdkVersion.UpstreamCommit PolarSdkVersion.UpstreamRelease", IMPLEMENTED, "Port version and upstream provenance are separate constants.")
add("shutDown", "PolarClient.Dispose", PLATFORM, "IDisposable releases sessions, watchers and the Windows transport.")
add("cleanup", "PolarClient.Dispose", PARTIAL, "Full disposal exists; upstream cleanup preserves the SDK while removing closed sessions.")
add("setMtu", "PolarDeviceSession.MaxWriteSize", PLATFORM, "Windows negotiates ATT MTU; MaxWriteSize exposes negotiated MTU minus 3. Arbitrary MTU requests are not available.")
add("foregroundEntered", "", PLATFORM, "Android foreground lifecycle hook has no Windows desktop equivalent; no operation is required.")
add("setPolarFilter polarFilter", "PolarClient.PolarOnly")
add("searchForDevice", "PolarClient.StartScanAsync PolarClient.DeviceDiscovered PolarClient.StopScanAsync", IMPLEMENTED,
    "Use DeviceDiscovered and filter BleDevice.Name for prefix/substring overloads; stop scan explicitly or via cancellation.")
add("autoConnectToDevice startAutoConnectToDevice", "PolarClient.AutoConnectAsync", IMPLEMENTED,
    "Predicate supplies RSSI/service/name criteria; timeout and cancellation are explicit.")
add("connectToDevice waitForConnection", "PolarClient.ConnectAsync", IMPLEMENTED, "Awaited connect also waits for requested service initialization.")
add("disconnectFromDevice", "PolarClient.DisconnectAsync")
add("getDeviceName", "BleDevice.Name", PARTIAL, "Name is supplied in discovery results; no connected-device name cache lookup helper.")
add("getRSSIValue", "BleDevice.Rssi", PARTIAL, "Advertisement RSSI is available during scanning; no query/cache keyed by a connected Polar identifier.")
add("setAutomaticReconnection automaticReconnection", "PolarClient.AutomaticReconnection PolarClient.SessionConnected", IMPLEMENTED, "Enable reconnect policy; SessionConnected supplies the replacement session, whose streams must be subscribed again.")
add("isFeatureReady bleSdkFeatureReady", "PolarDeviceSession.ReadyServices PolarDeviceSession.ServiceReady", PARTIAL,
    "GATT service readiness is exposed. Full upstream feature-by-device/firmware readiness mapping is not implemented.")
add("bleSdkFeaturesReadiness", "PolarDeviceSession.ReadyServices PolarDeviceSession.GattServices", PARTIAL,
    "Service availability can be inspected; no complete per-SDK-feature ready/unavailable callback.")
add("setApiCallback observer deviceInfoObserver deviceHrObserver deviceFeaturesObserver", "PolarClient.DeviceDiscovered PolarDeviceSession.ServiceReady PolarDeviceSession.HeartRateReceived PolarDeviceSession.Disconnected", PARTIAL,
    "C# events replace delegates, but some original callback payloads/events remain missing; see individual callback rows.")
add("deviceConnecting", "PolarClient.ConnectAsync", PARTIAL, "Starting the awaited ConnectAsync operation identifies connecting state; no dedicated connecting event.")
add("deviceConnected", "PolarClient.SessionConnected")
add("deviceDisconnected", "PolarDeviceSession.Disconnected PolarDeviceSession.ErrorOccurred", PARTIAL,
    "Disconnect/error events exist; upstream categorized reason, pairingError and info DTO are not fully mapped.")
add("blePowerStateChanged blePowerOn blePowerOff powerStateObserver isBlePowered", "PolarClient.BluetoothRadioStateChanged PolarClient.BluetoothRadioState PolarClient.IsBlePowered PolarClient.GetBluetoothRadioStateAsync", IMPLEMENTED,
    "Read-only Windows Bluetooth radio state observer/query, including Unknown/Disabled/Unavailable. Upstream blePowerOn/Off are callbacks, not commands that change OS radio power.")
add("setApiLogger logger message", "PolarClient.ErrorOccurred PolarDeviceSession.ErrorOccurred", PARTIAL,
    "Errors are observable; general SDK diagnostic message logging callback is not implemented.")
add("disInformationReceived disInformationReceivedWithKeysAsStrings", "PolarDeviceSession.DeviceInformation PolarDeviceSession.ServiceReady", IMPLEMENTED,
    "Read DeviceInformation after DeviceInformation readiness; dictionary Guid keys can be formatted as strings.")
add("getBatteryLevel batteryLevelReceived", "PolarDeviceSession.BatteryLevel PolarDeviceSession.BatteryLevelReceived")
add("getChargerState batteryChargingStatusReceived powerSourcesStateReceived batteryPowerSourcesStateReceived", "PolarDeviceSession.BatteryStatus PolarDeviceSession.BatteryStatusReceived", IMPLEMENTED,
    "BAS 0x2BED reports charge, presence and wired/wireless power states; optional uninterpreted bytes remain in RawData.")
add("checkIfDeviceDisconnectedDueRemovedPairing", "PolarDeviceSession.ErrorOccurred PolarDeviceSession.Disconnected", PLATFORM,
    "Windows GATT does not expose a definitive remote pairing-removal reason. Authentication errors/disconnects are exposed without claiming their cause; local pairing state cannot prove a remote deletion.")
add("hrNotificationReceived hrValueReceived", "PolarDeviceSession.HeartRateReceived")
add("htsNotificationReceived", "HealthThermometerClient.StartAsync HealthThermometerClient.MeasurementReceived")
add("startListenForPolarHrBroadcasts", "PolarClient.StartScanAsync PolarClient.HrBroadcastReceived", IMPLEMENTED,
    "Parses Polar 0x006B manufacturer HR frames, skips GPB envelope and suppresses duplicate frame counters. Filter Device identifiers in the event handler.")

add("setLocalTime setDaylightSavingTime", "PolarDeviceService.SetLocalTimeAsync", IMPLEMENTED,
    "Pass DateTimeOffset with the desired offset; DST convenience behavior is SetLocalTimeAsync(DateTimeOffset.Now).")
add("getLocalTime getLocalTimeWithZone", "PolarDeviceService.GetLocalTimeAsync", IMPLEMENTED, "Returns DateTimeOffset, preserving local time and UTC offset.")
add("getDiskSpace", "PolarDeviceService.GetDiskSpaceAsync")
add("setLedConfig", "PolarDeviceService.SetLedConfigurationAsync")
add("doFactoryReset", "PolarDeviceService.FactoryResetAsync PolarDeviceService.ResetAsync", IMPLEMENTED, "ResetAsync exposes preservePairing for overloads that request it.")
add("doRestart", "PolarDeviceService.RestartAsync PolarDeviceService.ResetAsync", IMPLEMENTED, "ResetAsync exposes preservePairing for the iOS overload.")
add("setWarehouseSleep setWareHouseSleep", "PolarDeviceService.WarehouseSleepAsync")
add("setHibernateMode", "PolarDeviceService.HibernateAsync")
add("turnDeviceOff", "PolarDeviceService.TurnOffAsync")
add("doFirstTimeUse", "PolarDeviceService.ConfigureFirstTimeUseAsync PolarFirstTimeUseConfiguration.ToProto", IMPLEMENTED,
    "Typed configuration validates SDK ranges, creates all timestamped physical fields, writes time/physical data/user ID in a sync session; default master identifier is UInt64.MaxValue.")
add("isFtuDone", "PolarDeviceService.IsFirstTimeUseCompleteAsync")
add("getUserPhysicalConfiguration", "PolarDeviceService.GetPhysicalDataAsync", IMPLEMENTED, "Typed nullable physical configuration with original protobuf; missing file returns null.")
add("getUserDeviceSettings getPolarUserDeviceSettings", "PolarDeviceService.GetUserConfigurationAsync", IMPLEMENTED, "Typed optional settings preserve unknown fields, absent values and explicit false/zero.")
add("setUserDeviceSettings setPolarUserDeviceSettings", "PolarDeviceService.SetUserConfigurationAsync PolarDeviceService.UpdateUserSettingsAsync", IMPLEMENTED,
    "Named settings convert to protobuf with last-modified timestamp; read/modify/write preserves unknown fields.")
add("setUserDeviceLocation", "PolarDeviceService.SetDeviceLocationAsync")
add("setUsbConnectionMode", "PolarDeviceService.SetUsbConnectionModeAsync")
add("setTelemetryEnabled", "PolarDeviceService.SetTelemetryEnabledAsync")
add("setAutomaticOHRMeasurementEnabled", "PolarDeviceService.SetAutomaticOhrMeasurementAsync")
add("setAutomaticTrainingDetectionSettings", "PolarDeviceService.SetAutomaticTrainingDetectionAsync")
add("sendInitializationAndStartSyncNotifications", "PolarDeviceService.StartSyncAsync")
add("sendTerminateAndStopSyncNotifications", "PolarDeviceService.StopSyncAsync")
add("setMultiBLEConnectionMode", "PolarFeatureClient.SetMultiConnectionAsync")
add("getMultiBLEConnectionMode", "PolarFeatureClient.GetMultiConnectionAsync")
add("setSensorInitiatedSecurityMode", "PolarFeatureClient.SetSensorInitiatedSecurityAsync")
add("getSensorInitiatedSecurityMode", "PolarFeatureClient.GetSensorInitiatedSecurityAsync")
add("deleteTelemetryData", "PolarLoggingService.DeleteTelemetryDataAsync")
add("deleteDeviceDateFolders", "PolarStoredDataService.DeleteDateFoldersAsync", IMPLEMENTED, "Explicit ordered dates are required; unlike mobile null/no-op behavior, invalid ranges throw.")
add("deleteStoredDeviceData", "PolarStoredDataService.DeleteStoredDataAsync", IMPLEMENTED,
    "Deletes selected files through explicit inclusive date and prunes empty ancestors below /U/0; HIST and unrelated data survive. Errors propagate rather than being silently logged.")

add("getAvailableOnlineStreamDataTypes", "PmdClient.ReadFeaturesAsync PolarDeviceSession.ReadyServices", PARTIAL,
    "PMD feature set and HR service availability are exposed separately; caller must merge/filter control-only PMD types.")
add("getAvailableHRServiceDataTypes", "PolarDeviceSession.ReadyServices", IMPLEMENTED, "HeartRate service readiness indicates HR availability.")
add("requestStreamSettings requestFullStreamSettings", "PmdClient.GetSettingsAsync", IMPLEMENTED, "offline=false; full=true requests full settings.")
add("startHrStreaming", "PolarDeviceSession.StartHeartRateAsync PolarDeviceSession.HeartRateReceived")
add("stopHrStreaming", "PolarDeviceSession.StopHeartRateAsync")
add("startEcgStreaming startAccStreaming startGyroStreaming startMagnetometerStreaming startPpgStreaming startPpiStreaming startTemperatureStreaming startPressureStreaming startSkinTemperatureStreaming startLocationStreaming",
    "PmdClient.StartAsync PmdClient.DataReceived PmdFrameParser.Parse", IMPLEMENTED,
    "Select PmdMeasurementType, offline=false; decoded PmdDataBatch uses named channels and metadata rather than per-sensor mobile DTO classes. PPI uses empty settings.")
add("stopStreaming", "PmdClient.StopAsync")
add("enableSDKMode disableSDKMode", "PmdClient.SetSdkModeAsync")
add("isSDKModeEnabled", "PmdClient.GetSdkModeAsync")
add("requestDerivedMeasurementGroupIds", "PmdClient.GetSettingsAsync", IMPLEMENTED,
    "GetSettingsAsync(DerivedMeasurement).Available[DerivedMeasurementSettingsGroupId] contains the supported IDs.")
add("requestDerivedMeasurementSettingsGroup", "PmdClient.GetDerivedSettingsGroupAsync")
add("startDerivedOfflineRecording", "PmdClient.StartAsync", IMPLEMENTED, "Use DerivedMeasurement with its selected settings and offline=true.")
add("stopDerivedOfflineRecording", "PmdClient.StopAsync", IMPLEMENTED, "Stop DerivedMeasurement type.")

add("getAvailableOfflineRecordingDataTypes", "PolarOfflineRecordingService.GetAvailableDataTypesAsync")
add("requestOfflineRecordingSettings requestFullOfflineRecordingSettings", "PolarOfflineRecordingService.GetSettingsAsync", IMPLEMENTED, "Set full=true for the full variant.")
add("getOfflineRecordingStatus", "PolarOfflineRecordingService.GetStatusAsync", IMPLEMENTED, "Returns active offline types; all other types are inactive.")
add("startOfflineRecording", "PolarOfflineRecordingService.StartAsync")
add("stopOfflineRecording", "PolarOfflineRecordingService.StopAsync")
add("listOfflineRecordings listSplitOfflineRecordings", "PolarOfflineRecordingService.ListAsync", IMPLEMENTED, "Set split=true for physical segments; otherwise contiguous numbered segments are grouped.")
add("getOfflineRecord getOfflineRecordWithProgress getSplitOfflineRecord", "PolarOfflineRecordingService.DownloadAsync OfflineRecordingParser.Parse", IMPLEMENTED,
    "Downloads selected logical/physical entry, propagates IProgress<long>, decrypts/parses parts with continuous timestamps; split entry remains separate.")
add("getSubRecordingCount", "PolarOfflineRecordingService.GetSubRecordingCountAsync")
add("removeOfflineRecord", "PolarOfflineRecordingService.RemoveAsync", IMPLEMENTED,
    "Re-lists all current segments of the same recording type, including for split entries, then removes empty ancestors; other types survive.")
add("removeOfflineRecords", "PolarOfflineRecordingService.TryRemoveAsync", IMPLEMENTED, "Boolean error result convenience; .NET cancellation still propagates.")
add("setOfflineRecordingTrigger", "PolarOfflineRecordingService.SetTriggerAsync")
add("getOfflineRecordingTriggerSetup", "PolarOfflineRecordingService.GetTriggerAsync")

add("startRecording", "PolarExerciseService.StartH10RecordingAsync")
add("stopRecording", "PolarExerciseService.StopH10RecordingAsync")
add("requestRecordingStatus", "PolarExerciseService.GetRecordingStatusAsync")
add("listExercises fetchStoredExerciseList listOfflineExercisesV2", "PolarExerciseService.ListExerciseEntriesAsync", IMPLEMENTED,
    "Returns path/identifier/size and V2 date. H10's absent recorded date remains null instead of inventing the host's current date. iOS fetchStoredExerciseList is a deprecated alias, not a cache API.")
add("fetchExercise fetchOfflineExerciseV2", "PolarExerciseService.FetchExerciseDataAsync", IMPLEMENTED, "Returns interval and HR/RR samples (RR takes precedence), retaining source protobuf.")
add("removeExercise removeOfflineExerciseV2", "PolarExerciseService.RemoveOfflineExerciseAsync", IMPLEMENTED,
    "Both upstream platforms delete exactly entry.path for H10 and V2; no parent-folder deletion is part of these APIs.")
add("startOfflineExerciseV2", "PolarExerciseService.StartOfflineExerciseAsync", IMPLEMENTED, "Typed result state and directory with original result protobuf; caller supplies sport ID.")
add("stopOfflineExerciseV2", "PolarExerciseService.StopOfflineExerciseV2Async")
add("getOfflineExerciseStatusV2", "PolarExerciseService.GetOfflineExerciseV2StatusAsync")
add("isOfflineExerciseV2Supported", "PolarExerciseService.IsOfflineExerciseV2SupportedAsync")
add("getTrainingSessionReferences", "PolarExerciseService.GetTrainingSessionReferencesAsync", IMPLEMENTED, "Optional date limits; returns session/exercise paths, date, file kinds and sizes.")
add("getTrainingSession getTrainingSessionWithProgress", "PolarExerciseService.FetchTrainingSessionAsync", IMPLEMENTED,
    "Typed session/reference/exercises with summary/samples/advanced samples/route fields, raw files, gzip decoding and incremental byte/percent progress. Upstream training DTOs themselves retain these protobuf payloads.")
add("deleteTrainingSession", "PolarExerciseService.DeleteTrainingSessionAsync")
add("startExercise", "PolarExerciseService.StartExerciseAsync", IMPLEMENTED, "Supply numeric sport identifier.")
add("stopExercise", "PolarExerciseService.StopExerciseAsync")
add("pauseExercise", "PolarExerciseService.PauseExerciseAsync")
add("resumeExercise", "PolarExerciseService.ResumeExerciseAsync")
add("getExerciseStatus", "PolarExerciseService.GetExerciseInformationAsync")
add("observeExerciseStatus", "PolarExerciseService.StatusChanged PolarExerciseStatusEvent.Information", IMPLEMENTED, "D2H 19 event exposes typed state/type/sport ID plus original protobuf.")

add("getSteps", "PolarStoredDataService.GetStepsAsync")
add("getDistance", "PolarStoredDataService.GetDistanceAsync")
add("getCalories", "PolarStoredDataService.GetCaloriesAsync")
add("getActiveTime", "PolarStoredDataService.GetActiveTimeDataAsync", IMPLEMENTED, "Typed per-class TimeSpan values preserve absent fields.")
add("getActivitySampleData", "PolarStoredDataService.GetActivityDataAsync", IMPLEMENTED, "Typed local timestamps, sample intervals, steps/MET values, activity transitions and raw protobuf.")
add("getDailySummaryData", "PolarStoredDataService.GetDailySummaryDataAsync", IMPLEMENTED, "Typed optional calories/steps/distance, goals, class durations and feedback; source protobuf remains available.")
add("get247HrSamples", "PolarStoredDataService.Get247HeartRateDataAsync", IMPLEMENTED, "Typed session start time/trigger/HR samples. Unknown trigger numbers are preserved rather than discarding samples.")
add("get247PPiSamples", "PolarStoredDataService.Get247PpiSamplesAsync PolarStoredDataService.DecodePpiSessions", IMPLEMENTED,
    "Delta-decodes PPI/error values and quality flags; date/time and trigger type are retained.")
add("getNightlyRecharge", "PolarStoredDataService.GetNightlyRechargeDataAsync", IMPLEMENTED, "Typed UTC timestamps and nullable recovery metrics/tips, retaining original protobuf.")
add("getSkinTemperature", "PolarStoredDataService.GetSkinTemperatureDataAsync", IMPLEMENTED, "Typed device/location/type and sample delta-milliseconds/Celsius, retaining original protobuf.")
add("getSpo2TestData getSpo2Test", "PolarStoredDataService.GetSpo2DataAsync", IMPLEMENTED, "Typed test time/offset/status and optional measurement/quality fields, retaining original protobuf.")
add("getSleep getSleepData", "PolarStoredDataService.GetSleepDataAsync", IMPLEMENTED, "Typed local+offset times, UTC modified/created times, phases, cycles, goal, trim offsets, snoozes and optional skin temperature; absent timezone is not guessed.")
add("getSleepRecordingStatus", "PolarRestService.GetSleepRecordingStatusAsync")
add("getSleepRecordingState", "PolarRestService.GetSleepRecordingStatusAsync", IMPLEMENTED, "Legacy Boolean behavior maps Enabled to true, Disabled/Unknown to false.")
add("observeSleepRecordingState observeSleepRecordingStatus", "PolarRestService.SubscribeSleepStatusAsync PolarRestService.SleepStatusChanged", IMPLEMENTED,
    "Subscribe after attaching SleepStatusChanged; a device notification produces its typed status array. Legacy Boolean maps Enabled to true.")
add("stopSleepRecording", "PolarRestService.StopSleepRecordingAsync")
add("listRestApiServices", "PolarRestService.GetServicesAsync")
add("getRestApiDescription", "PolarRestService.GetServiceDescriptionAsync", IMPLEMENTED, "Typed events/endpoints/actions/details/triggers with source JSON preserved.")
add("putNotification", "PolarRestService.PutNotificationAsync")
add("receiveRestApiEvents", "PolarRestService.ObserveEvents PolarRestService.DecodeEvents", IMPLEMENTED, "Gzip/UTF8/JSON decoded before an explicit conversion delegate produces T; disposable subscriptions avoid reflection for NativeAOT/IL2CPP.")
add("observeDeviceToHostNotifications", "PftpClient.NotificationReceived", IMPLEMENTED, "Event carries full numeric ID and parameter bytes, including unknown future IDs.")
add("readFile getFile", "PolarFileService.ReadAsync")
add("writeFile", "PolarFileService.WriteAsync")
add("deleteFileOrDirectory", "PolarFileService.DeleteAsync")
add("getFileList", "PolarFileService.ListAsync")
add("createFolder", "PolarFileService.CreateFolderAsync")
add("exportDeviceLogs", "PolarLoggingService.ExportAsync")
add("getLogConfig", "PolarLoggingService.GetLogConfigurationAsync", IMPLEMENTED, "Nullable named configuration preserves false vs absent and original unknown fields.")
add("setLogConfig", "PolarLoggingService.SetLogConfigurationAsync", IMPLEMENTED, "Includes all logging flags, trigger/frequency/retain settings and external HR sensor settings.")
add("getDeviceTelemetryConfiguration", "MemfaultClient.ReadConfigurationAsync")
add("startTelemetry", "MemfaultClient.StartAsync MemfaultClient.DataReceived", IMPLEMENTED, "Memfault BLE chunk export and configuration are exposed; no automatic external upload.")
add("stopTelemetry", "MemfaultClient.StopAsync")
add("getAvailableTelemetryTypes", "PolarDeviceSession.GattServices MemfaultClient.ServiceUuid", IMPLEMENTED, "Presence of Memfault service indicates the currently defined telemetry type.")
add("checkFirmwareUpdate", "FirmwareCatalog.CheckAsync")
add("updateFirmware", "FirmwareCatalog.DownloadAsync FirmwareUpdater.UpdateAsync", IMPLEMENTED,
    "Compose catalog check/download (or explicit URL) and updater with reconnect callback and progress. Firmware update has not been run on hardware.")
add("getWatchFaceConfig", "WatchFaceService.GetAsync")
add("setWatchFaceConfig", "WatchFaceService.SetAsync")

COMPANION = "onDeviceAppeared onDeviceDisappeared onAssociationSucceeded onError isCompanionDeviceManagerAvailable createAssociationRequest associate launchAssociation getAssociatedDeviceAddresses isAssociated removeAssociation startObservingDevicePresence stopObservingDevicePresence requestBackgroundExecutionPrivilege polarCompanionDeviceAppeared polarCompanionDeviceDisappeared"
add(COMPANION, "PolarClient.PairAsync PolarClient.StartScanAsync PolarClient.DeviceDiscovered", PLATFORM,
    "Android CompanionDeviceManager association/presence/background privileges do not exist on Windows. Windows pairing and scanning are available, but association listing/removal and companion disappearance/background privileges are not equivalent.")


def uncomment(text: str) -> str:
    # Preserve line numbers; API source has no comment-like literals relevant to declarations.
    text = re.sub(r"/\*.*?\*/", lambda m: "\n" * m[0].count("\n"), text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def declarations():
    excluded = {"equals", "hashCode", "fromValue", "getByString"}  # DTO/enum helpers, not feature APIs.
    for platform, folder, ext in [("Android", ANDROID, "kt"), ("iOS", IOS, "swift")]:
        files = sorted((ROOT / "upstream" / folder).glob("*." + ext))
        if platform == "iOS":
            files.append(ROOT / "upstream" / REST)
        for file in files:
            text = uncomment(file.read_text(encoding="utf-8-sig"))
            if file.name == "PolarBleApiImpl+RestService.swift":
                # Only the exported protocol, not private implementations or internal DTO helpers.
                start = text.index("public protocol PolarRestServiceApi")
                end = text.index("\n}", start)
                text = "\n" * text[:start].count("\n") + text[start:end]
            pattern = (r"(?m)^\s*(?:(?:abstract|override|suspend|public|open)\s+)*fun\s+(?:\w+\.)?(\w+)\s*(?:<[^\n]*?>)?\s*\(" if ext == "kt"
                       else r"(?m)^\s*(?:(?:public|open|static|class|@objc)\s+)*func\s+(\w+)\s*(?:<[^\n]*?>)?\s*\(")
            for match in re.finditer(pattern, text):
                name = match[1]
                if name in excluded:
                    continue
                # Start at the first non-whitespace char: regex \s can consume blank lines.
                start = match.start() + len(match[0]) - len(match[0].lstrip())
                pos = text.index("(", match.start())
                depth = 1
                end = pos + 1
                while depth and end < len(text):
                    depth += (text[end] == "(") - (text[end] == ")")
                    end += 1
                line_end = text.find("\n", end)
                tail = text[end:line_end if line_end >= 0 else len(text)]
                signature = re.sub(r"\s+", " ", text[start:end] + tail.split("{")[0]).strip()
                yield {"platform": platform, "api": "PolarRestServiceApi" if file.name.endswith("+RestService.swift") else file.stem,
                       "member": name, "kind": "method", "signature": signature,
                       "source": file.relative_to(ROOT / "upstream").as_posix(), "line": text[:start].count("\n") + 1}
            if file.name == "PolarBleApi.swift":
                for match in re.finditer(r"(?m)^\s*var\s+(\w+)\s*:[^\n]+\{\s*get", text):
                    start = match.start() + len(match[0]) - len(match[0].lstrip())
                    yield {"platform": platform, "api": file.stem, "member": match[1], "kind": "property",
                           "signature": text[start:text.find("\n", start)].strip(),
                           "source": file.relative_to(ROOT / "upstream").as_posix(), "line": text[:start].count("\n") + 1}


def locate(symbol: str) -> dict:
    owner, member = symbol.rsplit(".", 1)
    candidates = []
    for file in sorted((ROOT / "src").rglob("*.cs")):
        if "obj" in file.parts or "Verification" in file.parts:
            continue
        text = file.read_text(encoding="utf-8-sig")
        owner_match = re.search(r"\b(?:class|struct|interface)\s+" + re.escape(owner) + r"\b", text)
        if not owner_match:
            continue
        # Several source files contain sibling types with identically named methods
        # (e.g. HeartRateParser.Parse and PolarHeartRateAdvertisement.Parse).
        following_type = re.search(r"(?m)^\s*(?:public|internal|private)\s+(?:(?:sealed|static|partial|readonly|abstract)\s+)*(?:class|struct|interface)\s+", text[owner_match.end():])
        end = owner_match.end() + following_type.start() if following_type else len(text)
        segment = text[owner_match.start():end]
        for declaration in re.finditer(r"\bpublic\b[^\n]*\b" + re.escape(member) + r"[ \t]*(?:[({;=]|=>|<|(?=\r?\n[ \t]*\{))", segment):
            line = text[:owner_match.start() + declaration.start()].count("\n") + 1
            candidates.append({"symbol": symbol, "source": file.relative_to(ROOT).as_posix(), "line": line})
    if not candidates:
        raise ValueError("Mapped symbol not found: " + symbol)
    # Partial classes may have references in method bodies; declarations always precede their bodies.
    return candidates[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if generated files differ; do not write")
    args = parser.parse_args()
    commit = subprocess.check_output(["git", "-C", str(ROOT / "upstream"), "rev-parse", "HEAD"], text=True).strip()
    rows = sorted(declarations(), key=lambda x: (x["api"], x["member"], x["platform"], x["line"]))
    unknown = sorted({r["member"] for r in rows} - RULES.keys())
    if unknown:
        raise ValueError("Unmapped upstream members: " + ", ".join(unknown))
    resolved = {s: locate(s) for r in rows for s in RULES[r["member"]]["symbols"]}
    for row in rows:
        rule = RULES[row["member"]]
        row.update(status=rule["status"], implementation=[resolved[s] for s in rule["symbols"]], notes=rule["notes"])
        row["source_url"] = f"https://github.com/polarofficial/polar-ble-sdk/blob/{commit}/{row['source']}#L{row['line']}"
    document = {"schema_version": 1, "upstream_commit": commit,
                "scope": "Android+iOS feature API declarations, callback/factory methods, PolarBleApi properties, and external iOS REST protocol; overload/default declarations retained. DTO/enum utility methods and model constructors excluded.",
                "hardware_target": {"device": "Polar Verity Sense", "firmware": "3.0.16", "consumer": "Unity 6 Windows x64"},
                "status_meanings": {IMPLEMENTED: "Concrete protocol/function implementation exists; physical device feature behavior is not certified by this mapping.",
                                    PARTIAL: "Concrete subset exists, with a specified semantic or DTO/convenience gap.",
                                    PLANNED: "No implementation for this capability exists yet.",
                                    PLATFORM: "Mobile OS contract is replaced or inapplicable on Windows; not a claim of original behavior parity."},
                "declaration_counts": dict(sorted(Counter(r["status"] for r in rows).items())),
                "unique_member_count": len({r["member"] for r in rows}), "declaration_count": len(rows), "members": rows}
    outputs = {ROOT / "docs/api-mapping.json": json.dumps(document, ensure_ascii=False, indent=2) + "\n"}
    csv_output = io.StringIO(newline="")
    columns = ["platform", "api", "kind", "member", "signature", "source", "line", "source_url", "status", "implementation_symbols", "implementation_locations", "notes"]
    writer = csv.DictWriter(csv_output, fieldnames=columns, lineterminator="\n")
    writer.writeheader()
    for row in rows:
        simple = {k: row[k] for k in columns if k in row}
        simple["implementation_symbols"] = "; ".join(i["symbol"] for i in row["implementation"])
        simple["implementation_locations"] = "; ".join(f"{i['source']}:{i['line']}" for i in row["implementation"])
        writer.writerow(simple)
    outputs[ROOT / "docs/api-inventory.csv"] = csv_output.getvalue()
    stale = []
    for file, content in outputs.items():
        if args.check:
            if not file.exists() or file.read_text(encoding="utf-8") != content:
                stale.append(file.relative_to(ROOT).as_posix())
        else:
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text(content, encoding="utf-8", newline="\n")
    if stale:
        raise SystemExit("Stale inventory: " + ", ".join(stale))
    print(json.dumps({k: document[k] for k in ("upstream_commit", "declaration_count", "unique_member_count", "declaration_counts")}, indent=2))


if __name__ == "__main__":
    main()
