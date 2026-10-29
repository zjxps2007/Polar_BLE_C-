# Third-party components

Polar Native SDK is an unofficial port of Polar BLE SDK commit b58e64dcd13d377447d125c16d7cd25ec928bd50. See LICENSE.md, NOTICE.md and Licenses~/Polar_SDK_License.txt. The pinned protocol schemas, binary fixtures and capability metadata retain their original provenance.

The native DLL statically links Protobuf 29.3 (full C++ runtime), Abseil 20240722.0, utf8_range from Protobuf 29.3, zlib 1.3.1 and minizip-ng 4.0.10. License texts are included in Licenses~. The standalone Windows GUI also uses Dear ImGui 1.91.9b. C++/WinRT and Windows CNG/WinHTTP are Windows platform dependencies.

There is no managed Google.Protobuf, Unsafe support assembly, NativeAOT bridge or .NET runtime inside this SDK DLL. The Unity package depends on Unity Collections 2.6.3 and the Unity runtime supplied by the installed Editor/Player. The original mobile SDK third-party listing is retained for provenance; it is not a list of libraries bundled in the native DLL.
