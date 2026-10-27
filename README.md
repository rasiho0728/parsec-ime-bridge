# Parsec IME Bridge

Parsec 클라이언트에서 한/영 키(오른쪽 Alt)와 한자 키(오른쪽 Ctrl)가 호스트로 전달되지 않는 문제를 해결하는 트레이 앱. **클라이언트 PC에만** 설치한다.

## 원인
- 한국어 키보드 드라이버(`kbd101a.dll`)는 오른쪽 Alt를 한/영 전용 스캔코드 `0xF2`로, 오른쪽 Ctrl을 한자 전용 스캔코드 `0xF1`로 바꾼다.
- Parsec은 키를 스캔코드 기준으로 전달하는데, 이 두 스캔코드는 전달하지 않는다.

## 동작
Parsec 창(`parsecd`)에 포커스가 있을 때만, 지정한 트리거 키를 막고 다음 키를 대신 보낸다.

| 동작 | 기본 트리거 | 대신 보내는 것 |
|---|---|---|
| 한/영 | 오른쪽 Alt (VK_HANGUL / scan `0xF2`) | scan `E0 38` (오른쪽 Alt) |
| 한자 | 오른쪽 Ctrl (VK_HANJA / scan `0xF1`) | scan `E0 1D` (오른쪽 Ctrl) |

호스트 쪽 한국어 드라이버가 `E0 38`, `E0 1D`를 다시 한/영, 한자로 해석한다. 이 동작은 테스트 결과로 확인했다.

트리거는 「키 설정...」에서 다른 키나 조합(예: 오른쪽 Ctrl → 한/영, `Ctrl + Shift + C` → 한자)으로 바꿀 수 있다. 조합 키를 쓰면 호스트가 수식키 없이 한/영·한자만 누른 것으로 받도록, 보내기 전에 수식키를 잠시 뗐다가 다시 누른다. Alt나 Win이 들어간 조합은 메뉴나 시작 메뉴가 열리지 않도록 Ctrl 탭을 끼워 넣는다.

## 사용
- `ParsecImeBridge.exe`를 실행하면 트레이에 「한」 아이콘이 생긴다. 회색은 꺼짐, 파란색은 대기, 초록색은 Parsec 활성·변환 중이다.
- 우클릭 메뉴: 사용 / 한/영 전환 / 한자 변환 / 키 설정... / Windows 시작 시 실행 / 종료
- 아이콘을 더블클릭하면 사용을 켜고 끈다.
- 설정은 `HKCU\Software\ParsecImeBridge`에, 자동 실행은 `HKCU\...\CurrentVersion\Run`에 저장된다.

## 빌드
```
dotnet build src/ParsecImeBridge -c Release -o dist
```
.NET Framework 4.8 대상이라 Windows 10/11에서 exe 하나만으로 실행된다. 전체 솔루션은 `dotnet build ParsecImeBridge.sln`으로 빌드한다.

## 진단 도구 (`tools/`)
원인을 찾을 때 쓴 도구들. 키보드 환경이 다른 PC에서 같은 문제가 생기면 이 도구로 다시 확인할 수 있다. 실행 중에는 모든 키 입력을 로그 파일에 기록한다.

| 도구 | 실행 위치 | 용도 |
|---|---|---|
| `ParsecClientProbe` (net48) | 클라이언트 | 키 이벤트 기록(포커스 프로세스 포함), 후보 키를 Parsec 창으로 보내는 테스트 |
| `ParsecKeyProbe` (net8) | 호스트 | Parsec이 주입한 키 기록, Parsec 연결 상태 표시 |
| `KeyboardInfo` (net48) | 어디서나 | 레이어 드라이버·레이아웃·Scancode Map·키보드 장치 정보, Raw Input으로 장치별 스캔코드 확인 |
