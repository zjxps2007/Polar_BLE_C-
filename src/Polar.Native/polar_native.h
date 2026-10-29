#ifndef POLAR_NATIVE_H
#define POLAR_NATIVE_H
#include <stdint.h>

/* Polar BLE native ABI v1. All functions use cdecl. Handles and request IDs are int64.
 * Payloads: little-endian integers; strings = int32 byte count + strict UTF-8, no NUL;
 * buffers = int32 byte count + bytes; GUIDs = 16 bytes in .NET Guid.ToByteArray order.
 * polar_submit copies its input before returning. Caller owns every input/output buffer.
 * Operation IDs:
 * 1 StartScan, 2 StopScan: empty input/output.
 * 3 Connect: input string deviceId; completion connection handle + int32 maxWriteSize.
 * 4 Discover: no input; int32 maxWriteSize, int32 serviceCount, repeated
 *   { GUID service, int32 characteristicCount, repeated { GUID characteristic, int32 properties } }.
 * 5 Read: GUID service + GUID characteristic; output raw characteristic bytes.
 * 6 Write: GUID service + GUID characteristic + uint8 withResponse + buffer data; empty output.
 * 7 Subscribe: GUID service + GUID characteristic + uint8 enabled; empty output.
 * 8 Disconnect: empty input/output. Repeated disconnect is harmless.
 * 9 Pair: input string deviceId; empty output. Normal OS encrypted pairing, no unpair/reset.
 * 10 GetRadioState: empty input; int32 state (0 Unknown, 1 On, 2 Off, 3 Disabled, 4 Unavailable).
 * Event kinds:
 * 1 success, 2 request error, 3 advertisement, 4 notification, 5 disconnected, 6 background error.
 * 8 radio state changed: int32 state using the same values as operation 10. Query/observe only.
 * Error payload: int32 category (1 canceled, 2 disconnected, 3 other), string operation,
 *   string exceptionType, string message.
 * Advertisement: string id, string name, uint64 address, int16 RSSI, int32 manufacturerCount,
 *   repeated { uint16 companyId, buffer data }, int32 serviceUuidCount, repeated GUID.
 * Notification: GUID service + GUID characteristic + buffer data.
 * Event 5 has no payload. Non-request events have request ID zero.
 * poll returns 0 empty, 1 copied+removed, 2 buffer too small (event retained), -1 error.
 * Only one poll consumer per context. last_error is thread-local UTF-8, no NUL.
 * Queue overflow is reported as event 6 and indicates loss of sample/advertisement events.
 * Cancellation ends the caller wait; Windows may still finish an outstanding GATT command.
 * Destroy cancels requests and closes all sessions. Never unload this NativeAOT DLL:
 * FreeLibrary/dlclose of NativeAOT libraries is not supported. Recreate contexts instead.
 */
#ifdef __cplusplus
extern "C" {
#endif
int32_t polar_abi_version(void);
int64_t polar_create(void);
int32_t polar_destroy(int64_t context);
int32_t polar_submit(int64_t context, int64_t request, int32_t operation, int64_t connection, const uint8_t* data, int32_t length);
int32_t polar_cancel(int64_t context, int64_t request);
int32_t polar_max_write_size(int64_t context, int64_t connection);
int32_t polar_poll(int64_t context, int32_t* kind, int64_t* request, int64_t* connection, uint8_t* buffer, int32_t capacity, int32_t* required);
int32_t polar_last_error(uint8_t* buffer, int32_t capacity);
#ifdef __cplusplus
}
#endif
#endif
