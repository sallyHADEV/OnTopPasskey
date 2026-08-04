# OnTopPasskey

Windows Hello / 패스키 인증 창("Windows 보안")이 다른 창에 가려지거나 포커스를 받지 못해
지문 인증이 먹지 않는 문제를 해결하는 초경량 트레이 프로그램.

인증 창이 뜨는 순간을 감지해 즉시 **항상 위(TOPMOST) + 포그라운드/포커스**를 강제로 가져온다.

---

## 1. 구현 가능성 검토 (OS 보안 UI를 외부에서 조작할 수 있는가)

"보안 인증 창이니 외부 간섭이 막혀 있지 않겠느냐"는 의문에 대한 확인 결과.

### 1-1. 보안 데스크톱(Secure Desktop)이 아니다 — 조작 가능

UAC 프롬프트는 별도 데스크톱(Winlogon desktop)에서 뜨기 때문에 일반 프로그램은 그 창을
**보지도 못한다**. 반면 이 인증 창은 다르다.

- 애초에 "브라우저 창에 가려진다"는 증상 자체가 일반 데스크톱의 Z-order에 참여한다는 증거다.
  보안 데스크톱 창은 다른 창에 가려질 수가 없다.
- 셸 훅(`HSHELL_WINDOWCREATED`)에 정상적으로 잡힌다.
- 서드파티 도구가 같은 창을 UI Automation으로 읽고 버튼까지 클릭하는 선례가 있다
  ([AuthenticatorChooser](https://github.com/Aldaviva/AuthenticatorChooser)).

### 1-2. 감지는 통과, 조작 단계에서 무결성 수준(IL)에 막힌다

먼저 일반 앱(문자표)으로 강제 로직 자체를 검증했고, Medium IL(비관리자)에서 정상 동작했다.

```
=== 시작 ===  self IL=8192(Medium)
[감지] class="#32770" title="문자표"
[계측] target proc=charmap.exe IL=8192(Medium)
[강제] TOPMOST=OK  AttachThreadInput(fg=True,target=True)  SetForegroundWindow=OK
       => 포그라운드 획득, 항상위 적용
```

그다음 **실제 Windows Hello 인증 창**으로 측정한 결과가 핵심이다.

```
[감지] class="Credential Dialog Xaml Host" src=shellhook          ← 감지 성공
[계측] target proc=CredentialUIBroker.exe IL=8202(Medium)
[강제] TOPMOST=실패(err 5)  AttachThreadInput(fg=True,target=False)
       SetForegroundWindow=실패(err 5)  => 포그라운드 미획득
```

**감지는 완벽하고, 막히는 지점은 조작 단계다.** 이유가 IL 값에 그대로 드러난다.

| | 무결성 수준 |
|---|---|
| 이 프로그램 (일반 실행) | `8192` (표준 Medium) |
| `CredentialUIBroker.exe` | **`8202`** (0x200A, Medium보다 10 높음) |

UIPI는 무결성 수준을 **숫자로 비교**한다. 같은 사용자 계정이어도 `8192 < 8202`라서
낮은 쪽에서 높은 쪽 창으로 향하는 `SetWindowPos` / `AttachThreadInput` /
`SetForegroundWindow`가 모두 거부된다(`err 5` = `ERROR_ACCESS_DENIED`).
`AttachThreadInput`이 전경 스레드에는 붙었는데(`fg=True`) 대상 스레드에만
실패한(`target=False`) 것도 이 설명과 정확히 맞는다.

자격 증명 UI를 같은 계정의 일반 앱으로부터 보호하려고 표준 Medium보다 살짝 올려둔
의도적인 설계로 보인다.

### 1-3. 해결: High 무결성으로 실행

High IL은 `12288`이라 `8202`를 넘어선다. 즉 **관리자 권한으로 실행하면 통과한다.**
같은 창을 다루는 AuthenticatorChooser가 매니페스트에 `requireAdministrator`를 두고
자동 실행을 "작업 스케줄러에서 최고 권한으로" 등록하도록 안내하는 이유도 이것이다.

1. **즉시 확인** — 트레이 아이콘 우클릭 → **관리자 권한으로 다시 실행**.
   UAC 동의 창을 승인하면 High IL로 재시작한다.
2. **영구 적용** — 로그온 시 UAC 창 없이 최고 권한으로 자동 실행되는 작업 등록.

```bash
powershell -ExecutionPolicy Bypass -File ".\install-startup-task.ps1"
```

프로그램은 이 상황을 스스로 인지한다. UIPI로 거부되면 로그에 `[권한]` 줄을 남기고 풍선
알림으로 안내하며, 헛돌지 않도록 10회 연속 거부되면 해당 창에 대한 재시도를 멈춘다.

### 1-4. 정리

| 항목 | 판정 |
|---|---|
| 보안 데스크톱이라 접근 불가? | **아니오.** 일반 데스크톱 창이다 |
| 인증 창 감지 | **성공** (`Credential Dialog Xaml Host`, 셸 훅이 즉시 포착) |
| 일반 권한으로 창 조작 | **불가.** 대상 IL 8202 > 우리 8192 → UIPI 차단 |
| 관리자(High=12288) 권한으로 조작 | **가능.** 8202를 넘어선다 |
| 인증 자체를 우회/조작 | **하지 않음.** 창을 앞으로 가져올 뿐, 인증은 사용자가 직접 한다 |

이 프로그램은 인증을 대신하거나 우회하지 않는다. 창 배치와 포커스만 바로잡고,
지문 스캔은 여전히 사용자가 직접 해야 한다.

---

## 2. 경량화 (측정치)

| 항목 | 측정값 |
|---|---|
| 실행 파일 크기 | **85 KB** (단일 exe, 설치 불필요 / 코드 29 KB + 아이콘 리소스) |
| 유휴 CPU | **0.000초 / 45초 = 0.0000%** |
| 워킹셋 | **약 3.0 MB** |
| 스레드 | 6 (대부분 CLR 기본 스레드) |
| 상시 폴링 | **없음** (유휴 시 동작 중인 타이머 0개) |

설계상 근거:

- **순수 Win32.** WinForms / System.Drawing / System.Diagnostics를 쓰지 않아 런타임에
  로드되는 관리 어셈블리가 mscorlib 하나뿐이다. (WinForms 트레이 앱은 보통 15~25 MB)
- **주 감지기는 셸 훅.** 최상위 창이 새로 생길 때만 메시지가 온다. 창 목록을 주기적으로
  훑는 폴링이 전혀 없다.
- **대기 중에는 `GetMessage`에서 블록**되어 스레드가 잠들어 있다. 그래서 유휴 CPU가
  측정 한계 아래(0)로 나온다.
- **재시도 타이머는 인증 창이 떠 있는 동안에만** 켜고 닫히면 즉시 `KillTimer`.
- 이벤트 콜백은 3단 필터(가장 싼 검사부터)로 대부분을 즉시 반려하고, 문자열 버퍼 재사용과
  PID→프로세스명 캐시로 이벤트당 힙 할당을 최소화했다.
- 초기화 후와 인증 창 처리 후 워킹셋을 OS에 반납한다.

---

## 3. 동작 방식

**감지 (3중)**

1. `RegisterShellHookWindow` → `HSHELL_WINDOWCREATED` / `HSHELL_WINDOWACTIVATED` — 주 감지기, 비용 거의 0
2. `SetWinEventHook(EVENT_SYSTEM_DIALOGSTART)`, `(EVENT_SYSTEM_FOREGROUND)` — 저빈도 보조
3. `SetWinEventHook(EVENT_OBJECT_SHOW)` — 감지율 최대화용, 설정에서 끌 수 있음

**판별** — 최상위 창 클래스명 `Credential Dialog Xaml Host`가 1순위 신호다.
제목("Windows 보안"/"Windows Security")은 로케일마다 달라지므로 보조로만 쓰고,
프로세스명(`CredentialUIBroker.exe`)도 보조 신호다.

**강제**

1. `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)` — 활성화보다 먼저 Z-order를 올려 가려짐부터 해소
2. 현재 전경 스레드와 대상 스레드에 `AttachThreadInput`으로 입력 큐를 붙임
   (`SetForegroundWindow`의 호출자 제한을 우회하는 표준 기법)
3. `BringWindowToTop` → `SetForegroundWindow` → `SetActiveWindow` → `SetFocus`
4. 입력 큐 분리

> **소유자 체인을 따라 올라가면 안 된다.**
> 초기 구현은 `GetAncestor(hwnd, GA_ROOTOWNER)`로 "최상위 소유자"를 찾아 조작했는데,
> WebAuthn은 호출자(브라우저)의 HWND를 넘기므로 인증 창이 **브라우저 창에 소유된 창**으로
> 만들어질 수 있다. 소유자 체인은 프로세스 경계를 넘으므로 결과적으로 브라우저 창을
> 조작하게 되고, 그러면 (a) 인증 창이 아니라 브라우저가 활성화되어 여전히 클릭해야 하며
> (b) 인증이 끝난 뒤 브라우저에 '항상 위'가 눌러붙는다.
> 반드시 규칙에 매칭된 창 자체를 다뤄야 한다.

**강제 시점** — XAML 호스트는 내용이 비동기로 올라오므로 창이 만들어진 순간 한 번 잡는
것만으로는 내부 콘텐츠에 포커스가 들어가지 않는다. 그래서 세 갈래로 나눈다.

| 시점 | 동작 | 입력 큐 |
|---|---|---|
| 감지 즉시 + 0.2 / 0.7 / 1.5 / 2.5초 | 예약 재시도 | 붙임 |
| 포커스를 빼앗겼을 때 (최소 500ms 간격) | 재탈취 | 붙임 |
| 포그라운드 상태일 때 | 항상 위만 유지 | **안 붙임** |

`AttachThreadInput`은 두 스레드의 입력 큐를 묶어 입력을 직렬화하므로, 매 100ms마다 하면
끊기는 느낌을 준다. 그래서 꼭 필요할 때만 붙이고 최소 간격을 둔다.

추적은 창이 닫힐 때까지 유지하며, 닫히면 원래 '항상 위'가 아니었던 창은 상태를 되돌린다.

---

## 4. 사용법

### 빌드

```bash
powershell -ExecutionPolicy Bypass -File ".\build.ps1"
```

.NET SDK가 필요 없다. Windows에 기본 포함된 .NET Framework 4.x의 `csc.exe`로 컴파일한다.
결과물은 `bin\OnTopPasskey.exe` 하나뿐이다.

### 실행

`bin\OnTopPasskey.exe`를 실행하면 트레이(미어캣 아이콘)에 상주한다. 우클릭 메뉴:

| 항목 | 설명 |
|---|---|
| 활성화 | 감시 on/off |
| **로그 ▸** | 아래 표 참조 |
| Windows 시작 시 자동 실행 | `HKCU\...\Run` 등록. **설치판에서는 표시되지 않는다** (작업 스케줄러와 중복되므로) |
| 관리자 권한으로 다시 실행 | High IL로 재시작. 이미 상승 상태면 표시되지 않는다 |
| 동작 자가 진단 | 문자표(charmap)를 대상으로 강제 로직이 통하는지 즉시 시험 |
| 설정 파일 열기 / 설정 다시 읽기 | |
| 종료 | |

로그 하위 메뉴:

| 항목 | 설명 |
|---|---|
| 기록 안 함 | **파일에 아무것도 쓰지 않는다.** 로그 파일이 아예 생기지 않는다 |
| 기본 | 인증 창을 감지하고 처리한 결과만 (기본값, 인증 1회당 4~5줄) |
| 자세히 | 창이 떠 있는 동안의 입력 상태 추적까지 (`TraceMs` 주기) |
| 진단 모드 | 나타나는 모든 창의 class/title/proc 기록. 문제 진단용 |
| 로그 열기 / 로그 비우기 | |

기록 수준을 바꾸는 순간만은 수준과 관계없이 한 줄을 남긴다. 나중에 로그가 비어 있는
이유를 알 수 있게 하기 위해서다.

트레이 아이콘은 `assets/make-icon.ps1`이 GDI+로 그려 멀티 사이즈 `.ico`로 묶은 것이고,
빌드 시 `/win32icon`으로 exe에 포함된다. 런타임에 아이콘 파일은 필요 없다.

로그와 설정: `%LOCALAPPDATA%\OnTopPasskey\`

### 첫 확인 절차 (중요)

1. 프로그램을 실행해 둔다.
2. 평소처럼 패스키/Windows Hello 인증을 한 번 띄운다.
3. 트레이 → **로그 열기**. 다음 세 줄이 있으면 정상이다.

```
[감지] class="Credential Dialog Xaml Host" ...
[계측] target pid=... proc=CredentialUIBroker.exe IL=8192(Medium) ...
[강제] TOPMOST=OK ... => 포그라운드 획득, 항상위 적용
```

- `[감지]`가 아예 없으면 → 판별 규칙이 안 맞는 것. 트레이에서 **진단 모드**를 켜고 다시
  띄운 뒤, 로그에 찍힌 실제 class/title/proc 값을 `config.ini`에 추가한다.
- `[감지]`는 되는데 `err 5` / `포그라운드 미획득`이면 → **권한 문제**(1-2 참조).
  트레이 → **관리자 권한으로 다시 실행**으로 해결한다.

### 관리자 권한으로 자동 실행 (예비 경로)

일반 권한으로 포커스 강제가 실패할 때만 쓴다. 로그온 시 UAC 창 없이 최고 권한으로 실행되는
작업 스케줄러 항목을 만든다.

```bash
powershell -ExecutionPolicy Bypass -File ".\install-startup-task.ps1"
```

해제:

```bash
powershell -ExecutionPolicy Bypass -File ".\install-startup-task.ps1" -Uninstall
```

---

## 5. 설정 (`%LOCALAPPDATA%\OnTopPasskey\config.ini`)

| 키 | 기본값 | 설명 |
|---|---|---|
| `ClassContains` | `Credential Dialog Xaml Host` | 창 클래스명 부분 일치 (여러 줄 가능) |
| `ProcessName` | `CredentialUIBroker.exe`, `LogonUI.exe` | 프로세스명 일치 |
| `TitleEquals` | `Windows 보안`, `Windows Security` | 제목 완전 일치 |
| `TitleContains` | `Windows Hello` | 제목 부분 일치 |
| `Language` | `auto` | `auto`=Windows 표시 언어를 따름, `ko`, `en` |
| `LogLevel` | `1` | `0`=기록 안 함, `1`=기본, `2`=자세히 |
| `SpyMode` | `0` | 진단 모드 |
| `KeepTopmost` | `1` | 인증 창이 떠 있는 동안 항상 위 유지 |
| `UseObjectShowHook` | `1` | 보조 감지기. `0`이면 더 가벼워지지만 감지율이 약간 낮아짐 |
| `SafetyPollMs` | `0` | 최후의 안전망 스캔 주기(ms). `0`=사용 안 함(권장) |

규칙 항목(`ClassContains`/`ProcessName`/`TitleEquals`/`TitleContains`)을 하나라도 직접
적으면 기본 규칙 대신 파일의 규칙만 사용한다.

---

## 6. 배포용 설치 파일 만들기

```bash
powershell -ExecutionPolicy Bypass -File ".\package.ps1"
```

`dist\OnTopPasskeySetup-1.0.0.exe` (172 KB) 하나가 만들어진다. **본체를 리소스로 품은
단일 설치 파일**이며, 실행하면 UAC 동의 창이 뜬 뒤 설치 마법사가 열린다.

Inno Setup, NSIS, WiX 같은 외부 설치 도구가 필요 없다. Windows 에 기본 포함된
`csc.exe` 만으로 설치 관리자(`src\Setup.cs`)를 컴파일하고, `/resource:` 옵션으로
본체 실행 파일을 그 안에 넣는다. `package.ps1` 은 본체 빌드 → 설치 관리자 빌드 →
SHA256 출력까지 한 번에 하며, 본체에 박힌 버전과 패키지 버전이 다르면 중단한다.

| 명령줄 | 동작 |
|---|---|
| (없음) | 설치 마법사 |
| `/uninstall` | 제거 |
| `/silent` | UI 없이 설치 (자동 배포용) |
| `/lang:ko` `/lang:en` | 표시 언어 강제 (기본은 Windows 표시 언어를 따름) |

### 한국어 / English

본체와 설치 관리자 모두 **Windows 표시 언어를 보고 한국어와 영어를 자동 전환**한다
(`GetUserDefaultUILanguage` 의 기본 언어 ID 가 `LANG_KOREAN`이면 한국어). 따라서 같은
설치 파일 하나를 국내외에 그대로 배포하면 된다.

번역은 리소스 어셈블리 대신 `L.T("한국어", "English")` 형태로 코드 옆에 나란히 둔다.
실행 파일이 하나로 유지되고, 문구를 고칠 때 두 언어가 어긋날 일이 없다.
자동 판별을 무시하고 싶으면 설치 관리자는 `/lang:en`, 본체는 `config.ini` 의 `Language=en`
으로 강제한다.

시작 메뉴 바로 가기 이름도 언어에 따라 달라지므로(`OnTopPasskey 시작` / `OnTopPasskey`),
제거할 때는 두 언어의 이름을 모두 지운다.

`src\Setup.cs` 는 `/define:PREVIEW` 로 빌드하면 권한 검사를 건너뛴다. 화면 배치를
눈으로 확인하기 위한 빌드 전용 스위치이며 배포본에는 들어가지 않는다.

### 설치가 하는 일

1. `%ProgramFiles%\OnTopPasskey\` 에 본체와 제거 관리자(`uninstall.exe`) 설치
2. 작업 스케줄러에 `OnTopPasskey` 등록 (로그온 시, **최고 권한**, UAC 창 없음)
3. 시작 메뉴 바로 가기 2개 (실행 / 로그 보기)
4. "앱 및 기능" 목록에 등록 (설정에서 제거 가능)
5. 이전 등록 정리 — 다른 경로를 가리키는 기존 작업, `HKCU\...\Run` 항목, 실행 중인
   구버전 프로세스를 모두 제거한 뒤 설치한다

제거할 때는 제거 관리자가 자기 자신을 임시 폴더로 복사해 그쪽에서 다시 실행한다.
설치 폴더 안에서 돌면 자기 파일이 잠겨 폴더를 지울 수 없기 때문이다.

### Program Files 에 설치하는 이유 (보안)

이 프로그램은 인증 창을 조작하려면 High 무결성으로 실행되어야 한다. 그런데 **최고 권한으로
실행되는 작업이 사용자가 쓸 수 있는 폴더**(문서, 다운로드, 바탕 화면 등)를 가리키면,
그 파일을 바꿀 수 있는 누구든 로그온 시 관리자 권한으로 임의 코드를 실행할 수 있다.
전형적인 권한 상승 통로다.

`Program Files` 는 관리자만 쓸 수 있으므로 이 통로가 막힌다. 개발 중에는 편의상
작업 폴더에서 실행했지만, 배포판은 반드시 이쪽이어야 한다.

시작 메뉴 바로 가기가 exe 를 직접 가리키지 않고 `schtasks /run` 을 거치는 것도 같은 이유다.
exe 를 직접 실행하면 일반 권한으로 떠서 인증 창을 조작하지 못한다.

## 7. 알려진 배경

- 이 증상은 Windows 11의 알려진 회귀 현상으로, Windows 10에서는 발생하지 않았다.
  Microsoft의 공식 수정이나 설정은 아직 없다
  ([Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/4335748/fingerprint-sign-in-for-apps-and-browser-pop-up-wi),
  [Bitwarden 커뮤니티](https://community.bitwarden.com/t/windows-hello-prompt-not-focused-when-unlocking-chrome-browser-extension-with-windows-hello/85509)).
- 대상 창 클래스명 `Credential Dialog Xaml Host`는
  [AuthenticatorChooser](https://github.com/Aldaviva/AuthenticatorChooser)의 구현에서도
  동일하게 확인된다.
- 검증 환경: Windows 11 Pro 26200 (25H2), 64비트.
