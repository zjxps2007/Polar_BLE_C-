# Polar protobuf schemas

These schemas were copied from `polarofficial/polar-ble-sdk`, commit
`b58e64dcd13d377447d125c16d7cd25ec928bd50`, from
`sources/Android/android-communications/library/src/sdk/proto/`.

The only schema change is an explicit C# namespace option:
`option csharp_namespace = "PolarBle.Protocol.Protobuf";`.
Field numbers, protobuf packages, field types, custom options and enum values are preserved.
The `Google.Protobuf` package supplies the standard descriptor schema; the upstream copy
of `google/protobuf/descriptor.proto` is not compiled into this assembly.

Build-time C# generation uses `Grpc.Tools`; there is no gRPC transport or server dependency.
Generated messages retain fields for device data, including values not mapped to convenience
properties. Runtime device support is not implied by the presence of a generated message.

The Polar SDK license in `LICENSES/Polar_SDK_License.txt` applies to these schemas and their
derived generated code. Third-party schema notices remain in their original files.
