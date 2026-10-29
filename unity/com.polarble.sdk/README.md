# Polar Native SDK for Unity 6

UPM 0.2.0-preview.1 / native ABI v2. Windows x64, Unity 6000.3.24f1 Editor, Mono and IL2CPP. Protocols, BLE, protobuf, crypto and file services execute in `polar_sdk.dll`; C# owns Unity lifetime and batch buffers.

Install this folder's `package.json` from Package Manager or install the packaged TGZ. The package needs Unity Collections 2.6.3. Import **Live Sensor**, then choose **Polar → Create Native Sensor Sample** and enter Play. Replay is enabled by default. Disable Replay and enter a discovered BLE address to request real ACC streaming.

```csharp
var context = new Polar.Native.PolarContext();
context.EventReceived += OnControlEvent;
ulong request = context.Connect(address, automaticReconnect: true);
// On the matching COMPLETE: context.Start(device, Measurement.Acc).
// On its COMPLETE: context.AttachStream(stream).
// In Update: consume stream.Samples[0..stream.Count] without creating per-sample objects.
// OnDestroy / Play Stop: context.Dispose().
```

`Initialize(device, featureMask)` returns feature readiness and DIS information as a native `polar.sdk.DeviceSnapshot`. `Capabilities` reads the snapshot. `ObserveRadio` starts/stops Windows radio notifications. `HrBroadcasts` creates a native advertisement sample stream with an optional address filter. Constants are generated in `NativeEnums.cs` from the C headers.

For settings, offline files and other services, create `NativeMessage("polar.sdk.ServiceRequest")`, set typed fields, and `Submit(context, device, Service.X)`. Parse the result with `NativeMessage("polar.sdk.ServiceResult", bytes)`. `NativeFormats` performs gzip/AES/ZIP/protobuf/REC work inside the native DLL. These control APIs allocate and belong outside the steady-state frame receiver.

Persistent sample buffers are reused each EarlyUpdate; consume them during that frame. C# callbacks and Unity object changes stay on the main thread. Stop, drain pending/queued frames, and release streams; shutdown cancels and joins native work. Old handles remain invalid after disconnect. Restart streams using the new handle in a Reconnected event. The SDK never replays arbitrary writes after reconnect.

See [ABI](Documentation~/ABI.md), [implementation status](Documentation~/PORTING-STATUS.md), and [verification status](Documentation~/VERIFICATION.md). Final source verification and long-duration trials are pending. Verity Sense 3.0.16 has not passed live HR/ACC reception because Windows GATT discovery returned Unreachable. Earlier short replay measurements do not certify this package's final binary or sensor support.
