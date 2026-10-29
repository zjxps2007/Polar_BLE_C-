# ABI v2 contract

The ABI uses Windows x64 default 8-byte packing and C `__cdecl` exports. Every structure with `struct_size` must set it to `sizeof(struct)`. Version is 2. Fields called reserved must be zero. No STL object, allocator ownership, C++ exception, callback delegate, or protobuf runtime object crosses the DLL boundary. Input pointers must point to readable memory for their specified lengths; the ABI cannot validate arbitrary process addresses.

## Ownership and lifecycle

- `create` owns one MTA control worker and one event-driven decode worker. There is no periodic 5 ms native poll.
- `submit` copies path/data before returning and reserves one completion event slot. A successful submission gets one `COMPLETE`, including cancellation/failure, unless the context is destroyed. A full control queue rejects submission with `QUEUE_OVERFLOW`.
- Control work is serialized per context. Native reconnect retries use an event-driven backoff without occupying the worker during the delay. A request already inside an OS operation may delay later requests; cancellation signals its wait.
- `cancel` wakes the request. Late WinRT completions cannot publish an abandoned device or write into a new stream. Synchronous WinHTTP calls have a 5-second I/O timeout; cancellation is checked between blocking operations, not an instant abort of the OS call.
- All handles are monotonic opaque uint64 values. Disconnect invalidates device streams. Automatic reconnect emits `RECONNECTING` and `RECONNECTED`, with a fresh device handle. Restart streams explicitly.
- `destroy` cancels requests, revokes handlers and joins workers; it may block. Never call it in the steady-state per-frame receive path.
- Stop first, then drain while `pending_frames` or `queued_samples` is nonzero, then release a stream. `release_stream` rejects active/in-flight streams. Explicit release discards any unread samples still queued.

## Results and queues

`OK=0`, `EMPTY=1`, `BUFFER_TOO_SMALL=2`; errors are negative (see header). A short event buffer reports the required byte length without dequeuing. Data reads copy up to capacity into caller-owned `polar_sample[]`; drain repeatedly if the application requires all currently queued samples.

`LATEST` discards oldest sample data under pressure and increments `skipped_samples`; ingress drops are counted separately and reset timestamp interpolation. `LOSSLESS` faults the stream on overflow and returns `QUEUE_OVERFLOW`; it does not silently discard and continue. `stream_info` reports state, error, produced/consumed/skipped counts, queue depth, pending decoder frames and high watermark. Control errors are separate from sensor data. Progress is coalesced per request and may be skipped when the event queue is full; completion is reserved.

`event.hresult` and `att_error` contain OS/protocol values only when supplied. Zero does not prove that the failing layer returned a code. `stage` identifies the operation stage; `elapsed_ns` is host duration. `DIAGNOSTIC`/error payloads are length-delimited UTF-8. No SDK errors are silently converted into success.

## Operations

