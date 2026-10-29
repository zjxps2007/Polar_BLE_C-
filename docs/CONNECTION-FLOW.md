# 연결·초기화·인증 — Windows / Unity

대상: Windows x64, Unity 6000.3.24f1, Polar Verity Sense (사용자 제공 펌웨어 3.0.16). SDK 버전은 0.2.0-preview / ABI v2이다. 이 문서는 연결 경로 변경과 제한된 실기 시험을 다루며 전체 API 호환성·장시간 안정성 인증이 아니다.

## 공통 네이티브 흐름

`검색 → 연결 → GATT 검색 → 링크 협상 대기 → 요청 기능 초기화 → Ready → 스트림 시작` 순서다. 보호된 ATT 작업이 0x05/0x0F를 반환하면 같은 GATT 세션에서 인증을 기다린 뒤 그 작업을 다시 시도한다. Windows 예제, C++ 진단기, Unity Editor/Mono/IL2CPP는 모두 `native/windows_ble.cpp`의 이 경로를 사용한다. C#에는 BLE 연결·페어링 구현이 없다.

- CONNECT의 기본 기능 마스크는 HR/DIS/배터리인 7이다. ACC까지 준비하려면 15, HR만은 1, PMD만은 8을 지정한다. 0은 전송 연결만 요청한다.
- HR 준비는 알림 구독까지 포함한다. PMD 준비는 제어·데이터 알림 구독과 기능 읽기를 포함한다. DIS/BAS 조회에서 이미 찾은 특성 객체를 재사용한다.
- CONNECT 성공은 요청된 초기화 이후다. 지원하지 않는 기능은 unavailable, 초기화 오류는 failed로 구분한다. `CONNECTION_STATE`의 임시 핸들은 CONNECT 성공 전까지 앱에서 사용하지 않는다.
- Windows가 MTU/PHY를 관리한다. 협상 이벤트가 조용해진 뒤 1.2초 기다리며 총 대기는 최대 6초다. Windows 11에서 지원하면 Balanced 연결 파라미터를 요청하고 요청 객체를 세션 동안 유지한다. Windows 10에서는 선택 API를 호출하지 않는다.
- 인증 복구 단계는 최대 2회다. 먼저 `EncryptionRequired`로 같은 ATT 작업에 Windows 보안 협상을 요청한다. 읽기·CCCD 구독은 OS 암호화 완료를 최대 12초 기다리며 500ms 간격으로 재시도한다(명령 timeout이 더 짧으면 그 값으로 제한). 이후에도 보안 오류가 반환되면 갱신된 DeviceInformation으로 명시적 페어링을 기다린다. 일반 쓰기는 결과가 불확실한 Unreachable 오류에서 반복하지 않는다. 사용자 거부·취소를 성공으로 처리하지 않는다.
- 인증 실패는 `AUTHENTICATION_FAILED(-13)`, 원래 ATT 오류·단계·Windows pairing status와 함께 반환한다. 이 세션의 자동 재연결은 중단한다. 일반 재연결은 2/4/8/16초 backoff를 사용하고 초기화 옵션을 보존한다.
- 취소·연결 종료는 작업 대기를 깨운다. 이전 세션의 콜백과 늦은 인증 완료로 새 스트림을 만들지 않는다.

일반 연결에는 Windows 설정의 사전 페어링이 필요하지 않다. 다만 Windows의 보안 협상 과정에서 OS 페어링 정보가 생성될 수 있다. Windows 설정의 “연결됨/페어링됨” 표시만으로 SDK 준비나 지속 수신을 판단하지 않는다.

## 명시적인 키 복구

`UNPAIR(17)` / C++ `context::unpair(address)` / Unity `PolarContext.Unpair(address)` / GUI `Reset Windows pairing`은 선택한 센서의 Windows 등록을 해제하는 명시적 작업이다. CONNECT가 자동으로 호출하지 않는다. 기존 키 오류가 확인됐을 때 실행한 뒤 다시 연결한다. 초기화·기록 삭제·펌웨어 변경 명령과 별개이다.

