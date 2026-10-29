# Windows C# / Unity 포팅 상태

현재 결과는 **공개 SDK 기능의 Windows 포팅 구현과 검증 기반**이다. 모든 메서드가 원본과 같은 반환 모델·동작을 제공하거나 모든 기기에서 검증되었다는 의미는 아니다. 구현 코드, API 의미의 차이, 실기 검증 여부를 분리해서 관리한다.

기준 소스는 Polar BLE SDK [`b58e64dcd13d377447d125c16d7cd25ec928bd50`](https://github.com/polarofficial/polar-ble-sdk/tree/b58e64dcd13d377447d125c16d7cd25ec928bd50)이다. Android 또는 iOS 한쪽만 있는 기능도 조사 대상이다. 사용자 검증 대상은 **Verity Sense 3.0.16 / Unity 6 / Windows x64**이다.

## 메서드별 근거

- [api-inventory.csv](api-inventory.csv): 플랫폼·원본 API·메서드/속성·시그니처·원본 소스 줄·상태·실제 C# 심볼과 줄·남은 차이.
- [api-mapping.json](api-mapping.json): 같은 내용을 구조화한 결과. 원본 링크는 브랜치가 아닌 고정 커밋에 연결된다.
- [generate_api_inventory.py](https://github.com/zjxps2007/Polar_BLE_C-/blob/main/scripts/generate_api_inventory.py): 표 생성 및 실제 구현 심볼 검증. Python 표준 라이브러리만 사용한다.

```powershell
python scripts/generate_api_inventory.py
python scripts/generate_api_inventory.py --check
```

추출 범위는 두 플랫폼 `sdk/api`의 기능 계약·콜백·팩터리 메서드, iOS `PolarBleApi` 속성, 그리고 `sdk/impl/PolarBleApiImpl+RestService.swift`에 별도로 선언된 공개 REST 프로토콜이다. 오버로드와 기본 구현 선언도 각 소스 위치를 보존한다. 데이터 모델의 생성자, enum 변환, `equals`/`hashCode` 같은 보조 함수는 기능 API 집계에서 제외한다. 따라서 행 수는 서로 다른 기능 수가 아니다.

현재 스냅샷은 Android 209개, iOS 185개 선언으로 총 **394행 / 고유 멤버명 205개**이다. 새로운 API가 들어오면 미검토 상태로 자동 성공 처리하지 않고 생성기가 실패한다. 구현 코드가 이동하면 다시 생성해 줄 번호를 갱신한다.

| 상태 | 선언 수 | 뜻 |
|---|---:|---|
| `implemented-hardware-unverified` | 332 | 실제 프로토콜/기능 구현이 존재한다. 이 표만으로 해당 기능의 실기 성공을 주장하지 않는다. |
| `partial` | 37 | 동작 일부, 편의 처리 또는 이벤트 의미에 구체적인 차이가 남아 있다. |
| `planned` | 0 | 구현 자체가 없는 기능으로 남겨둔 선언은 없다. `partial`과 플랫폼 차이는 별도로 유지한다. |
| `platform-replacement` | 25 | Windows 방식으로 대체하거나 모바일 OS 전용 동작이 적용되지 않는다. 원본 동작과 동등하다는 뜻이 아니다. |

완료율 백분율은 계산하지 않는다. 콜백 중복과 오버로드 수가 비중을 왜곡하고, `partial` 안에서도 남은 작업의 크기가 다르기 때문이다.

## 구현된 경로와 남은 차이

| 영역 | 실제 구현 | 남은 차이 / 검증 경계 |
|---|---|---|
| BLE 기본 기능 | Windows 검색·연결·GATT·알림·pairing, 자동 재연결·새 세션 이벤트, 취소·오류 이벤트, Bluetooth radio 상태 조회·관찰 | 일부 연결 콜백/기능별 readiness와 모바일 DTO 의미는 다름. 전원 관찰은 읽기 전용이며 OS 전원 변경 명령이 아님 |
| HR / PMD 스트리밍 | HR/RR 및 ECG·ACC·PPG·PPI·GYRO·MAG·온도·압력·위치·Derived 디코더, 설정·시작·정지·SDK 모드 | PMD 출력은 이름 있는 채널과 메타데이터를 갖는 `PmdDataBatch`. 센서별 모바일 DTO 클래스와 API 모양은 다름. 해당 기기/펌웨어에서 가능한 형식은 런타임 조회 필요 |
| HR 광고 | `HrBroadcastReceived`, Polar 제조사 0x006B·GPB envelope 처리·frame counter 중복 제거 | 식별자 선택은 이벤트의 Device 정보로 필터링. 연결 없이 스캔 중 동작 |
| PSFTP | RFC60/76 프레이밍, 요청·쓰기·쿼리·D2H/H2D, 파일 목록·폴더·삭제, 취소·disconnect 처리 | 실제 기기의 보안·모드·최대 전송량·오류 조건 검증 필요 |
| 오프라인 PMD | 설정·트리거·시작·정지, PMDFILES.TXT/폴더 목록, 분할 결합·암호화·진행률·삭제 | 삭제 시 같은 측정 타입의 현재 분할 파일을 재조회하며 다른 타입은 보존. Boolean 삭제 래퍼도 제공하되 .NET 취소는 예외로 전달 |
| H10 / Offline Exercise V2 | 녹화·상태·start result·exercise entry·HR/RR 샘플 DTO와 다운로드·삭제 | 원본 두 플랫폼처럼 지정한 entry.path만 삭제. H10에 기록 시간이 없으면 호스트 현재 시간을 만들어 넣지 않고 null 유지 |
| 기기 설정 / FTU | 시간·시간대·디스크·LED·리셋·절전·USB·OHR·자동운동감지·sync 및 typed physical/user settings | FTU 범위 검증과 UTC last-modified, 기본 master ID 및 USERID timestamp 생성. 설정의 미지 필드와 선택 값 보존 |
| Activity / Sleep / SpO2 | 저장 파일 및 typed activity/day/HR/PPI/nightly/temperature/SpO2/sleep DTO, 수면 REST 명령 | `TimeSpan`, 현지 시각과 UTC offset, 선택 필드 및 unknown enum을 보존. 기존 protobuf API와 Source를 함께 유지 |
| Training session | 날짜 필터 없는 전체 탐색 또는 범위 탐색, reference/exercise/session DTO, gzip·진행률·exercise 제어 | 원본처럼 protobuf summary/samples/route를 typed 계층에 배치. 마지막 세션 삭제는 E/를 정리하며 다른 일별 데이터 보존. stale reference는 다른 세션을 삭제하지 않음 |
| REST / D2H | 서비스·설명 DTO, GET/PUT, gzip JSON, typed 수면 상태 배열, 숫자 D2H ID/파라미터 전달 | 임의 타입 이벤트는 명시적 변환 delegate와 IDisposable 구독 사용. NativeAOT/IL2CPP에서 런타임 JSON reflection 불필요 |
| Logging / Telemetry | 모든 LogConfig 선택 필드·외부 HR 센서 설정, 로그 export·삭제, Memfault BLE export | unknown protobuf 필드를 읽기/수정/쓰기에 보존. 외부 업로드는 별도 작업 |
| FWU / WatchFace | 패키지·카탈로그·백업·복원·재접속 흐름, watchface codec/query/set | 실제 기기에 FWU 또는 watchface 변경을 수행해 검증한 것으로 표시하지 않음 |
| 배터리 | BAS 잔량, 0x2BED charging/battery-present/wired·wireless power 상태 읽기·이벤트 | 원본 SDK가 해석하지 않는 optional BAS 필드는 RawData에 보존 |

통합 `build.ps1` 검증은 **8 suites / 491 assertions**를 통과했다. 회귀 검증에는 PSFTP/PMD 전송, unknown protobuf 유지, 선택 값 false/0과 누락 구별, 시간대 날짜 경계, 실제 gzip training 데이터 경로, 분할 기록 삭제 시 형제 보존, 마지막 세션 정리 및 stale reference 거부가 포함된다. fake transport 테스트는 무선 환경, pairing, 펌웨어별 응답이나 장시간 연결 품질의 실기 검증을 대체하지 않는다. 이후 빌드/실기 결과는 저장소 README 및 검증 로그를 기준으로 확인한다.

## Verity Sense 3.0.16에 적용할 범위

원본 [Verity Sense 기기 문서](https://github.com/polarofficial/polar-ble-sdk/blob/b58e64dcd13d377447d125c16d7cd25ec928bd50/documentation/products/PolarVeritySense.md)는 HR·PPG·PPI·ACC·GYRO·MAG의 스트리밍/오프라인 기록과 시간·저장 공간·재시작·리셋·FWU·SDK 모드를 설명한다. 포팅 저장소에 ECG, SpO2, 수면, watchface API 코드가 있다고 해서 Verity Sense 하드웨어에 그 기능이 생기는 것은 아니다.

- 지원 데이터와 설정은 연결 후 PMD 기능/설정을 조회한다. `ReadyServices`는 GATT 준비 상태이며 모든 SDK 기능의 기기별 지원 여부를 보증하지 않는다.
- Verity Sense 내부 기록/수영 모드의 파일 전송은 원본 문서상 `SYSTEM_BUSY` 대상이다. 파일 목록/다운로드 검증은 센서 모드에서 수행한다.
- SDK 모드에서는 HR/PPI가 지원되지 않는다. PPI 최초 데이터 지연, HR 갱신 간격 및 내부 운동 중단 조건은 원본 기기 문서에 따른다.
- `polar_device_capabilities.json`의 `sense`는 `activityDataSupported: false`이다. Activity/Sleep 계층의 검증은 해당 기능을 지원하는 다른 기기가 필요하다.

## Windows / Unity 경계

공유 Core는 C# `netstandard2.1`이며 Windows BLE는 운영체제 GATT API를 사용한다. Unity 6 Windows x64는 NativeAOT 브리지와 Unity 어댑터를 통해 호출한다. Core API 구현과 Unity 외부 노출 범위는 별도이므로, 위 표를 Unity에서 모든 고수준 메서드가 바로 호출된다는 의미로 해석하지 않는다. NativeAOT 빌드 성공, Unity Editor import, Windows player 실행 및 실기 수신은 각각 별도로 확인해야 한다.

Android CompanionDeviceManager의 association·presence service·background execution privilege와 iOS CoreBluetooth state restoration은 Windows에 같은 계약이 없다. 현재 Windows pairing/scan/세션 수명 관리로 대체하며, association 목록/삭제와 companion disappearance/background privilege의 동등 구현은 제공하지 않는다.

Windows GATT의 인증 실패나 일반 연결 해제만으로 **원격 장치에서 pairing이 삭제되었다**고 확정할 수 없다. `checkIfDeviceDisconnectedDueRemovedPairing`은 이러한 OS 정보 차이를 표시하는 `platform-replacement`이며, 판별 기능의 구현 예정으로 표시하지 않는다. Bluetooth radio 조회·`Radio.StateChanged` 관찰은 `BluetoothRadioState`/`IsBlePowered`/`BluetoothRadioStateChanged`에 연결했고 NativeAOT·Unity 경로에도 노출한다. 원본 `blePowerOn`/`blePowerOff`는 상태 콜백이므로 이를 OS 전원 변경 명령으로 해석하지 않는다.

같은 기기에 연결하면서 서비스 종류를 추가하면 그 합집합이 이후 자동 재연결에도 유지된다. 명시적 disconnect는 예약 또는 진행 중인 연결 요청을 무효화하고, 취소 후 늦게 반환된 OS 연결도 폐기한다. 기존 요청의 `h10FileSystem` 변경은 오류로 알리므로 먼저 disconnect한 뒤 새 요청을 시작해야 한다.

현재 남은 `partial`은 GATT 서비스 준비 상태와 원본 SDK의 세부 기능별 readiness 차이, 연결 시작/해제 원인 콜백의 일부 의미, 연결 ID를 통한 이름/RSSI 캐시 조회, 기존 SDK를 유지하는 cleanup, 일반 진단 logger, PMD+HR 가용 타입 목록의 단일 편의 메서드다. 주요 도메인 명령과 저장 데이터 접근·변환을 위한 실제 코드가 있어도 이 호환성 차이와 실기 미검증을 감추지 않는다. 다음 우선순위는 실제 Verity Sense HR/PMD/PSFTP 수신 검증과 위 호환성 편의 계층의 보완이다.
