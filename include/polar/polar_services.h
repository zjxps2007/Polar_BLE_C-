#ifndef POLAR_SERVICES_H
#define POLAR_SERVICES_H
#include "polar_sdk.h"
#ifdef __cplusplus
extern "C" {
#endif
enum {
    POLAR_CODEC_GZIP_DECODE = 1,
    POLAR_CODEC_GZIP_ENCODE = 2,
    POLAR_CODEC_AES128 = 3,
    POLAR_CODEC_AES256 = 4,
    POLAR_CODEC_XOR = 5,
    POLAR_CODEC_PROTO_ROUNDTRIP = 6,
    POLAR_CODEC_PROTO_MERGE = 7,
    POLAR_CODEC_WATCHFACE_GET = 8,
    POLAR_CODEC_WATCHFACE_SET = 9,
    POLAR_CODEC_ZIP_LIST = 10,
    POLAR_CODEC_JSON_VALIDATE = 11,
    POLAR_CODEC_REST_EVENTS = 12
};
/* POLAR_OP_SERVICE: command.flags is one of these IDs; command.data is a
   polar.sdk.ServiceRequest protobuf. Completion data is ServiceResult protobuf. */
enum {
    POLAR_SERVICE_LIST_FILES = 1,
    POLAR_SERVICE_READ_MESSAGE = 2,
    POLAR_SERVICE_MERGE_MESSAGE = 3,
    POLAR_SERVICE_TIME_SET = 10,
    POLAR_SERVICE_TIME_GET = 11,
    POLAR_SERVICE_DISK = 12,
    POLAR_SERVICE_SYNC_START = 13,
    POLAR_SERVICE_SYNC_STOP = 14,
    POLAR_SERVICE_RESET = 15,
    POLAR_SERVICE_SETTINGS_GET = 20,
    POLAR_SERVICE_SETTINGS_MERGE = 21,
    POLAR_SERVICE_LED = 22,
    POLAR_SERVICE_LOCATION = 23,
    POLAR_SERVICE_USB = 24,
    POLAR_SERVICE_TELEMETRY_ENABLE = 25,
    POLAR_SERVICE_OHR = 26,
    POLAR_SERVICE_AUTO_TRAINING = 27,
    POLAR_SERVICE_PHYSICAL = 28,
    POLAR_SERVICE_FTU_STATUS = 29,
    POLAR_SERVICE_FTU = 30,
    POLAR_SERVICE_DAILY = 40,
    POLAR_SERVICE_ACTIVITY = 41,
    POLAR_SERVICE_AUTOMATIC = 42,
    POLAR_SERVICE_NIGHTLY = 43,
    POLAR_SERVICE_SLEEP = 44,
    POLAR_SERVICE_SKIN_TEMP = 45,
    POLAR_SERVICE_SPO2 = 46,
    POLAR_SERVICE_STEPS = 47,
    POLAR_SERVICE_DISTANCE = 48,
    POLAR_SERVICE_CALORIES = 49,
    POLAR_SERVICE_OFFLINE_LIST = 50,
    POLAR_SERVICE_OFFLINE_GET = 51,
    POLAR_SERVICE_OFFLINE_DELETE = 52,
    POLAR_SERVICE_DELETE_STORED = 53,
    POLAR_SERVICE_DELETE_DATE_FOLDERS = 54,
    POLAR_SERVICE_AUTOMATIC_PPI = 55,
    POLAR_SERVICE_ACTIVE_TIME = 56,
    POLAR_SERVICE_OFFLINE_COUNT = 57,
    POLAR_SERVICE_OFFLINE_GROUPS = 58,
    POLAR_SERVICE_EXERCISE_LIST = 60,
    POLAR_SERVICE_EXERCISE_GET = 61,
    POLAR_SERVICE_EXERCISE_DELETE = 62,
    POLAR_SERVICE_H10_RECORDING = 63,
    POLAR_SERVICE_EXERCISE_CONTROL = 64,
    POLAR_SERVICE_OFFLINE_EXERCISE_SUPPORT = 65,
    POLAR_SERVICE_LOG_GET = 70,
    POLAR_SERVICE_LOG_SET = 71,
    POLAR_SERVICE_LOG_EXPORT = 72,
    POLAR_SERVICE_TELEMETRY_DELETE = 73,
    POLAR_SERVICE_WATCHFACE_GET = 80,
    POLAR_SERVICE_WATCHFACE_SET = 81,
    POLAR_SERVICE_WATCHFACE_COMPLICATIONS = 82,
    POLAR_SERVICE_REST_GET = 90,
    POLAR_SERVICE_REST_PUT = 91,
    POLAR_SERVICE_SLEEP_SUBSCRIBE = 92,
    POLAR_SERVICE_SLEEP_STOP = 93,
    POLAR_SERVICE_SLEEP_STATUS = 94,
    POLAR_SERVICE_FIRMWARE_CHECK = 100,
    POLAR_SERVICE_FIRMWARE_DOWNLOAD = 101,
    POLAR_SERVICE_FIRMWARE_BACKUP = 102,
    POLAR_SERVICE_FIRMWARE_RESTORE = 103,
    POLAR_SERVICE_FIRMWARE_UPDATE = 104,
    POLAR_SERVICE_PMD = 110,
    POLAR_SERVICE_FEATURE = 111
};
typedef struct polar_transform {
    uint32_t struct_size, operation;
    const uint8_t *data;
    uint32_t data_length, reserved;
    const uint8_t *parameters;
    uint32_t parameters_length, reserved2;
    const char *schema;
    uint32_t schema_length, reserved3;
} polar_transform;
/* Pure native transformations. Proto merge parses both operands with the official
   full runtime and preserves optional/unknown data. Output is length-delimited. */
POLAR_API polar_result POLAR_CALL polar_sdk_transform(const polar_transform *, uint8_t *, uint32_t,
                                                      uint32_t *);
typedef struct polar_offline_info {
    uint32_t struct_size, version, firmware_hash, header_free;
    char start_time_utc[20];
    uint32_t sample_rate;
    uint64_t sample_count, last_timestamp_ns;
} polar_offline_info;
/* Parse all REC packets into caller memory. For large recordings use capacity=0
   to discover the exact sample count before allocating. Key strategy: 0/1/2/3. */
POLAR_API polar_result POLAR_CALL polar_sdk_decode_recording(const uint8_t *, uint32_t, uint32_t,
                                                             const uint8_t *, uint32_t,
                                                             polar_offline_info *, polar_sample *,
                                                             uint32_t, uint32_t *);
typedef uint64_t polar_message;
typedef struct polar_recording_part {
    const uint8_t *data;
    uint32_t length, reserved;
} polar_recording_part;
typedef struct polar_recording_options {
    uint32_t struct_size, strategy, measurement, derived_methods;
    const uint8_t *key;
    uint32_t key_length, reserved;
} polar_recording_options;
/* Ordered split files, each with its own REC header. Preserves inter-part sample
   timing, validates header/firmware/type agreement before writing caller memory.
   measurement=UINT32_MAX disables filename type check. Inputs must stay immutable
   for the duration of the call. Capacity negotiation never changes output. */
POLAR_API polar_result POLAR_CALL polar_sdk_decode_recordings(const polar_recording_part *,
                                                              uint32_t,
                                                              const polar_recording_options *,
                                                              polar_offline_info *, polar_sample *,
                                                              uint32_t, uint32_t *);
typedef struct polar_field {
    uint32_t struct_size, kind, present, count;
    int64_t signed_value;
    uint64_t unsigned_value;
    double real_value;
    uint32_t data_length, reserved;
} polar_field;
/* kind matches google.protobuf.FieldDescriptor::CppType: int32=1,int64=2,
   uint32=3,uint64=4,double=5,float=6,bool=7,enum=8,string/bytes=9,message=10.
   Field paths use protobuf names and '.' for nested messages. index selects a
   repeated terminal field. Missing fields remain present=0. Unknown fields are
   preserved by SerializePartial, including unknown proto2 enum values. */
POLAR_API polar_result POLAR_CALL polar_sdk_message_parse(const char *, uint32_t, const uint8_t *,
                                                          uint32_t, polar_message *);
POLAR_API polar_result POLAR_CALL polar_sdk_message_field(polar_message, const char *, uint32_t,
                                                          uint32_t, polar_field *, uint8_t *,
                                                          uint32_t, uint32_t *);
POLAR_API polar_result POLAR_CALL polar_sdk_message_serialize(polar_message, uint8_t *, uint32_t,
                                                              uint32_t *);
POLAR_API polar_result POLAR_CALL polar_sdk_message_destroy(polar_message);
/* Mutate a native protobuf message without a managed protobuf runtime. Set kind
   to the exact CppType. present=0 clears the field; UINT32_MAX appends a repeated
   value. Nested singular messages use dot paths. Message values are wire bytes.
   Unknown enum numbers are stored in the protobuf unknown field set. */
POLAR_API polar_result POLAR_CALL polar_sdk_message_set(polar_message, const char *, uint32_t,
                                                        uint32_t, const polar_field *,
                                                        const uint8_t *, uint32_t);
#ifdef __cplusplus
}
#endif
#endif
