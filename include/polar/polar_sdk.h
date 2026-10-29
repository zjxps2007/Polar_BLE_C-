#ifndef POLAR_SDK_H
#define POLAR_SDK_H
#include <stdint.h>
#if defined(_WIN32)
#ifdef POLAR_SDK_BUILD
#define POLAR_API __declspec(dllexport)
#else
#define POLAR_API __declspec(dllimport)
#endif
#define POLAR_CALL __cdecl
#else
#define POLAR_API
#define POLAR_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif
#define POLAR_ABI_VERSION 2u
#define POLAR_VERSION "0.2.0-preview"
typedef uint64_t polar_context;
typedef uint64_t polar_device;
typedef uint64_t polar_stream;
typedef uint64_t polar_request;
typedef int32_t polar_result;
enum {
    POLAR_OK = 0,
    POLAR_EMPTY = 1,
    POLAR_BUFFER_TOO_SMALL = 2,
    POLAR_INVALID_ARGUMENT = -1,
    POLAR_INVALID_HANDLE = -2,
    POLAR_DISCONNECTED = -3,
    POLAR_CANCELLED = -4,
    POLAR_TIMEOUT = -5,
    POLAR_PROTOCOL_ERROR = -6,
    POLAR_NOT_SUPPORTED = -7,
    POLAR_QUEUE_OVERFLOW = -8,
    POLAR_OS_ERROR = -9,
    POLAR_BUSY = -10,
    POLAR_INTERNAL_ERROR = -11,
    POLAR_DEVICE_ERROR = -12
};
enum { POLAR_LATEST = 0, POLAR_LOSSLESS = 1 };
/* INITIALIZE flags select a bitset of these IDs; zero requests all.
   CAPABILITIES returns cached polar.sdk.DeviceSnapshot. Ready means prepared
   transport/capability, not a guarantee that every file or command exists. */
