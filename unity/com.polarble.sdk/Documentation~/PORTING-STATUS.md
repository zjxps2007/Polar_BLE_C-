# 네이티브 이식 상태 — ABI v2

2026-10-30, 0.2.0-preview. 이 문서는 **구현 연결 상태**입니다. 시험 통과·기기 지원 인증을 의미하지 않습니다. 사용자 요청에 따라 남은 구현을 먼저 진행하고 최종 검증은 그 뒤 단계로 분리했습니다.

## API 연결

`upstream-api.csv`는 원본 commit b58e64d의 Android/iOS **394개 선언**, **231개 고유 (API, member)** 스냅샷입니다. `generate_api_inventory.py`는 새 공개 C 헤더의 실제 심볼로 대응표를 생성합니다.

| 상태 | 선언 수 | 의미 |
|---|---:|---|
| native | 372 | 기능에 대응하는 네이티브 명령·타입·배치/이벤트 경로가 구현됨 |
| platform-difference | 22 | 모바일 CompanionDeviceManager·foreground·MTU 강제 지정·페어링 삭제 원인 판정 등의 Windows 차이 |
| unmapped | 0 | 대응 심볼 없는 선언 없음 |

이 숫자는 독립 기능 수나 이전 491+49 검증의 동등성 통과 수가 아닙니다. 콜백은 새 ABI의 pull event/batch로 대체하며 기존 C# API 호환 계층을 제공하지 않습니다. Windows가 제공하지 않는 연결 실패 원인은 추정해 채우지 않습니다.

## 구현 범위

| 영역 | 네이티브 구현 |
|---|---|
| 검색·연결 | Polar 필터, 주소 필터, RSSI 자동 연결, Windows 페어링 상태·페어링, GATT·MTU, 취소, 새 핸들을 발급하는 재연결 |
| 초기화·관찰 | 특징별 ready/unavailable/failed, DIS 원본 bytes, HR/HTS 가용 목록, 라디오 상태 이벤트 |
| 데이터 | HR·HTS·BAS·Polar HR 광고, ECG/ACC/PPG/PPI/GYRO/MAG/온도/위치/기압/derived PMD, 압축·정확한 타임스탬프 |
| PMD 제어 | 지원 설정, SDK 모드, 오프라인 시작/중지/상태, 암호키, 트리거, derived 그룹 |
| 저장 데이터 | PSFTP get/put/remove/list/query, 진행 이벤트, REC 복호화·분할 결합·그룹/개수, 운동·활동·수면·피부온도·SpO2·자동 HR/PPI |
| 설정 | Protobuf 병합, 시간, FTU, 신체 정보, LED·USB·OHR·자동운동·텔레메트리·위치, PFCS, 명시적 초기화/재시작 |
| 진단·부가 기능 | 로그/텔레메트리, REST·수면 이벤트, watchface KVTX/FlatBuffer, 펌웨어 카탈로그·ZIP·백업·전송·복구 흐름 |
| Windows 예제 | 공개 SDK만 사용하는 Win32/D3D11/ImGui, 그래프, 백그라운드 CSV 기록, 오류 단계 표시 |
| Unity | UPM, Cdecl 바인딩, Persistent NativeArray, EarlyUpdate 수신, Play 종료 정리, 그래프/가속도 샘플 |

세부 입력·출력은 [ABI](ABI.md), `polar_services.h`, `native_sdk.proto`, `api-inventory.csv`를 참조하세요. 데이터 타입을 구현했다는 사실과 특정 Polar 모델이 해당 측정/파일/서비스를 지원한다는 사실은 별개입니다. 기본 기기 프로필은 원본 capability JSON을 출처와 함께 포함하며 실제 GATT 준비 결과를 별도로 기록합니다.

## 검증 단계에 남은 일

- 최종 소스와 동일한 DLL로 전체 회귀·ASan·변이 입력·Unity Editor/Mono/IL2CPP 재검증.
- 원래 491개 코어·49개 경계 검증이 다룬 동작별 대응 확인. 현재 원본 PMD 72개 golden frame과 추가 네이티브 모의 테스트만으로 전체 동등성을 주장하지 않음.
- 1x/4x 재생의 CPU·할당·복사·큐·분위수 측정, 20회 Play/Stop, 실제 Player 30분·4시간 시험.
- 실제 Windows GUI 클릭/화면·CSV 저장 검증. Computer Use 앱 접근이 승인되지 않아 실행하지 못한 검증은 통과로 간주하지 않음.
- Verity Sense 3.0.16 HR·ACC 실기 수신. 이전 C++ 진단에서는 GATT Unreachable, 수신 0건. 재생 결과로 실기 지원을 대체하지 않음.
- 펌웨어 복구·초기화·삭제는 우선 모의 기기로 검증. 실제 센서에 파괴적 명령을 실행하지 않음.

5초 WinHTTP I/O 대기 중의 취소는 즉시 완료되지 않을 수 있습니다. 펌웨어 복구 백업은 덮어쓰지 않는 새 파일에 먼저 영속화합니다. 복구 실패 시 백업을 유지하고 오류를 반환하며, 복구를 성공으로 꾸미지 않습니다.
