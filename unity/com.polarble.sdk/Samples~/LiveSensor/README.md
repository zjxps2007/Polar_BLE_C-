# Native sensor sample

Import this sample from Package Manager, then choose **Polar → Create Native Sensor Sample**.
Press Play: the C++ replay producer moves the object and graph through the same bounded receive queues as BLE.

To use a sensor, stop Play, disable `Replay`, and enter the 12-digit BLE address discovered by the native diagnostic tool. ACC must be supported by that device. The C# component only consumes native samples and updates Unity objects; it contains no PMD parser or packet builder.

The sample enables native reconnect and starts a new ACC stream when a fresh device handle arrives. Play Stop disposes the context. Its graph/object drawing cost is separate from the minimal performance validation scene. An actual sensor connection is still unverified on the target Verity Sense because Windows GATT discovery has returned Unreachable.
