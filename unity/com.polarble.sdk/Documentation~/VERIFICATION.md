# 검증 기록

검증일: 2026-10-29 KST. 대상: Windows x64, Polar Verity Sense 소프트웨어 3.0.16, Unity 6000.3.24f1. 원본 SDK 기준은 `b58e64dcd13d377447d125c16d7cd25ec928bd50`이다.

## 구현과 검증의 구분

이 포트는 공통 C# 프로토콜/서비스, Windows WinRT BLE, NativeAOT DLL, Unity UPM 패키지를 제공한다. API 선언 대응표는 394행이며 구현 332행, 부분 37행, 플랫폼 대체 25행이다. 오버로드와 두 플랫폼의 중복을 포함하므로 이 수를 서로 다른 기능 수나 실기 성공 수로 해석하지 않는다. 부분 구현의 구체적인 의미는 [포팅 상태](PORTING-STATUS.md)와 [API 목록](api-inventory.csv)에 기록했다.

빌드·단위 테스트, 실제 Unity 실행, 실제 기기에서의 데이터 수신을 서로 구분한다. 프로토콜 테스트는 원본 바이너리와 모의 전송으로 수행하며 무선 환경이나 기기 펌웨어를 대체하지 않는다.

## 최종 빌드 및 실제 Unity 실행

| 검증 | 결과 |
|---|---|
| Core 회귀 테스트 | 8 suites / 491 assertions 통과 |
| Native 경계·이벤트·취소 테스트 | 49 assertions 통과 |
| Windows / Console / .NET Standard Unity 어댑터 | 빌드 경고 0, 오류 0 |
| NativeAOT Windows x64 | 네이티브 DLL 게시 성공 |
| Unity 6000.3.24f1 Editor | DLL 로드, radio `On`, 3초 스캔/정지 성공. 광고 45건, Polar 1대, 오류 없음 |
| Unity Windows Mono 플레이어 | 빌드 경고 0·오류 0, 실제 실행 성공. radio `On`, 광고 46건, Polar 1대, 오류 없음 |
| Unity Windows IL2CPP 플레이어 | 빌드 경고 0·오류 0, 실제 실행 성공. radio `On`, 광고 52건, Polar 1대, 오류 없음 |

이 실행들의 센서 연결·스트리밍 옵션은 사용하지 않았다. 최종 증거 폴더는 `artifacts/UnityValidation/SmokeResults/20261028T221436490Z/`이다. JSON의 `connected: false`는 이 검사 범위에 연결이 포함되지 않았음을 나타낸다. 플랫폼별 빌드 결과와 프로세스 실행 결과를 모두 보존했다.

검증 입력의 SHA-256:

| 파일 | SHA-256 |
|---|---|
| `Polar.Core.dll` | `D806809C3D752A5BBCA729AB6B50EA4F0FAA36B3B149B2805447DDF6D5A2F4DB` |
| `Polar.Native.dll` | `2CE591D27075686169ADB2C7C27E5DEAAAFAEAF9519F72ADC50E526B137B378C` |

## 재현

```powershell
./scripts/build.ps1
./validation/UnitySmoke/Invoke-UnitySmoke.ps1
python scripts/generate_api_inventory.py --check
./scripts/package-unity.ps1
```

최종 통합 빌드 로그는 `artifacts/build/build-final.log`, 배포 DLL의 해시는 `artifacts/build/package-manifest.json`에 보존한다. Unity 실행별 증거는 `artifacts/UnityValidation/SmokeResults/<UTC>/`에 JSON·Editor·플레이어·빌드 로그로 저장한다. `validation/UnitySmoke`의 소스로 같은 경로를 다시 실행할 수 있다.

## 실제 Verity Sense 결과

| 항목 | 확인 결과 |
|---|---|
| 기기 검색 | Polar Verity Sense 광고 확인. 공개 문서에서는 기기 일련번호와 BLE 주소를 생략 |
| 초기 연결 세션 | GATT 8개 서비스, ATT MTU 232 / 값 payload 229바이트, DIS 조회 확인 |
| 버전 | DIS firmware revision `0.1.6`, software revision `3.0.16`을 구분해 확인 |
| 초기 HR | 알림 구독은 성공했으나 관찰 기간 중 샘플 0건. 수신 성공으로 간주하지 않음 |
| 초기 PMD | CCCD 쓰기에 ATT 0x05 인증 부족. 암호화 요구 재시도도 실패 |
| Windows 수동 페어링 후 | AEP `IsPaired=True`, `ProtectionLevel=Encryption` 확인 |
| 기기 전원 재시작·다른 앱 연결 종료 후 | 순수 WinRT `FromId`에서도 Uncached/Cached/HR 지정/PMD 지정 GATT 검색 모두 `Unreachable`, 서비스 0개 |
| HR / ACC 스트리밍 | 서비스 검색 단계에서 막혀 샘플 수신 미검증 |

최종 페어링 상태 및 직접 WinRT 비교 증거는 `artifacts/UnityValidation/SmokeResults/Windows-PairedAep-Protection.log`, 재시작 후 SDK 재현은 `artifacts/logs/verity-hr-after-powercycle.log`이다. OS의 `ConnectionStatus=Connected`와 실제 GATT 서비스 요청의 성공은 동일하지 않았다. 암호화 없는 페어링이 원인이라는 가설은 현재 상태값과 맞지 않는다. 이 결과만으로 센서 펌웨어, 어댑터 드라이버, Windows Bluetooth 스택 중 어느 하나를 원인으로 확정하지 않는다.

Qualcomm FastConnect 7800 어댑터와 Polar의 PnP 상태는 `OK`, `bthserv`는 실행 중이었다. 사용자 Bluetooth 지원 서비스는 중지 상태였으며 시작 시도는 OS 권한 부족으로 실패했다. 서비스 상태가 GATT 오류의 원인인지는 확인되지 않았다. 기존 페어링과 센서 저장 데이터는 유지했다.

## 실기 미검증 범위

PMD 각 센서 데이터, PSFTP 다운로드, 오프라인 기록, FTU·설정 쓰기, 저장 데이터 DTO, 펌웨어 업데이트, watchface 변경은 해당 지원 기기의 실기 검증이 남아 있다. Verity Sense가 지원하지 않는 수면·활동·SpO2·watchface 등의 기능은 다른 지원 기기가 필요하다. 펌웨어 업데이트·초기화·기록 삭제·PPI 측정·SDK 모드 변경을 이번 실기 검증에서 실행하지 않았다.

자동 재연결의 취소·명시적 단절·늦은 연결 완료·서비스 확장 및 네이티브 이벤트 순서는 모의 전송으로 검증한다. 장시간 무선 연결, Bluetooth 어댑터 교체, OS 절전/복귀 등은 별도 검증 항목이다.
