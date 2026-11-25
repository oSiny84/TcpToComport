# TcpToComport

Windows PC를 Modbus TCP ↔ Modbus RTU 게이트웨이로 사용하는 데스크톱 프로그램입니다.

```text
Modbus TCP client -> PC IP:502 -> TcpToComport -> COM port -> RS-232 -> MCU
```

Modbus TCP의 Unit ID를 RTU 슬레이브 주소로 사용합니다. 지원 주소는 1~247이며, TCP 요청은 여러 클라이언트에서 받을 수 있지만 단일 RTU 버스에는 한 번에 하나씩 전달됩니다.

## 빌드

.NET 8 SDK 이상이 필요합니다. 외부 NuGet 패키지는 사용하지 않습니다.

```powershell
dotnet build -c Release
```

## 실행

`src\bin\Release\net8.0-windows\TcpToComport.exe`를 실행합니다. 화면에서 COM 포트와 baud/parity/data/stop bit를 선택한 뒤 **게이트웨이 시작**을 누릅니다.

기본 수신 주소는 `0.0.0.0`, 포트는 Modbus TCP 표준 포트 `502`입니다. 화면의 수신 주소와 TCP 포트에서 변경할 수 있습니다.

웹 기반 Modbus Monitor용 로컬 WebSocket 브리지(`ws://localhost:8765`)도 함께 시작됩니다. Monitor 설정은 Bridge `ws://localhost:8765`, Target host `127.0.0.1`, Port `502`로 지정합니다.

실행 파일 배포본은 다음처럼 만들 수 있습니다.

```powershell
dotnet publish src -c Release -r win-x64 --self-contained false
```

## 외부 PC에서 접속

1. 게이트웨이 PC의 사설 IP를 `ipconfig`로 확인합니다.
2. Windows Defender 방화벽에서 사용할 TCP 포트(기본 502)의 인바운드를 허용합니다.
3. 같은 LAN의 Modbus TCP 클라이언트에서 `게이트웨이 PC IP:502`로 접속합니다.
4. 클라이언트의 Unit ID를 MCU의 RTU 슬레이브 주소와 같게 설정합니다.

인터넷에서 직접 연결해야 한다면 포트 502를 그대로 노출하기보다 VPN을 통해 접속하는 구성을 권장합니다. 이 프로그램은 자체 인증이나 암호화를 제공하지 않습니다.

## 응답과 오류 처리

- RTU 응답의 슬레이브 주소, 기능 코드, CRC를 검사합니다.
- RTU 응답이 없거나 잘못된 경우 Modbus TCP 예외 `0x0B`(Gateway Target Device Failed to Respond)를 반환합니다.
- RTU 프레임 끝은 기능별 예상 길이 또는 무응답 간격으로 판별합니다.
- `--response-timeout-ms`로 MCU 응답 제한 시간을 변경할 수 있습니다.
- 특수 장비에서 프레임 간격 조정이 필요하면 `--frame-gap-ms`를 지정할 수 있습니다.
