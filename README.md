# Polar Native SDK — Windows / Unity

C++20 코어와 C ABI v2를 사용하는 **0.2.0-preview**입니다. Windows SDK·GUI에는 .NET 런타임이 필요하지 않습니다. C# 코드는 Unity 바인딩·샘플·검증에만 사용합니다. 이전 C# API와 호환되지 않습니다.

**개발용 미리보기입니다.** 네이티브 재생·모의 프로토콜 시험과 실제 센서 지원은 별개입니다. Verity Sense 3.0.16은 Windows GATT 서비스 검색에서 `Unreachable`이 발생하여 HR·ACC 실기 사용 가능으로 표시하지 않습니다. [구현 상태](docs/PORTING-STATUS.md), [검증 결과](docs/VERIFICATION.md)를 확인하세요.

| 산출물 | 내용 |
|---|---|
| Native SDK ZIP | `polar_sdk.dll`, import library, C 헤더, C++ RAII 헤더, 스키마, 콘솔 도구 |
| Unity UPM | Unity 6000.3용 C# 호출·수명 관리, Persistent NativeArray 배치 수신, DLL, 샘플 |
| Windows 예제 ZIP | Win32·D3D11·Dear ImGui GUI와 DLL; 검색·HR/ACC·그래프·CSV·진단 |

대상은 Windows x64 / Windows 10 2004 이상, Unity 6000.3.24f1 Editor·Mono·IL2CPP입니다. 120 FPS는 앱의 프레임 목표이며 센서 샘플링 속도를 변경하지 않습니다.

## 빌드

VS 2022 Build Tools의 C++ x64 도구, Windows SDK 10.0.19041 이상과 영문 컴파일러 리소스가 필요합니다. CMake 3.31.6·Ninja 1.12.1은 스크립트가 해시를 확인해 준비합니다. 최초 의존성 다운로드에는 네트워크가 필요합니다. `.tools/BuildTools`가 있으면 사용하며, 없으면 `vswhere`로 찾습니다.

```powershell
./scripts/build.ps1
./scripts/build.ps1 -Asan -NoGui -BuildDirectory artifacts/native/asan
./scripts/prepare-native-unity.ps1
./scripts/package.ps1
```

`cmake/Dependencies.cmake`에서 Protobuf 29.3, Abseil 20240722.0, zlib 1.3.1, minizip-ng 4.0.10, Dear ImGui 1.91.9b의 버전·SHA-256을 고정합니다. 암호화는 Windows CNG, HTTPS는 WinHTTP, BLE는 C++/WinRT를 사용합니다. SDK DLL에 정적 C++ 런타임과 프로토콜 의존성을 링크합니다.

## Windows 앱에 연결

배포 SDK를 압축 해제하고 `find_package(PolarNative CONFIG REQUIRED)`와 `Polar::SDK`를 사용하세요. `examples/consumer`는 별도 폴더에서 빌드할 수 있습니다. 실행 EXE 옆에 `polar_sdk.dll`을 둡니다.

```cpp
#include <polar/polar_sdk.hpp>
polar::sdk::context sdk(POLAR_LOSSLESS, 8192);
auto request = sdk.scan();
// polar_sdk_poll_event: 광고 → connect → COMPLETE의 새 device handle
// start(device, POLAR_ACC) → COMPLETE의 stream handle
// polar_sdk_read_samples: 호출자가 마련한 polar_sample[]에 한 번에 읽기
```

비동기 요청의 완료·오류와 센서 배치는 별도 큐입니다. `submit`은 입력을 복사하므로 반환 후 임시 입력 버퍼를 해제할 수 있습니다. [ABI 계약](docs/ABI.md)에 핸들, 명령, 버퍼, 오류, 재연결 규칙을 설명합니다.

```powershell
./artifacts/native/ninja/polar_example.exe
./artifacts/native/ninja/polar_diag.exe scan 0 10
./artifacts/native/ninja/polar_diag.exe pair-state 24ACAC24B9DA
./artifacts/native/ninja/polar_diag.exe hr 24ACAC24B9DA 10
./artifacts/native/ninja/polar_diag.exe acc 24ACAC24B9DA 10
./artifacts/native/ninja/polar_replay.exe 30 4 artifacts/native/replay-4x.json
```

주소는 검색 결과의 12자리 BLE 주소를 사용합니다. GUI의 `Replay ACC`는 센서 없이 동일한 수신 경로와 CSV를 시험합니다. 실제 기기 기능은 지원 설정과 기능 조회 결과에 따릅니다. 삭제·초기화·펌웨어 변경은 명시적으로 해당 명령을 제출할 때만 수행됩니다.

## Unity

1. `prepare-native-unity.ps1`로 현재 DLL을 UPM에 복사합니다.
2. Package Manager의 **Add package from disk**로 `unity/com.polarble.sdk/package.json`을 선택하거나 배포 TGZ를 설치합니다.
3. **Live Sensor** 샘플을 가져와 `PolarSensorView`를 GameObject에 추가합니다.
4. Windows x64를 선택합니다. IL2CPP는 Editor의 Windows IL2CPP 모듈과 C++ 도구가 필요합니다.

`PolarContext`는 EarlyUpdate에서 스트림별 Persistent `NativeArray<Sample>`에 데이터를 읽습니다. `stream.Count` 범위만 같은 프레임에 소비하세요. 다음 프레임에 내용이 바뀝니다. 샘플마다 이벤트·객체·문자열을 생성하지 않습니다. 제어 이벤트는 메인 스레드에서 전달되며 앱의 이벤트 처리 비용도 측정 범위에 들어갈 수 있습니다.

`Dispose`는 작업 종료를 기다릴 수 있으므로 Play 종료·앱 종료에서 수행합니다. 재연결 후 이전 핸들은 무효이며 새 기기 핸들로 스트림을 다시 시작해야 합니다. 오프라인/설정 작업에는 네이티브 Protobuf를 호출하는 `NativeMessage`와 `NativeFormats`를 사용합니다. 이 제어 API는 관리 메모리를 할당하므로 실시간 수신 루프와 분리합니다.

## 출처·라이선스

Polar 공식 SDK commit `b58e64dcd13d377447d125c16d7cd25ec928bd50`의 프로토콜·스키마를 바탕으로 이식했습니다. 원본 라이선스와 출처는 `NOTICE`, `LICENSES`에 유지합니다. 이전 C# 기준 구현은 Git commit `f73aca50dafabf7f3dcfe8bb867083922c9c447a`에 있습니다. 배포 SDK에는 C# fallback과 NativeAOT DLL이 없습니다.

공식 통합 경로: [C++/WinRT](https://learn.microsoft.com/en-us/windows/apps/develop/cpp-winrt/intro-to-using-cpp-with-winrt), [Unity native plugins](https://docs.unity3d.com/6000.0/Documentation/Manual/plug-ins-native.html).
