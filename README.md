# Polar BLE C# Windows / Unity port

Polar BLE SDK의 공개 프로토콜과 API를 C#으로 이식하는 **초기 개발 버전**입니다. 전체 기능의 실기 동등성을 보장하는 정식 릴리스가 아닙니다. 구현·부분 구현·플랫폼 대체를 [API 대응표](docs/PORTING-STATUS.md)에서 구분합니다.

- 원본: `polarofficial/polar-ble-sdk`, commit `b58e64dcd13d377447d125c16d7cd25ec928bd50` (8.5.0 이후 master)
- 우선 대상: Windows x64, Verity Sense 3.0.16, Unity 6
- Windows 런타임: .NET 10 / Windows 10 2004 이상. 공통 코어: .NET Standard 2.1
- Unity 연결: C#으로 작성한 Windows BLE 어댑터를 NativeAOT 네이티브 DLL로 빌드하고 P/Invoke로 호출

## 구성

| 경로 | 역할 |
|---|---|
| `src/Polar.Core` | BLE 계약, PMD·PSFTP·HR·BAS·HTS·PFCS·MDS 프로토콜, 데이터 모델, 파일·운동·활동·수면·설정·펌웨어 서비스 |
| `src/Polar.Windows` | Windows WinRT BLE 전송, 선택적 OS 페어링 |
| `src/Polar.Native` | C# NativeAOT C ABI, 비동기 명령·이벤트 큐 |
| `unity/com.polarble.sdk` | Unity UPM 패키지, Windows 어댑터, 메인 스레드 전달 및 수명 관리 |
| `samples/Polar.Console` | 검색, 정보 조회, HR·ACC 수신, 기록 목록 예제 |
| `tests/Polar.Tests` | 원본 바이너리 벡터 및 모의 BLE·파일 시스템 회귀 테스트 |
| `docs/api-inventory.csv` | Android·iOS API 선언별 원본 위치와 C# 구현 위치 |

## 빌드

.NET SDK 10과 MSVC x64 빌드 도구 및 Windows SDK가 필요합니다. C++는 네이티브 링크 도구로만 사용하며 구현 소스는 C#입니다. Unity의 IL2CPP 플레이어를 빌드하려면 해당 Editor 버전의 Windows IL2CPP 모듈도 필요합니다.

```powershell
./scripts/build.ps1
# 이미 패키지를 복원한 경우
./scripts/build.ps1 -NoRestore
# Unity Package Manager에서 가져올 .tgz 배포본
./scripts/package-unity.ps1
```

스크립트는 프로젝트의 `.tools/dotnet/dotnet.exe`가 있으면 사용하고, 없으면 PATH의 `dotnet`을 사용합니다. 검증 후 관리 DLL과 **publish 폴더의 네이티브 DLL**을 Unity 패키지에 복사합니다. `src/Polar.Native/bin/.../Polar.Native.dll`은 관리 중간 산출물이므로 직접 배포하지 마세요.

Visual Studio에서는 `Polar.sln`을 엽니다. 패키징 스크립트는 `artifacts/com.polarble.sdk-0.1.0.tgz`와 SHA-256 기록을 만들며 라이선스, API 대응표, 검증 기록도 함께 포함합니다.

## Windows 실행

```powershell
dotnet run --project samples/Polar.Console -- scan 10
dotnet run --project samples/Polar.Console -- inspect <검색에서 반환된-ID>
dotnet run --project samples/Polar.Console -- hr <검색에서 반환된-ID> 10
dotnet run --project samples/Polar.Console -- acc <검색에서 반환된-ID> 10
dotnet run --project samples/Polar.Console -- recordings <검색에서 반환된-ID>
```

프로젝트 전용 SDK만 설치했다면 `dotnet` 대신 `./.tools/dotnet/dotnet.exe`를 사용합니다. BLE 주소와 Polar 일련번호는 서로 다릅니다. `ConnectAsync`에는 검색 결과의 `BleDevice.Id`를 전달합니다.

```csharp
using var client = new PolarClient(new WindowsBleTransport());
client.DeviceDiscovered += (_, device) => Console.WriteLine($"{device.Name}: {device.Id}");
await client.StartScanAsync(token);
// 검색 결과를 선택한 다음
await client.StopScanAsync(token);
using var session = await client.ConnectAsync(deviceId,
    PolarServices.HeartRate | PolarServices.DeviceInformation | PolarServices.Battery,
    cancellationToken: token);
session.HeartRateReceived += (_, sample) => Console.WriteLine(sample.BeatsPerMinute);
await session.StartHeartRateAsync(token);
```

PMD는 `PolarServices.Pmd`, 저장 데이터는 `PolarServices.FileTransfer`로 초기화합니다. `session.Pmd.ReadFeaturesAsync()` 및 설정 조회 결과를 사용해 데이터 타입·설정을 선택하세요. `ReadyServices`는 GATT 서비스 준비 상태이며 모든 고수준 명령의 기기 지원 여부를 의미하지 않습니다. 지원하지 않는 기기 명령은 원래의 프로토콜 오류를 반환합니다.