enum {
    POLAR_FEATURE_HR = 0,
    POLAR_FEATURE_DEVICE_INFO = 1,
    POLAR_FEATURE_BATTERY = 2,
    POLAR_FEATURE_STREAMING = 3,
    POLAR_FEATURE_OFFLINE = 4,
    POLAR_FEATURE_H10_RECORDING = 5,
    POLAR_FEATURE_OFFLINE_EXERCISE = 6,
    POLAR_FEATURE_TIME = 7,
    POLAR_FEATURE_SDK_MODE = 8,
    POLAR_FEATURE_FILES = 9,
    POLAR_FEATURE_THERMOMETER = 10,
    POLAR_FEATURE_LED = 11,
    POLAR_FEATURE_FIRMWARE = 12,
    POLAR_FEATURE_ACTIVITY = 13,
    POLAR_FEATURE_SLEEP = 14,
    POLAR_FEATURE_TEMPERATURE = 15,
    POLAR_FEATURE_SPO2 = 16,
    POLAR_FEATURE_TRAINING = 17,
    POLAR_FEATURE_DEVICE_CONTROL = 18,
    POLAR_FEATURE_PFCS = 19,
    POLAR_FEATURE_WATCHFACE = 20,
    POLAR_FEATURE_TELEMETRY = 21
};
enum {
    POLAR_ECG = 0,
    POLAR_PPG = 1,
    POLAR_ACC = 2,
    POLAR_PPI = 3,
    POLAR_GYRO = 5,
    POLAR_MAG = 6,
    POLAR_SKIN_TEMP = 7,
    POLAR_SDK_MODE = 9,
    POLAR_LOCATION = 10,
    POLAR_PRESSURE = 11,
    POLAR_TEMP = 12,
    POLAR_OFFLINE_HR = 14,
    POLAR_DERIVED = 15,
    POLAR_HR = 64,
    POLAR_THERMOMETER = 65,
    POLAR_BATTERY_LEVEL = 66,
    POLAR_BATTERY_STATUS = 67,
    POLAR_HR_ADVERTISEMENT = 68
};
enum {
    POLAR_EVENT_COMPLETE = 1,
    POLAR_EVENT_ADVERTISEMENT = 2,
    POLAR_EVENT_DISCONNECTED = 3,
    POLAR_EVENT_ERROR = 4,
    POLAR_EVENT_RADIO = 5,
    POLAR_EVENT_MEASUREMENT_STOPPED = 6,
    POLAR_EVENT_DIAGNOSTIC = 7,
    POLAR_EVENT_PROGRESS = 8,
    POLAR_EVENT_NOTIFICATION = 9,
    POLAR_EVENT_RECONNECTING = 10,
    POLAR_EVENT_RECONNECTED = 11,
    POLAR_EVENT_DEVICE_INFO = 12,
    POLAR_EVENT_FEATURES = 13,
    POLAR_EVENT_REST = 14,
    POLAR_EVENT_CONNECTING = 15
};
enum {
    POLAR_OP_SCAN_START = 1,
    POLAR_OP_SCAN_STOP = 2,
    POLAR_OP_CONNECT = 3,
    POLAR_OP_DISCONNECT = 4,
    POLAR_OP_PAIR_STATE = 5,
    POLAR_OP_PAIR = 6,
    POLAR_OP_SERVICES = 7,
    POLAR_OP_RADIO = 8,
    POLAR_OP_READ = 9,
    POLAR_OP_WRITE = 10,
    POLAR_OP_SUBSCRIBE = 11,
    POLAR_OP_AUTO_CONNECT = 12,
    POLAR_OP_RECONNECT_POLICY = 13,
    POLAR_OP_INITIALIZE = 14,
    POLAR_OP_DEVICE_INFO = 15,
    POLAR_OP_CAPABILITIES = 16,
    POLAR_OP_PMD_FEATURES = 20,
    POLAR_OP_PMD_SETTINGS = 21,
    POLAR_OP_STREAM_START = 22,
    POLAR_OP_STREAM_STOP = 23,
    POLAR_OP_PMD_COMMAND = 24,
    POLAR_OP_FILE_GET = 30,
    POLAR_OP_FILE_PUT = 31,
    POLAR_OP_FILE_REMOVE = 32,
    POLAR_OP_FILE_LIST = 33,
    POLAR_OP_QUERY = 34,
    POLAR_OP_NOTIFICATION = 35,
    POLAR_OP_PFCS = 40,
    POLAR_OP_TELEMETRY = 41,
    POLAR_OP_SERVICE = 50,
    POLAR_OP_REPLAY_OPEN = 100,
    POLAR_OP_REPLAY_CLOSE = 101
};
/* All structures use the platform's normal 8-byte packing on Windows x64.
   Strings are UTF-8 with explicit lengths. Inputs are copied before submit returns.
   Every accepted request produces one COMPLETE event unless its context is destroyed.
   All handles are invalid after context destruction. */
typedef struct polar_config {
    uint32_t struct_size, abi_version;
    uint32_t sample_capacity, ingress_capacity, event_capacity, overflow_policy;
    uint32_t command_timeout_ms, reserved;
} polar_config;
typedef struct polar_command {
    uint32_t struct_size, operation;
    polar_device device;
    polar_stream stream;
    uint64_t address;
    uint32_t measurement, flags;
    const char *path;
    uint32_t path_length, timeout_ms;
    const uint8_t *data;
    uint32_t data_length, reserved;
    uint8_t service_uuid[16], characteristic_uuid[16];
} polar_command;
typedef struct polar_event {
    uint32_t struct_size, type;
    polar_request request;
    polar_device device;
    polar_stream stream;
    uint64_t host_time_ns;
    int32_t result, hresult;
    uint32_t att_error, stage;
    uint64_t elapsed_ns;
    uint32_t data_length, flags;
} polar_event;
/* PROGRESS payload is this header followed by path_length UTF-8 bytes.
   Progress is coalesced per request. total_bytes=0 means unknown; direction is
   0 receive / 1 transmit. File counters reset for each path in a service request. */
