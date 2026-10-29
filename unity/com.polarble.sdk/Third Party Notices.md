# Third-party components

This package contains modified code and protocol schemas from [Polar BLE SDK](https://github.com/polarofficial/polar-ble-sdk/tree/b58e64dcd13d377447d125c16d7cd25ec928bd50), subject to the included custom Polar SDK license. It also includes these runtime dependencies:

| Component | Version | License and notice files |
|---|---|---|
| Polar BLE SDK protocol implementation and schemas | pinned source above | `LICENSE.md`, `NOTICE.md`, `Licenses~/UpstreamThirdPartySoftwareListing.txt` |
| Google.Protobuf | 3.31.1 | `Licenses~/Google.Protobuf_LICENSE.txt` (BSD-3-Clause) |
| System.Runtime.CompilerServices.Unsafe | 4.5.2 | `Licenses~/System.Runtime.CompilerServices.Unsafe_LICENSE.txt`, `Licenses~/System.Runtime.CompilerServices.Unsafe_NOTICES.txt` |
| .NET NativeAOT runtime | 10.0.14 | `Licenses~/DotNet_LICENSE.txt`, `Licenses~/DotNetNativeAOT_NOTICES.txt` |

Google.Protobuf's license was obtained from the package's [recorded source commit](https://github.com/protocolbuffers/protobuf/blob/74211c0dfc2777318ab53c2cd2c317a2ef9012de/LICENSE). Other runtime notices were copied from the installed SDK or restored NuGet packages. The original upstream notice lists dependencies of the original mobile SDK; inclusion does not mean every listed mobile library is bundled here.
