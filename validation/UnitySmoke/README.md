# Unity 6 실제 검증

Windows x64 Unity 6 Editor, Mono Player, IL2CPP Player에서 같은 C# NativeAOT BLE DLL을 사용합니다. 스크립트는 가짜 BLE 데이터를 생성하지 않습니다. 기본 검증은 실제 스캔 3초 후 중지하며, 센서가 없는 환경에서는 광고 수가 0일 수 있습니다.

```powershell
./scripts/build.ps1
./validation/UnitySmoke/Invoke-UnitySmoke.ps1
```

Unity 6000.3.24f1 기본 설치 경로가 다르면 `-UnityEditorPath`를 지정합니다. IL2CPP에는 해당 Editor 버전의 Windows Build Support (IL2CPP), MSVC C++ 도구, Windows SDK가 필요합니다. 누락된 모듈이나 컴파일 오류는 실패로 반환합니다.

```powershell
# 특정 대상만 검사
./validation/UnitySmoke/Invoke-UnitySmoke.ps1 -Targets Editor,Mono
# 선택한 장치의 연결, GATT 검색, DIS 읽기, 연결 해제까지 검사
./validation/UnitySmoke/Invoke-UnitySmoke.ps1 -Targets Editor -DeviceId '<device-id>'
```

`<device-id>`는 검색에서 반환된 `BleDevice.Id`로 바꿉니다.

장치 지정 모드도 설정을 변경하거나 스트리밍을 시작하지 않습니다. 심박수/ACC 실기 검증은 `src/Polar.Windows/Verification` 프로그램으로 별도 수행합니다. 다른 앱에서 같은 센서를 연결 중이면 먼저 연결을 해제해야 합니다.

검증 프로젝트는 `artifacts/UnityValidation`, 실행별 JSON 결과와 Editor/빌드/Player 로그는 그 안의 `SmokeResults/<UTC>/`에 저장됩니다. `success`는 그 실행이 검사한 항목만 의미합니다. 기본 스캔 성공은 센서 연결, PMD, 모든 SDK 기능의 성공을 의미하지 않습니다.

NativeAOT DLL은 프로세스가 끝날 때까지 로드된 상태로 유지합니다. DLL 교체 전에는 검증 Editor/Player를 종료해야 하며, Play 종료 시 연결과 네이티브 세션은 Dispose로 정리합니다.