공개 WinRT 연결 종료 이벤트는 HCI의 구체적인 키 오류를 주지 않는다. SDK는 일반 disconnect를 키 오류로 추정하지 않는다. 실기 진단에서 ETW/HCI로 확인한 코드와 SDK가 실제 반환한 값을 구분해 기록한다.

## 실행 방법

```powershell
./scripts/build.ps1
./scripts/prepare-native-unity.ps1
./artifacts/native/ninja/polar_diag.exe scan 0 10
./artifacts/native/ninja/polar_diag.exe hr 24ACAC24B9DA 60
./artifacts/native/ninja/polar_diag.exe hr-acc 24ACAC24B9DA 60
# 마지막 인자는 진단용 초기화 마스크. HR만 요청하는 예:
./artifacts/native/ninja/polar_diag.exe hr 24ACAC24B9DA 60 1
```

GUI에서는 검색된 센서를 선택하고 `Ready` 뒤 HR/ACC를 시작한다. Unity Live Sensor 샘플은 `Replay=false`와 실제 주소를 지정한다. `PolarContext.TryGetConnectionState`로 상태를 표시하며, 성공 완료 이벤트의 기기 핸들로 스트림을 시작한다.

검증 프로젝트의 `PolarLiveRunner`는 `--polar-live ADDRESS --polar-measurement hr|acc|hr-acc --polar-seconds 60`으로 실제 BLE를 사용한다. `--polar-features 1` 등으로 초기화 범위를 분리할 수 있다. 결과 JSON에 backend·DLL 해시·준비 순서·샘플 수·수신 간격·오류를 저장한다. 지속 수신 판정에는 전체 관찰 시간, 각 스트림 3건 이상, 마지막 수신 후 5초 이내, 최대 수신 간격 5초 이내가 필요하다. 1건 수신은 합격으로 처리하지 않는다.

## 공식 코드와 Windows 대응

기준은 [Polar SDK b58e64d](https://github.com/polarofficial/polar-ble-sdk/tree/b58e64dcd13d377447d125c16d7cd25ec928bd50)이다.

| 원본 경로 | 적용한 동작 |
|---|---|
| Android `BDBleApiImpl.kt`, `BDDeviceListenerImpl.kt` | 앱 검색·세션 열기. OS 설정 사전 페어링을 전제로 하지 않음 |
| Android `ConnectionHandler.kt`, `GattCallback.kt` | 서비스·협상 뒤 ATT 준비, 지연과 이벤트에 따른 초기화 |
| Android `BDDeviceSessionImpl.kt` | ATT 인증 오류에서 인증 완료를 기다리고 원래 작업을 재시도, 재시도 상한 |
| `BleHrClient.kt`, `BlePMDClient.kt` | HR 알림, PMD 제어·데이터 알림과 기능 준비 |
| iOS `CBDeviceSessionImpl.swift` | 링크를 유지하며 OS 보안 협상과 알림/읽기 재시도를 처리 |

Windows 구현은 Android createBond나 HCI 명령을 그대로 실행하지 않는다. [Microsoft GATT client](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client), [GattProtectionLevel](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattprotectionlevel), [페어링 API](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/pair-devices)에 맞춰 구현한다. 상태 객체 갱신 필요성은 [Bleak의 WinRT 구현](https://github.com/hbldh/bleak/blob/develop/bleak/backends/winrt/client.py)의 pairing 경로에서도 교차 확인했다. 공개 SDK와 비공개 Polar Flow 내부 구현이 동일하다고 단정하지 않는다.

실기 결과는 [VERIFICATION.md](VERIFICATION.md)의 연결 흐름 시험 항목에 기록한다. 30분 성능 시험·4시간 수집·VR 렌더링 검증과는 별개이다.
