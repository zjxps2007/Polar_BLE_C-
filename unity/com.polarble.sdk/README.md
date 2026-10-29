# Polar BLE C# Windows Port — Unity 6

Windows x64용 비공식 초기 개발 패키지입니다. 공통 C# 프로토콜 코어와 C# NativeAOT Windows BLE DLL을 포함합니다. Unity Editor, Windows Mono 및 IL2CPP에서 사용할 수 있습니다. 전체 SDK 기능의 실기 동등성은 아직 검증되지 않았습니다.

## 설치

Unity Package Manager에서 **Add package from disk**로 이 폴더의 `package.json`을 선택합니다. `.tgz` 배포본은 **Add package from tarball**로 선택합니다. Windows x64를 빌드 대상으로 사용하세요. Linux/macOS/Windows 32비트는 이 전송 DLL의 대상이 아닙니다.

1. GameObject에 `PolarBle.Unity.PolarUnityClient`를 추가합니다.
2. Package Manager에서 **Heart Rate** 샘플을 가져옵니다.
3. 다른 GameObject에 `HeartRateExample`을 붙이고 `client` 필드에 위 컴포넌트를 연결합니다.
4. `deviceId`를 비워 두고 실행하면 Console에 검색 결과가 나옵니다.
5. Play를 종료하고 출력된 `ble:...` ID를 입력한 뒤 다시 실행하면 연결과 HR 구독을 시작합니다.

BLE 주소에서 만든 transport ID와 Polar 제품 일련번호는 다릅니다. 검색된 `BleDevice.Id`를 사용하세요. `Heart Rate` 예제는 심박수 구독만 수행하며 센서의 측정 모드를 바꾸지 않습니다.

## 전체 API 사용

`PolarUnityClient.Client`는 공통 `PolarClient`이며, `ConnectAsync`가 반환한 `PolarDeviceSession`에서 PMD, 파일, 기록, 설정 등 서비스를 사용할 수 있습니다. 파일 서비스처럼 추가 초기화가 필요한 기능은 `session.InitializeAsync(PolarServices.FileTransfer)`로 준비합니다. 반환된 기기 기능과 설정을 조회한 뒤 명령을 선택하세요.

`PolarUnityClient`의 `DeviceFound`, `HeartRate`, `Error` 이벤트는 Unity 메인 스레드에서 전달됩니다. 코어 서비스의 이벤트는 작업 스레드에서 발생하므로 `client.Dispatch(() => ...)`로 Unity 오브젝트를 조작하세요. 앱당 클라이언트 한 개를 권장합니다.

NativeAOT DLL은 프로세스 수명 동안 로드됩니다. Play/Stop에서는 연결과 컨텍스트를 정리하지만 DLL 자체를 언로드하지 않습니다. DLL을 교체할 때는 Editor와 Player를 종료하세요.

## 검증 범위

Unity 6000.3.24f1 Editor·Windows Mono·Windows IL2CPP에서 실제 DLL 로드 및 BLE 검색을 검증했습니다. 검색 성공은 HR/PMD 샘플 수신 성공을 의미하지 않습니다. Verity Sense 3.0.16의 스트리밍 검증은 Windows GATT 연결 문제로 미완료 상태입니다. 상세한 기능 차이와 검증 결과는 배포본의 `Documentation~`을 참고하세요.

펌웨어 업데이트, 파일 삭제, 초기화 및 기록 시작은 기기 상태나 저장 데이터를 바꾸는 API입니다. 예제는 이러한 명령을 자동 실행하지 않습니다. 펌웨어 업데이트 경로는 모의 테스트만 통과했으며 실기 검증은 아직 수행하지 않았습니다.

## 라이선스

원본은 Polar BLE SDK commit `b58e64dcd13d377447d125c16d7cd25ec928bd50`입니다. Polar 공식 Windows 제품이 아닙니다. [Polar SDK 라이선스](LICENSE.md), [원본 고지](NOTICE.md), [종속성 고지](Third%20Party%20Notices.md)를 함께 보존하세요.
