<img src="assets/meerkat-preview.png" width="96" align="right" alt="OnTopPasskey icon">

# OnTopPasskey

Keeps the Windows Hello / passkey prompt on top and focused, so your fingerprint
actually registers.

**[⬇ Download the latest installer](https://github.com/sallyHADEV/OnTopPasskey/releases/latest)**

---

## English

### The problem

On Windows 11 the **"Windows Security"** prompt (fingerprint / PIN / passkey) is
sometimes created *behind* your browser window and without input focus. While it
sits like that, the fingerprint reader ignores your finger — you have to click the
prompt first before it will accept anything. This did not happen on Windows 10 and
Microsoft has not shipped a fix or a setting for it.

OnTopPasskey watches for that prompt and, the moment it appears, pulls it to the
front and gives it focus.

It does **not** perform, replace, or bypass the authentication. You still scan your
own finger — the program only fixes where the window sits and who has focus.

### Install

1. Download `OnTopPasskeySetup-x.y.z.exe` from the
   [Releases page](https://github.com/sallyHADEV/OnTopPasskey/releases/latest).
2. Run it and approve the UAC prompt.
3. Done. A meerkat icon appears in the notification area.

The app installs to `C:\Program Files\OnTopPasskey` and starts automatically at
logon — with no UAC prompt after this first one.

### Why administrator rights are required

`CredentialUIBroker.exe`, the process that shows the prompt, runs at **integrity
level 8202** — slightly above the 8192 that ordinary programs get. Windows compares
those numbers, so a normal-privilege process is refused when it tries to move or
focus that window (`ERROR_ACCESS_DENIED`). Running at the administrator level
(12288) clears that bar.

That is also why the installer registers a scheduled task instead of a normal
start-up entry: the task runs the app with highest privileges at logon, so you
never see a UAC prompt again.

The app is installed under `Program Files` on purpose. A task that runs with
highest privileges must not point at a folder you can write to, or anyone able to
replace that file would gain administrator rights at your next logon.

### Tray menu

| Item | What it does |
|---|---|
| Enabled | Turns the watcher on and off |
| Logging ▸ | See below |
| Restart as administrator | Only shown if the app is not already elevated |
| Self test | Tries the same logic against Character Map so you can see it work |
| Open settings file / Reload settings | |
| Exit | |

**Logging** submenu:

| Item | What it does |
|---|---|
| Off | Writes nothing at all — no log file is created |
| Normal | Detections and their outcome only (default, ~4 lines per prompt) |
| Verbose | Also traces foreground / active / focus state while the prompt is open |
| Diagnostic mode | Logs every window that appears. Use this if the prompt is not detected |
| Open log / Clear log | |

### Did it work?

Trigger a passkey or fingerprint prompt once, then open **OnTopPasskey Log** from
the Start menu. A successful run looks like this:

```
[detect] class="Credential Dialog Xaml Host" title="Windows Security" src=shellhook
[force]  TOPMOST=OK  SetForegroundWindow=OK  => foreground acquired, topmost applied
```

- **No `[detect]` line at all** — the matching rules did not fire. Turn on
  *Diagnostic mode*, reproduce it once, and add the `class` / `title` / `proc`
  values from the log to `config.ini`.
- **`[detect]` but `err 5` / `foreground not acquired`** — a privilege problem.
  Use *Restart as administrator*, or reinstall so the scheduled task handles it.

### Settings

`%LOCALAPPDATA%\OnTopPasskey\config.ini`

| Key | Default | Meaning |
|---|---|---|
| `Language` | `auto` | `auto` follows the Windows display language; or `ko` / `en` |
| `LogLevel` | `1` | `0` off, `1` normal, `2` verbose |
| `SpyMode` | `0` | Diagnostic mode |
| `ClassContains` | `Credential Dialog Xaml Host` | Window class substring match |
| `ProcessName` | `CredentialUIBroker.exe`, `LogonUI.exe` | Process name match |
| `TitleEquals` / `TitleContains` | localized titles | Title match (a fallback signal) |
| `KeepTopmost` | `1` | Keep the prompt topmost while it is open |
| `UseObjectShowHook` | `1` | Secondary detector; `0` is slightly lighter |
| `SafetyPollMs` | `0` | Last-resort scan interval in ms; `0` = off |
| `TraceMs` | `500` | Trace interval when `LogLevel=2` |

Writing any rule entry (`ClassContains`, `ProcessName`, `TitleEquals`,
`TitleContains`) replaces the built-in rules entirely.

The window class is matched first because it is the same in every language;
window titles are localized and are only used as a fallback.

### Resource usage

| | |
|---|---|
| Idle CPU | **0.000 s over 45 s — 0.0000%** |
| Memory | about **3 MB** working set |
| Installed size | about **100 KB**, a single executable |

There is no periodic polling. Detection uses a shell hook, so the program only
does work when a new top-level window is created; the rest of the time its thread
is asleep. It is plain Win32 — the only managed assembly loaded at runtime is
`mscorlib`.

### Uninstall

**Settings → Apps → Installed apps → OnTopPasskey → Uninstall**, or run
`uninstall.exe` in the install folder.

Your settings and logs in `%LOCALAPPDATA%\OnTopPasskey` are kept. Delete that
folder too if you want them gone.

### Requirements

Windows 10 or 11, and .NET Framework 4.x (included with Windows).
The interface follows your Windows display language: Korean or English.

### Background

- [Microsoft Q&A: fingerprint pop-up appears behind windows with no focus](https://learn.microsoft.com/en-us/answers/questions/4335748/fingerprint-sign-in-for-apps-and-browser-pop-up-wi)
- [Bitwarden community: Windows Hello prompt not focused](https://community.bitwarden.com/t/windows-hello-prompt-not-focused-when-unlocking-chrome-browser-extension-with-windows-hello/85509)
- [AuthenticatorChooser](https://github.com/Aldaviva/AuthenticatorChooser) — another
  tool for the same dialog; the `Credential Dialog Xaml Host` window class used here
  matches its implementation

### License

[MIT](LICENSE) — free to use, modify, and redistribute, including commercially,
as long as the copyright notice is kept. Provided as is, without warranty.

---

## 한국어

### 문제

Windows 11에서 **"Windows 보안"** 창(지문 / PIN / 패스키)이 브라우저 창 **뒤에**
포커스 없이 만들어지는 경우가 있습니다. 이 상태에서는 지문 센서에 손을 올려도 입력이
무시되고, 창을 한 번 클릭해야 인증이 진행됩니다. Windows 10에서는 없던 현상이고
Microsoft의 수정이나 설정도 아직 없습니다.

OnTopPasskey는 그 창이 나타나는 순간을 감지해 곧바로 맨 앞으로 올리고 포커스를
가져옵니다.

인증을 대신하거나 우회하지 **않습니다.** 지문 스캔은 그대로 사용자가 합니다. 이
프로그램은 창의 위치와 포커스만 바로잡습니다.

### 설치

1. [Releases 페이지](https://github.com/sallyHADEV/OnTopPasskey/releases/latest)에서
   `OnTopPasskeySetup-x.y.z.exe`를 내려받습니다.
2. 실행하고 UAC 동의 창을 승인합니다.
3. 끝입니다. 알림 영역에 미어캣 아이콘이 나타납니다.

`C:\Program Files\OnTopPasskey`에 설치되고 로그온할 때 자동으로 실행됩니다. 처음
한 번 외에는 UAC 창이 뜨지 않습니다.

### 관리자 권한이 필요한 이유

인증 창을 띄우는 `CredentialUIBroker.exe`는 **무결성 수준 8202**로 실행됩니다. 일반
프로그램의 8192보다 살짝 높습니다. Windows는 이 값을 숫자로 비교하기 때문에, 일반
권한 프로세스가 그 창을 옮기거나 포커스를 주려 하면 거부합니다(`ERROR_ACCESS_DENIED`).
관리자 수준(12288)이면 이 기준을 넘어섭니다.

설치 관리자가 일반 시작 프로그램 대신 작업 스케줄러를 등록하는 것도 같은 이유입니다.
로그온 시 최고 권한으로 실행되므로 이후에는 UAC 창을 다시 볼 일이 없습니다.

`Program Files`에 설치하는 것도 의도한 선택입니다. 최고 권한으로 실행되는 작업이
사용자가 쓸 수 있는 폴더를 가리키면, 그 파일을 바꿀 수 있는 누구든 다음 로그온 때
관리자 권한을 얻게 됩니다.

### 트레이 메뉴

| 항목 | 설명 |
|---|---|
| 활성화 | 감시 켜기 / 끄기 |
| 로그 ▸ | 아래 표 참조 |
| 관리자 권한으로 다시 실행 | 이미 상승 상태면 표시되지 않습니다 |
| 동작 자가 진단 | 문자표를 대상으로 같은 로직을 시험해 눈으로 확인합니다 |
| 설정 파일 열기 / 설정 다시 읽기 | |
| 종료 | |

**로그** 하위 메뉴:

| 항목 | 설명 |
|---|---|
| 기록 안 함 | 아무것도 쓰지 않습니다. 로그 파일 자체가 생기지 않습니다 |
| 기본 | 감지와 처리 결과만 (기본값, 인증 1회당 4줄 정도) |
| 자세히 | 창이 떠 있는 동안의 전경 / 활성 / 포커스 상태까지 추적 |
| 진단 모드 | 나타나는 모든 창을 기록합니다. 인증 창이 감지되지 않을 때 쓰세요 |
| 로그 열기 / 로그 비우기 | |

### 잘 동작하는지 확인

패스키나 지문 인증을 한 번 띄운 뒤 시작 메뉴에서 **OnTopPasskey 로그**를 엽니다.
정상이면 이렇게 남습니다.

```
[감지] class="Credential Dialog Xaml Host" title="Windows 보안" src=shellhook
[강제] TOPMOST=OK  SetForegroundWindow=OK  => 포그라운드 획득, 항상위 적용
```

- **`[감지]` 줄이 아예 없으면** — 판별 규칙이 맞지 않는 것입니다. *진단 모드*를 켜고
  다시 띄운 뒤, 로그에 찍힌 `class` / `title` / `proc` 값을 `config.ini`에 추가하세요.
- **`[감지]`는 있는데 `err 5` / `포그라운드 미획득`이면** — 권한 문제입니다.
  *관리자 권한으로 다시 실행*을 쓰거나, 설치판으로 다시 설치하면 작업 스케줄러가
  해결합니다.

### 설정

`%LOCALAPPDATA%\OnTopPasskey\config.ini`

| 키 | 기본값 | 의미 |
|---|---|---|
| `Language` | `auto` | `auto`는 Windows 표시 언어를 따름. `ko` / `en` 으로 강제 |
| `LogLevel` | `1` | `0` 기록 안 함, `1` 기본, `2` 자세히 |
| `SpyMode` | `0` | 진단 모드 |
| `ClassContains` | `Credential Dialog Xaml Host` | 창 클래스명 부분 일치 |
| `ProcessName` | `CredentialUIBroker.exe`, `LogonUI.exe` | 프로세스명 일치 |
| `TitleEquals` / `TitleContains` | 로케일별 제목 | 제목 일치 (보조 신호) |
| `KeepTopmost` | `1` | 인증 창이 떠 있는 동안 항상 위 유지 |
| `UseObjectShowHook` | `1` | 보조 감지기. `0`이면 조금 더 가벼움 |
| `SafetyPollMs` | `0` | 최후의 안전망 스캔 주기(ms). `0` = 사용 안 함 |
| `TraceMs` | `500` | `LogLevel=2`일 때 추적 주기 |

규칙 항목(`ClassContains`, `ProcessName`, `TitleEquals`, `TitleContains`)을 하나라도
직접 적으면 기본 규칙 대신 파일의 규칙만 사용합니다.

창 클래스명을 1순위로 보는 이유는 언어와 무관하게 같기 때문입니다. 창 제목은
로케일마다 달라지므로 보조 신호로만 씁니다.

### 리소스 사용량

| | |
|---|---|
| 유휴 CPU | **45초 동안 0.000초 — 0.0000%** |
| 메모리 | 워킹셋 약 **3 MB** |
| 설치 크기 | 단일 실행 파일 약 **100 KB** |

주기적인 폴링이 없습니다. 감지는 셸 훅으로 처리하므로 최상위 창이 새로 만들어질 때만
일을 하고, 나머지 시간에는 스레드가 잠들어 있습니다. 순수 Win32로 작성되어 런타임에
로드되는 관리 어셈블리는 `mscorlib` 하나뿐입니다.

### 제거

**설정 → 앱 → 설치된 앱 → OnTopPasskey → 제거**, 또는 설치 폴더의 `uninstall.exe`를
실행하세요.

`%LOCALAPPDATA%\OnTopPasskey`의 설정과 로그는 남습니다. 완전히 지우려면 그 폴더도
삭제하세요.

### 요구 사항

Windows 10 또는 11, .NET Framework 4.x (Windows 기본 포함).
화면 언어는 Windows 표시 언어를 따릅니다 (한국어 / 영어).

### 참고

- [Microsoft Q&A: 지문 인증 창이 뒤에 뜨고 포커스가 없는 문제](https://learn.microsoft.com/en-us/answers/questions/4335748/fingerprint-sign-in-for-apps-and-browser-pop-up-wi)
- [Bitwarden 커뮤니티: Windows Hello 창에 포커스가 오지 않는 문제](https://community.bitwarden.com/t/windows-hello-prompt-not-focused-when-unlocking-chrome-browser-extension-with-windows-hello/85509)
- [AuthenticatorChooser](https://github.com/Aldaviva/AuthenticatorChooser) — 같은
  대화상자를 다루는 다른 도구. 여기서 쓰는 `Credential Dialog Xaml Host` 창
  클래스명이 그 구현과 일치합니다

### 라이선스

[MIT](LICENSE) — 저작권 표시만 유지하면 상업적 이용을 포함해 자유롭게 사용, 수정,
재배포할 수 있습니다. 보증은 제공되지 않습니다.