`session.Files`, `Device`, `StoredData`, `Offline`, `Exercises`, `Rest`, `Logging`, `WatchFace`, `Features`, `Thermometer`, `Telemetry`에서 기능을 이용합니다. Protobuf 원문 모델과 C# 편의 모델을 함께 제공해 생략된 optional 필드와 알 수 없는 값도 구분합니다.

`client.GetBluetoothRadioStateAsync()`와 `BluetoothRadioStateChanged`는 Windows Bluetooth 상태를 조회·관찰합니다. `IsBlePowered`는 아직 알 수 없는 상태를 `null`로 구분합니다. 이 API는 OS 전원 설정을 변경하지 않습니다.

## Unity 사용

1. `scripts/build.ps1`로 패키지 DLL을 생성합니다.
2. Unity Package Manager의 **Add package from disk**에서 `unity/com.polarble.sdk/package.json`을 선택합니다.
3. GameObject에 `PolarUnityClient`를 추가합니다. `Heart Rate` 샘플을 가져오거나 `ScanAsync` / `ConnectAsync`를 호출합니다.
4. Windows x64로 빌드합니다. `PolarUnityClient`의 `DeviceFound`, `HeartRate`, `Error` 이벤트는 메인 스레드로 전달됩니다. 다른 프로토콜 객체의 이벤트는 작업 스레드에서 발생하므로 Unity API 호출은 `Dispatch` 등을 사용하세요.

빌드된 `.tgz` 파일이 있다면 **Add package from tarball**로 설치할 수 있습니다. 전체 공통 코어는 UPM에 포함되어 있으며, 예제의 HR 이외에도 세션 서비스를 직접 호출할 수 있습니다.

NativeAOT DLL은 프로세스 실행 중 언로드하지 않습니다. Play/Stop에서는 연결과 네이티브 컨텍스트를 정리합니다. SDK는 앱당 한 인스턴스를 권장하며, 자동 재연결을 활성화하면 `SessionConnected`에서 새 세션을 받아 스트림을 다시 구독해야 합니다.

## 검증과 범위

- `dotnet run --project tests/Polar.Tests`: 실제 원본 프레임, 압축·암호화, 파일 전송, 취소·오류, 설정·기록·펌웨어 흐름의 모의 테스트
- `src/Polar.Windows/Verification`: 네이티브 경계 검증과 선택적 Windows 실기 테스트
- `validation/UnitySmoke`: 실제 Unity Editor·Standalone 검증용 소스
- 최신 확인 결과: [검증 기록](docs/VERIFICATION.md)

HR 구독 성공과 실제 샘플 수신은 별개의 검증입니다. 기기별·펌웨어별 테스트를 통과하기 전에는 전체 기능 지원을 확정하지 않습니다. Verity Sense의 PPI는 진행 중 운동을 중단할 수 있고, SDK 모드는 HR/PPI 지원을 제한합니다. 측정·모드·기록 상태를 확인한 후 기능을 선택하세요.

Windows의 페어링·MTU·백그라운드 수명 관리 의미는 모바일 SDK와 다릅니다. 페어링 제거 원인은 일반 단절·인증 오류만으로 확정하지 않습니다. 실험적인 저수준 파일 및 telemetry API도 원본과 같이 별도 취급합니다.

## 펌웨어 업데이트

`FirmwareCatalog`는 공개 서버 조회와 ZIP 해석을, `FirmwareUpdater`는 설정 백업·초기화·전송·재연결·복원·버전 확인을 수행합니다. 업데이트는 장치의 저장 데이터를 삭제할 수 있습니다. 측정 기록을 먼저 동기화하고, 요구되는 `persistBackup` 콜백으로 설정 백업을 보존해야 합니다. 테스트에서는 모의 기기만 사용하며 실기 업데이트는 검증하지 않았습니다. 실패 시 자동으로 성공 처리하지 않고 `FirmwareUpdateException`에 복구용 백업과 정리 오류를 보존합니다.

## 원본·라이선스

이 프로젝트는 Polar 공식 Windows SDK가 아닙니다. 수정한 프로토콜 구현과 스키마에는 [Polar SDK 사용자 정의 라이선스](LICENSES/Polar_SDK_License.txt)가 적용됩니다. 원본 저작권·라이선스 고지 및 종속성 고지를 유지해야 합니다. 원본 체크아웃 `upstream/`은 수정하지 않습니다. API 대응표 재생성은 `python scripts/generate_api_inventory.py`, 확인은 `--check` 옵션을 사용합니다.

API 대응표를 재생성하거나 검사하려면 별도로 원본 저장소가 필요합니다. `upstream/`은 Git에서 제외되므로 새 체크아웃에서는 다음과 같이 검토 기준 커밋을 준비합니다. 일반 빌드와 단위 테스트에는 이 단계가 필요하지 않습니다.

```powershell
git clone --filter=blob:none https://github.com/polarofficial/polar-ble-sdk.git upstream
git -C upstream checkout --detach b58e64dcd13d377447d125c16d7cd25ec928bd50
python scripts/generate_api_inventory.py --check
# 대응표를 다시 저장할 때
python scripts/generate_api_inventory.py
```

이미 `upstream/` 체크아웃이 있다면 clone을 생략하고 기준 커밋을 확인하세요. 원본 소스에 로컬 변경이 있으면 해당 변경을 보존한 별도 체크아웃을 사용합니다.