typedef struct polar_progress {
    uint64_t transferred_bytes, total_bytes;
    uint32_t path_length, direction;
} polar_progress;
typedef struct polar_advertisement {
    uint64_t address;
    int32_t rssi;
    uint32_t connectable;
    char name[128];
    uint32_t manufacturer_length;
    uint8_t manufacturer[256];
} polar_advertisement;
/* Values contain only measurements <=32-bit integers or floating point. Exact
   64-bit identifiers go in identifier; timestamps never pass through double.
   metadata is format-specific raw bytes (PPG gains, location satellite masks,
   NMEA, etc.), preserved without JSON conversion. A larger payload is rejected,
   never silently truncated. See docs/ABI.md for format/flag semantics. */
typedef struct polar_sample {
    uint64_t timestamp_ns, received_ns, ready_ns, sequence, identifier;
    uint32_t measurement, format, flags, channel_count;
    double values[32];
    int64_t integers[8];
    uint32_t metadata_length, reserved;
    uint8_t metadata[512];
} polar_sample;
typedef struct polar_stream_info {
    uint32_t struct_size, measurement;
    uint64_t queued_samples, produced_samples, consumed_samples, skipped_samples;
    uint64_t ingress_dropped, high_watermark;
    int32_t state, error;
    uint32_t sample_rate, pending_frames;
} polar_stream_info;
/* Logical bytes copied by SDK ingress, decode handoff, sample queue and batch
   read. Excludes the Windows Bluetooth stack, caller copies and CPU caches. */
typedef struct polar_stream_metrics {
    uint32_t struct_size, reserved;
    uint64_t ingress_frames, ingress_bytes, decoder_bytes, sample_queue_bytes, consumer_bytes;
} polar_stream_metrics;
POLAR_API polar_result POLAR_CALL polar_sdk_get_stream_metrics(polar_context, polar_stream,
                                                               polar_stream_metrics *);
/* Release a stopped stream after draining samples. Active streams return BUSY. */
POLAR_API polar_result POLAR_CALL polar_sdk_release_stream(polar_context, polar_stream);
typedef struct polar_decode_options {
    uint32_t struct_size, sample_rate;
    uint64_t previous_timestamp;
    float factor;
    uint32_t derived_methods;
    uint64_t received_ns;
} polar_decode_options;
POLAR_API uint32_t POLAR_CALL polar_sdk_abi_version(void);
POLAR_API uint64_t POLAR_CALL polar_sdk_clock_ns(void);
POLAR_API polar_result POLAR_CALL polar_sdk_create(const polar_config *, polar_context *);
/* destroy cancels requests, revokes WinRT handlers and joins workers. It may block;
   call on Play stop, never in a frame's data path. Stale handles stay invalid. */
POLAR_API polar_result POLAR_CALL polar_sdk_destroy(polar_context);
POLAR_API polar_result POLAR_CALL polar_sdk_submit(polar_context, const polar_command *,
                                                   polar_request *);
POLAR_API polar_result POLAR_CALL polar_sdk_cancel(polar_context, polar_request);
/* A short event buffer leaves the event queued and reports its required size. */
POLAR_API polar_result POLAR_CALL polar_sdk_poll_event(polar_context, polar_event *, uint8_t *,
                                                       uint32_t, uint32_t *);
POLAR_API polar_result POLAR_CALL polar_sdk_read_samples(polar_context, polar_stream,
                                                         polar_sample *, uint32_t, uint32_t *);
POLAR_API polar_result POLAR_CALL polar_sdk_stream_info(polar_context, polar_stream,
                                                        polar_stream_info *);
/* Replay enters the same bounded ingress/decoder/sample queues as BLE. */
POLAR_API polar_result POLAR_CALL polar_sdk_replay_frame(polar_context, polar_stream,
                                                         const uint8_t *, uint32_t, uint64_t);
/* Stateless parser. No output is written on insufficient capacity. */
POLAR_API polar_result POLAR_CALL polar_sdk_decode_frame(const uint8_t *, uint32_t,
                                                         const polar_decode_options *,
                                                         polar_sample *, uint32_t, uint32_t *);
POLAR_API polar_result POLAR_CALL polar_sdk_decode_notification(uint32_t, const uint8_t *, uint32_t,
                                                                uint64_t, polar_sample *, uint32_t,
                                                                uint32_t *);
#ifdef __cplusplus
}
#endif
#endif