| Operation | Input / output |
|---|---|
| SCAN_START / STOP | flags bit0 includes non-Polar advertisements. ADVERTISEMENT payload is `polar_advertisement`. |
| CONNECT | `address`; bit0 uses cached GATT, bit1 enables reconnect. Optional 16-byte `polar_connect_options` in data selects feature mask and authentication policy. Empty data prepares HR/DIS/BAS (mask 7). Completion follows initialization: uint32 usable ATT payload, uint32 paired, then 16-byte Windows GUIDs. |
| AUTO_CONNECT | optional `data`: int32 minimum RSSI + uint32 settle milliseconds, optionally followed by `polar_connect_options`; `path`: name prefix. Chooses strongest matching Polar advertisement, then uses the same initialization flow. |
| RECONNECT_POLICY | `device`, flags 0/1. Disabling also cancels queued retries for that session. |
| PAIR_STATE / PAIR | `address`; read state / explicitly request Windows pairing. Normal CONNECT does not require pre-pairing. |
| UNPAIR (17) | `address`; explicit Windows bond recovery. Invalidates this context's target device/streams, cancels queued reconnects, returns uint32 DeviceUnpairingResultStatus. Never invoked automatically by CONNECT. |
| RADIO | flags 0 snapshot, 1 observe, 2 stop observing. Snapshot uint32: 0 unknown, 1 on, 2 off, 3 disabled, 4 no Bluetooth radio. RADIO events carry Windows RadioState 0..3 in flags and payload. |
| SERVICES | `device`; returned GUIDs use Windows in-memory GUID layout, 16 bytes each. |
| READ / WRITE / SUBSCRIBE | `device`, service/characteristic GUIDs; WRITE bit0 with response; SUBSCRIBE bit0 enabled. |
| PMD_FEATURES / SETTINGS | `measurement`; settings bit0 offline, bit1 full. Returned original PMD settings bytes. |
| STREAM_START / STOP | `device`, `measurement`, optional settings bytes / `stream`. Completion gives stream handle. Supports standard HR/HTS/BAS and PMD. |
| PMD_COMMAND / PFCS | Advanced native protocol transaction; `data` contains command bytes. Prefer typed SERVICE controls. |
| FILE_GET / PUT / REMOVE / LIST | `device`, UTF-8 `path`, optional `data`. PFTP transport/progress stays native. |
| QUERY / NOTIFICATION | command `flags` selects query/notification ID; protobuf payload in data. |
| TELEMETRY | flags select configuration/start/stop (see native backend); chunks carry native sequence-gap diagnostics, never upload automatically. |
| SERVICE | flags is `POLAR_SERVICE_*`, data is `polar.sdk.ServiceRequest`, completion is `polar.sdk.ServiceResult`. |
| REPLAY_OPEN / CLOSE | measurement ACC; flags 0 manual frames, 1..4 native replay load. Replays enter the same ingress/decoder queues. |

Typed service inputs/outputs are documented by `schemas/native_sdk.proto`, the public `polar_services.h` IDs and `native/device_services.cpp`/`control_services.cpp`. `NativeMessage`/C++ `message` set typed fields using reflection; no managed protobuf is required. Do not infer that a device implements a service merely because its SDK operation exists.

## Samples and precision

`polar_sample` is 896 bytes. `timestamp_ns` retains the device clock, not wall-clock UTC. PMD timestamps are never converted through floating point. `received_ns`/`ready_ns` use a process-host monotonic QPC time base; use `polar_sdk_clock_ns()` to calculate host latency. OS-to-SDK radio scheduling and device batching precede `received_ns` and are excluded from SDK processing measurements.

`values[32]` holds measurement values that fit in 32-bit integers or floating point. `identifier` and `integers[8]` retain exact larger integers. `channel_count` bounds values. `metadata[512]` preserves bounded format-specific bytes; larger data is rejected. PMD `format` is the original format number; flags bit31 indicates compressed input. Location, PPG gain, satellite and NMEA formats use metadata and exact integer fields. Their authoritative per-format mapping is `native/decoder.cpp` and the 72 golden binary fixtures.

HR: values[0]=BPM, values[1]=RR milliseconds when present; energy is integers[0], absent=-1. Flags retain the original HR flags. HR device timestamp is absent (zero). HTS uses measurement value, exact calendar components in integers[0..5], type in [6] (absent=-1), and special IEEE-11073 status in [7]; raw payload is retained. BAS and advertisement flags/raw metadata remain native.

## Protobuf and offline files

`message_parse/field/set/serialize` use official full C++ Protobuf. Missing fields remain `present=0`. Unknown fields and unknown proto2 enum numbers survive serialization. Field paths use protobuf names and dots for singular submessages; an index selects a repeated terminal field and UINT32_MAX appends. `kind` matches the descriptor CppType. A uint64 identifier stays unsigned 64-bit. Serialization may normalize wire ordering; byte-for-byte identity is not promised.

