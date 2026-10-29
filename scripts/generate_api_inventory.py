"""Map the immutable 394-declaration source snapshot onto native ABI v2.
No .NET, upstream checkout, network, or third-party Python package is required.
Statuses describe implementations, never hardware certification.
"""
import csv, json, re
from collections import Counter
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
rows=list(csv.DictReader((ROOT/'docs/upstream-api.csv').open(encoding='utf-8-sig')))
assert len(rows)==394
rules={}
def add(names,symbol,status='native',note='',location='include/polar/polar_sdk.h'):
    for name in names.split(): rules[name]=(symbol,status,note,location)
def service(names,symbol,note='Native service result retains raw typed protobuf, optional and unknown fields.'):
    add(names,symbol,'native',note,'include/polar/polar_services.h')
add('cleanup shutDown','polar_sdk_destroy')
add('defaultImplementation polarImplementation','polar_sdk_create')
add('versionInfo','POLAR_VERSION POLAR_ABI_VERSION')
add('searchForDevice','POLAR_OP_SCAN_START POLAR_OP_SCAN_STOP')
add('connectToDevice waitForConnection','POLAR_OP_CONNECT','native','Async completion contains new device handle, negotiated MTU and services.')
add('disconnectFromDevice','POLAR_OP_DISCONNECT')
add('getDeviceName getRSSIValue polarFilter setPolarFilter','POLAR_OP_SCAN_START polar_advertisement','native','Name/RSSI come from scan events. Scan flags bit0 disables the Polar filter.')
add('isBlePowered','POLAR_OP_RADIO')
add('checkIfDeviceDisconnectedDueRemovedPairing','POLAR_OP_PAIR_STATE','partial','Paired/protection state is available; it does not prove why a remote link was lost.')
add('setMtu','POLAR_OP_CONNECT','platform-difference','Windows negotiates ATT MTU; connection result reports usable ATT bytes, caller cannot force a requested MTU.')
add('foregroundEntered','polar_sdk_create','platform-difference','Desktop apps own context lifetime; no mobile foreground API.')
add('setApiCallback observer setApiLogger logger message','polar_sdk_poll_event','native','Caller pulls completion/error/diagnostic events; no callbacks cross the DLL boundary.')
add('getBatteryLevel','POLAR_OP_STREAM_START POLAR_BATTERY_LEVEL polar_sdk_decode_notification')
add('getChargerState','POLAR_OP_STREAM_START POLAR_BATTERY_STATUS polar_sdk_decode_notification')
add('getAvailableOnlineStreamDataTypes getAvailableOfflineRecordingDataTypes','POLAR_OP_PMD_FEATURES')
add('requestFullStreamSettings requestStreamSettings requestOfflineRecordingSettings requestFullOfflineRecordingSettings','POLAR_OP_PMD_SETTINGS','native','flags bit0=offline, bit1=full settings. Completion contains PMD settings bytes.')
add('startAccStreaming startEcgStreaming startGyroStreaming startHrStreaming startLocationStreaming startMagnetometerStreaming startPpgStreaming startPpiStreaming startPressureStreaming startSkinTemperatureStreaming startTemperatureStreaming','POLAR_OP_STREAM_START polar_sdk_read_samples','native','Fixed native samples; only device-supported measurement/format/settings combinations succeed.')
add('stopHrStreaming stopStreaming','POLAR_OP_STREAM_STOP polar_sdk_release_stream')
add('getAvailableHRServiceDataTypes','POLAR_OP_SERVICES','partial','HR/HTS/BAS service UUIDs are reported; a composite capability DTO is not exposed.')
add('startListenForPolarHrBroadcasts','POLAR_OP_SCAN_START POLAR_HR_ADVERTISEMENT polar_sdk_decode_notification','partial','Native manufacturer-data decoder is available; deduplication/filtering by device set is caller policy.')
add('enableSDKMode disableSDKMode isSDKModeEnabled getOfflineRecordingStatus getOfflineRecordingTriggerSetup setOfflineRecordingTrigger startOfflineRecording stopOfflineRecording requestDerivedMeasurementGroupIds requestDerivedMeasurementSettingsGroup startDerivedOfflineRecording stopDerivedOfflineRecording','POLAR_OP_PMD_COMMAND','protocol-only','Serialized native PMD transactions are implemented; callers currently supply command/settings wire bytes. Typed offline/trigger/derived control facade remains to port.')
add('getMultiBLEConnectionMode setMultiBLEConnectionMode getSensorInitiatedSecurityMode setSensorInitiatedSecurityMode setDaylightSavingTime setHibernateMode setWareHouseSleep setWarehouseSleep turnDeviceOff','POLAR_OP_PFCS','protocol-only','Native PFCS transaction implemented; typed command builders and feature readiness facade remain to port.')
add('automaticReconnection setAutomaticReconnection autoConnectToDevice startAutoConnectToDevice','POLAR_OP_CONNECT','pending','Explicit reconnect creates a fresh session; automatic discovery selection and reconnect policy are not implemented.')
add('deviceFeaturesObserver isFeatureReady deviceInfoObserver deviceHrObserver','POLAR_OP_SERVICES POLAR_OP_READ POLAR_OP_STREAM_START','partial','Native services/read/stream operations exist. Automatic aggregate observer lifecycle is not provided.')
add('powerStateObserver','POLAR_OP_RADIO','partial','Current radio state query exists; continuous radio observation is not implemented.')
add('getFile readFile','POLAR_OP_FILE_GET')
add('writeFile createFolder','POLAR_OP_FILE_PUT','native','An empty PUT to a directory path creates a folder on supported devices.')
add('deleteFileOrDirectory','POLAR_OP_FILE_REMOVE')
service('getFileList','POLAR_SERVICE_LIST_FILES')
service('getPolarUserDeviceSettings getUserDeviceSettings','POLAR_SERVICE_SETTINGS_GET')
service('setPolarUserDeviceSettings setUserDeviceSettings','POLAR_SERVICE_SETTINGS_MERGE')
service('setAutomaticOHRMeasurementEnabled','POLAR_SERVICE_OHR')
service('setAutomaticTrainingDetectionSettings','POLAR_SERVICE_AUTO_TRAINING')
service('setLedConfig','POLAR_SERVICE_LED')
service('setTelemetryEnabled','POLAR_SERVICE_TELEMETRY_ENABLE')
service('setUsbConnectionMode','POLAR_SERVICE_USB')
service('setUserDeviceLocation','POLAR_SERVICE_LOCATION')
service('getUserPhysicalConfiguration','POLAR_SERVICE_PHYSICAL')
service('isFtuDone','POLAR_SERVICE_FTU_STATUS')
service('doFirstTimeUse','POLAR_SERVICE_FTU')
service('setLocalTime','POLAR_SERVICE_TIME_SET')
service('getLocalTime getLocalTimeWithZone','POLAR_SERVICE_TIME_GET')
service('getDiskSpace','POLAR_SERVICE_DISK')
service('doFactoryReset doRestart','POLAR_SERVICE_RESET','Destructive command is implemented and tested only against a mock; device reset flags are explicit.')
service('sendInitializationAndStartSyncNotifications','POLAR_SERVICE_SYNC_START')
service('sendTerminateAndStopSyncNotifications','POLAR_SERVICE_SYNC_STOP')
for names,symbol in [
('get247HrSamples','AUTOMATIC'),('get247PPiSamples','AUTOMATIC_PPI'),('getActiveTime','ACTIVE_TIME'),('getActivitySampleData','ACTIVITY'),('getCalories','CALORIES'),('getDailySummaryData','DAILY'),('getDistance','DISTANCE'),('getNightlyRecharge','NIGHTLY'),('getSteps','STEPS'),('getSleep getSleepData','SLEEP'),('getSkinTemperature','SKIN_TEMP'),('getSpo2Test getSpo2TestData','SPO2'),('deleteStoredDeviceData','DELETE_STORED'),('deleteDeviceDateFolders','DELETE_DATE_FOLDERS'),('deleteTelemetryData','TELEMETRY_DELETE'),('getLogConfig','LOG_GET'),('setLogConfig','LOG_SET'),('exportDeviceLogs','LOG_EXPORT'),('getWatchFaceConfig','WATCHFACE_GET'),('setWatchFaceConfig','WATCHFACE_SET')]: service(names,'POLAR_SERVICE_'+symbol)
service('listOfflineRecordings listSplitOfflineRecordings','POLAR_SERVICE_OFFLINE_LIST','Native file/index listing; split files remain explicit entries, rather than mobile SDK grouped DTOs.')
add('getOfflineRecord getOfflineRecordWithProgress getSplitOfflineRecord getSubRecordingCount','POLAR_SERVICE_OFFLINE_GET polar_sdk_decode_recording','partial','Native ordered raw parts and REC decryption/decoding are available. Combined split-recording facade and transfer progress are not complete.','include/polar/polar_services.h')
service('removeOfflineRecord removeOfflineRecords','POLAR_SERVICE_OFFLINE_DELETE','Remove selected recording parts; preserves sibling measurement types and nonempty parent directories.')
service('fetchStoredExerciseList listExercises listOfflineExercisesV2 getTrainingSessionReferences','POLAR_SERVICE_EXERCISE_LIST')
service('fetchExercise fetchOfflineExerciseV2 getTrainingSession','POLAR_SERVICE_EXERCISE_GET')
add('getTrainingSessionWithProgress','POLAR_SERVICE_EXERCISE_GET','partial','Native session files, typed protobuf and gzip decode exist; incremental progress events remain incomplete.','include/polar/polar_services.h')
service('removeExercise removeOfflineExerciseV2 deleteTrainingSession','POLAR_SERVICE_EXERCISE_DELETE')
service('requestRecordingStatus startRecording stopRecording','POLAR_SERVICE_H10_RECORDING','flags: 0 status, 1 start, 2 stop. Start validates identifier/type/1-or-5-second interval.')
service('getExerciseStatus startExercise stopExercise pauseExercise resumeExercise startOfflineExerciseV2 stopOfflineExerciseV2 getOfflineExerciseStatusV2','POLAR_SERVICE_EXERCISE_CONTROL','flags selects native query 21..26; typed protobuf result preserves status enums.')
service('isOfflineExerciseV2Supported','POLAR_SERVICE_OFFLINE_EXERCISE_SUPPORT')
service('listRestApiServices getRestApiDescription','POLAR_SERVICE_REST_GET')
service('putNotification','POLAR_SERVICE_REST_PUT')
service('stopSleepRecording','POLAR_SERVICE_SLEEP_STOP')
add('observeSleepRecordingState observeSleepRecordingStatus receiveRestApiEvents','POLAR_SERVICE_SLEEP_SUBSCRIBE POLAR_CODEC_REST_EVENTS polar_sdk_poll_event','partial','Native REST event decompression and sleep status parsing exist. Async one-shot wait and typed observer facade are not supplied.','include/polar/polar_services.h')
add('getSleepRecordingState getSleepRecordingStatus','POLAR_SERVICE_SLEEP_SUBSCRIBE POLAR_CODEC_REST_EVENTS','partial','Subscribe plus native decode is available; subscribe-and-wait request with timeout remains to port.','include/polar/polar_services.h')
add('getAvailableTelemetryTypes getDeviceTelemetryConfiguration startTelemetry stopTelemetry','POLAR_OP_TELEMETRY','native','Native configuration/chunks with duplicate suppression and 5-bit gap count. No telemetry is uploaded.')
add('observeDeviceToHostNotifications observeExerciseStatus','POLAR_OP_SUBSCRIBE polar_sdk_poll_event','native','D2H notification stage=35, flags=notification ID; payload retains original protobuf bytes.')
add('checkFirmwareUpdate','POLAR_SERVICE_FIRMWARE_CHECK','partial','Native WinHTTP catalog request and response JSON validation implemented; newer/mandatory/version selection facade remains to port.','include/polar/polar_services.h')
service('updateFirmware','POLAR_SERVICE_FIRMWARE_UPDATE','ZIP validation, persisted backup before reset, reconnect, transfer SYSUPDAT last, restore and version check; mock success/failure only. Physical recovery unverified.')
# Native controls now include typed wire builders and native async lifecycle policy.
service('enableSDKMode disableSDKMode isSDKModeEnabled getOfflineRecordingStatus getOfflineRecordingTriggerSetup setOfflineRecordingTrigger startOfflineRecording stopOfflineRecording requestDerivedMeasurementGroupIds requestDerivedMeasurementSettingsGroup startDerivedOfflineRecording stopDerivedOfflineRecording','POLAR_SERVICE_PMD','Typed PMD settings, security, statuses and triggers are encoded/decoded in C++; ServiceRequest.flags selects command 1..12.')
service('getMultiBLEConnectionMode setMultiBLEConnectionMode getSensorInitiatedSecurityMode setSensorInitiatedSecurityMode','POLAR_SERVICE_FEATURE','Typed PFCS Boolean setters/getters check feature bits and preserve device status.')
service('setDaylightSavingTime','POLAR_SERVICE_SETTINGS_MERGE','Merge the typed daylight_saving submessage; other settings and unknown fields survive.')
service('setHibernateMode setWareHouseSleep setWarehouseSleep turnDeviceOff','POLAR_SERVICE_RESET','Explicit sleep/factory/hibernate flags; mock only.')
service('getSleepRecordingState getSleepRecordingStatus','POLAR_SERVICE_SLEEP_STATUS','Native subscribe-before-wait, cancellation/deadline, REST decompression and status parsing.')
service('checkFirmwareUpdate','POLAR_SERVICE_FIRMWARE_CHECK','Native numeric version comparison and optional mandatory flag; JSON source retained. Mocked HTTP only.')
add('automaticReconnection setAutomaticReconnection','POLAR_OP_RECONNECT_POLICY POLAR_EVENT_RECONNECTED','native','Event-driven two-second retry backoff; old streams invalidated and new device handle emitted. Stream restart is explicit. Scheduler tested with a mock BLE backend.')
add('autoConnectToDevice startAutoConnectToDevice','POLAR_OP_AUTO_CONNECT','native','Strongest matching connectable Polar advertisement after RSSI threshold/settle interval. Name prefix and cancellation supported; radio hardware selection unverified.')
callbacks={'PolarBleApiCallback','PolarBleApiCallbackProvider','PolarBleApiObservers'}
add('checkIfDeviceDisconnectedDueRemovedPairing','POLAR_OP_PAIR_STATE','platform-difference','Windows exposes current bond/protection state; it does not establish the cause of a prior disconnect. No pairing-removal diagnosis is fabricated.')
add('deviceFeaturesObserver isFeatureReady bleSdkFeatureReady bleSdkFeaturesReadiness','POLAR_OP_INITIALIZE POLAR_OP_CAPABILITIES POLAR_EVENT_FEATURES','native','Native preparation reports ready/unavailable/failed per feature in DeviceSnapshot; cached query and pull events replace callbacks.')
add('deviceInfoObserver disInformationReceived disInformationReceivedWithKeysAsStrings','POLAR_OP_DEVICE_INFO POLAR_EVENT_DEVICE_INFO','native','Native DIS read aggregation retains UUIDs and exact bytes, distinguishing binary attributes.')
add('powerStateObserver blePowerStateChanged blePowerOff blePowerOn','POLAR_OP_RADIO POLAR_EVENT_RADIO','native','flags 1 observes Windows Radio.StateChanged; flags 2 revokes observation. Radio hardware changes are not yet validated.')
add('deviceConnected','POLAR_EVENT_COMPLETE POLAR_EVENT_RECONNECTED')
add('deviceConnecting','POLAR_EVENT_CONNECTING')
add('deviceDisconnected','POLAR_EVENT_DISCONNECTED')
add('deviceHrObserver hrValueReceived hrNotificationReceived htsNotificationReceived batteryLevelReceived batteryChargingStatusReceived powerSourcesStateReceived batteryPowerSourcesStateReceived','POLAR_OP_STREAM_START polar_sdk_read_samples','native','Native HR/HTS/BAS batches replace per-sample mobile callbacks; no managed protocol parser.')
add('getAvailableHRServiceDataTypes','POLAR_OP_INITIALIZE POLAR_OP_CAPABILITIES','native','DeviceSnapshot.hr_measurements reports present HR/HTS services; per-feature readiness distinguishes preparation failures.')
add('startListenForPolarHrBroadcasts','POLAR_OP_SCAN_START POLAR_HR_ADVERTISEMENT polar_sdk_read_samples','native','flags bit2 creates a native broadcast stream; bit1 suppresses identical manufacturer payloads. Optional uint64 address filter; sample identifier retains BLE address.')
service('listOfflineRecordings listSplitOfflineRecordings','POLAR_SERVICE_OFFLINE_LIST POLAR_SERVICE_OFFLINE_GROUPS','Native raw entries and grouped ordered parts with exact uint64 total size.')
service('getSubRecordingCount','POLAR_SERVICE_OFFLINE_COUNT')
service('getOfflineRecord getOfflineRecordWithProgress getSplitOfflineRecord','POLAR_SERVICE_OFFLINE_GET polar_sdk_decode_recordings','Ordered raw REC parts and native split decoding preserve timestamps/header identity; coalesced per-file transfer progress is available.')
service('getTrainingSessionWithProgress','POLAR_SERVICE_EXERCISE_GET','Typed session records plus unknown files and original compressed bytes; progress is per-file, total may be unknown until completion.')
service('observeSleepRecordingState observeSleepRecordingStatus receiveRestApiEvents','POLAR_SERVICE_SLEEP_SUBSCRIBE POLAR_EVENT_REST POLAR_CODEC_REST_EVENTS','Native subscription and typed REST event payloads; raw D2H events also retained. Caller polls events on its own thread.')
platform={'PolarCompanionDeviceApi'}
source_text='\n'.join(p.read_text(encoding='utf-8-sig') for p in (ROOT/'include/polar').glob('*') if p.is_file())
output=[]
for row in rows:
    symbol,status,note,location=rules.get(row['member'],('','pending','No native semantic equivalent mapped yet.',''))
    if row['api'] in callbacks and row['member'] not in rules:
        symbol,status,note,location='polar_sdk_poll_event polar_sdk_read_samples','partial','Callback has no explicit semantic mapping yet.','include/polar/polar_sdk.h'
    if row['api'] in platform or row['member'].startswith('polarCompanionDevice'):
        symbol,status,note,location='POLAR_OP_PAIR_STATE POLAR_OP_PAIR POLAR_OP_SCAN_START','platform-difference','Android CompanionDeviceManager permissions/background execution and association UI have no Windows-equivalent contract. Native Windows pairing/scan are separate operations.','include/polar/polar_sdk.h'
    for token in symbol.split():
        assert re.search(r'\b'+re.escape(token)+r'\b',source_text),token
    row.update(status=status,implementation_symbols=symbol,implementation_locations=location,notes=note)
    output.append(row)
with (ROOT/'docs/api-inventory.csv').open('w',encoding='utf-8',newline='') as f:
    writer=csv.DictWriter(f,fieldnames=list(output[0]));writer.writeheader();writer.writerows(output)
summary={'schema_version':2,'native_version':'0.2.0-preview','abi':2,'upstream_commit':'b58e64dcd13d377447d125c16d7cd25ec928bd50','baseline_commit':'f73aca50dafabf7f3dcfe8bb867083922c9c447a','declarations':len(rows),'unique_api_members':len({(r['api'],r['member']) for r in rows}),'status_meanings':{'native':'Concrete native operation exists; does not claim full old-test parity or hardware verification.','partial':'Specific semantic/workflow subset exists; notes describe the remaining work.','protocol-only':'Raw native protocol transaction exists; typed high-level implementation remains.','pending':'Requested semantic behavior is not implemented.','platform-difference':'Mobile OS API is inapplicable or replaced explicitly on Windows.'},'declaration_counts':dict(Counter(r['status'] for r in output)),'unmapped':sorted({r['api']+'.'+r['member'] for r in output if not r['implementation_symbols']})}
(ROOT/'docs/api-mapping.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(summary,ensure_ascii=False,indent=2))