`decode_recordings` accepts ordered split files, validates headers/type agreement before copying output, and carries timestamp interpolation across parts. A zero-capacity call reports the required sample count. Keep parts/key memory immutable throughout each call. Compression/encryption/ZIP transformations are bounded control operations and allocate native temporary memory. They are not part of the zero-allocation streaming claim.

## Thread and application boundary

The C API synchronizes native state, but the application must not free caller buffers during calls. Unity `PolarContext` is main-thread-only; callbacks and Unity APIs run on the main thread. Its Persistent NativeArray batch is reused next frame. Use `Count`, not array capacity. Application-side LINQ, closures, formatting, copying, recording or graph allocations are outside the native parser claim and can invalidate an app's zero-allocation target.

## Native initialization and observers

`CONNECT` prepares the selected features before its completion. `polar_connect_options` has four uint32 fields: struct_size=16, feature_mask, authentication (0 on demand, 1 disabled), reserved=0. A zero feature mask explicitly requests transport only. ACC callers can request mask 15 (HR/DIS/BAS/PMD). Missing services are reported as unavailable; a failed requested feature fails CONNECT. STREAM_START also prepares its required feature if it is not ready yet.

`CONNECTION_STATE` (16) carries the 32-byte `polar_connection_state`: connecting, discovering, negotiating, initializing, authenticating, ready or failed; requested/ready/unavailable/failed masks; MaxPduSize; Windows pairing status (UINT32_MAX before an attempt); authentication attempt count. The event's device handle is provisional until successful CONNECT completion or RECONNECTED. READY proves feature preparation, not receipt of live samples. State payloads are binary even when result is negative. DIAGNOSTIC stages 60..67 identify the client being prepared.

`INITIALIZE` explicitly adds/refreshes features (flags=0 selects all 22) and returns `polar.sdk.DeviceSnapshot` with per-feature ready/unavailable/failed state plus result/HRESULT/ATT fields. `CAPABILITIES` returns the cached snapshot; `DEVICE_INFO` refreshes DIS bytes. HR/HTS/BAS data still uses explicit stream start and batched reads. A ready transport does not guarantee that every later Polar file/command exists.

Protected read/write/CCCD operations handle ATT 0x05/0x0F through one native authentication policy. The same GATT session and operation are retained; there are at most two authentication recovery stages. Idempotent reads/CCCD writes may retry every 500 ms for up to 12 seconds while Windows encryption is settling (bounded by a shorter command timeout). Arbitrary writes with an uncertain Unreachable outcome are not replayed. Cancel/disconnect checks reject late completions. `AUTHENTICATION_FAILED=-13` ends the request and disables automatic reconnect for that failed session. Windows owns MTU/PHY; optional Windows 11 balanced connection preferences and negotiation events are used without raising the Windows 10 minimum. Reconnects preserve feature/authentication options and back off 2/4/8/16 seconds. The public WinRT disconnect event does not expose HCI key-error codes; the SDK does not invent them from a generic disconnect.

SCAN_START flags bit2 returns an HR advertisement stream, bit1 suppresses identical manufacturer payloads, and data optionally supplies a uint64 BLE-address list. `sample.identifier` contains the exact advertisement address. SCAN_STOP stops the broadcast stream; drain and release it normally. These scan flags do not alter sensor sampling.

REST D2H notifications retain their original event and additionally emit `REST` events containing `polar.sdk.RestEvents`; native gzip/JSON parsing supplies optional sleep state. Offline group/count services return typed metadata and ordered raw REC parts. `StoredFile.source_data` retains original compressed bytes when a typed file was decompressed; unrecognized files and firmware backups remain byte-preserving.

REPLAY_OPEN flags low byte selects load 1..4; bit8 enables synthetic motion for the interactive sample. Performance trials leave bit8 clear for stable inputs. `get_stream_metrics` reports logical bytes copied by SDK stages, excluding the Windows BLE stack, application copies and CPU cache traffic.
