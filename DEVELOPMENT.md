# 개발 기록

나중에 이 프로젝트를 다시 열었을 때 필요한 것들. **코드만 읽어서는 알 수 없는 것**
— 왜 그렇게 만들었는지, 무엇을 시도했다 버렸는지, 어디서 막혔는지 — 을 남긴다.
사용자용 설명은 [README.md](README.md) 에 있다.

작성 기준: 2026-08-04, 버전 1.0.3

---

## 1. 목표

1. Windows Hello / 패스키 인증 창이 다른 창에 가려지고 포커스를 받지 못해 지문이
   먹지 않는 문제를 해결한다. 창이 뜨는 즉시 앞으로 가져오고 포커스를 준다.
2. 최대한 가볍게. 시스템에 상시 부담을 주지 않는다.

인증 자체는 절대 대신하거나 우회하지 않는다. 창 배치와 포커스만 바로잡는다.

---

## 2. 핵심 발견 (다시 조사하지 말 것)

### 2-1. 대상 창은 보안 데스크톱이 아니다

UAC 프롬프트는 별도 데스크톱에 떠서 일반 프로그램이 **보지도 못한다.** 반면 이
인증 창은 일반 데스크톱에 있다. "브라우저에 가려진다"는 증상 자체가 일반 Z-order 에
참여한다는 증거이고, 셸 훅에도 정상적으로 잡힌다.

### 2-2. 창 식별은 클래스명으로

| | 값 |
|---|---|
| 최상위 창 클래스명 | `Credential Dialog Xaml Host` |
| 프로세스 | `CredentialUIBroker.exe` |

제목(`Windows 보안` / `Windows Security`)은 로케일마다 다르므로 **클래스명이 1순위**,
제목과 프로세스명은 보조 신호로만 쓴다. 같은 대화상자를 다루는
[AuthenticatorChooser](https://github.com/Aldaviva/AuthenticatorChooser) 도 동일한
클래스명을 쓴다.

### 2-3. 무결성 수준 8202 — 관리자 권한이 필요한 진짜 이유

```
CredentialUIBroker.exe  IL = 8202  (0x200A)
일반 프로그램            IL = 8192  (0x2000, 표준 Medium)
관리자                   IL = 12288 (0x3000, High)
```

`8202` 는 표준 Medium 보다 **10 높게** 잡힌 값이다. UIPI 는 무결성 수준을 숫자로
비교하므로, 같은 사용자 계정이어도 `8192 < 8202` 라서 창 조작이 전부 거부된다
(`ERROR_ACCESS_DENIED`). 자격 증명 UI 를 같은 계정의 일반 앱으로부터 보호하려는
의도적 설계로 보인다. High(12288)로 올리면 넘어선다.

`CredentialUIBroker.exe` 는 온디맨드 프로세스라 미리 측정할 수 없다. 그래서
계측을 프로그램에 내장해 첫 감지 시 `[계측]` 줄로 남긴다.

### 2-4. `err 5` 두 종류를 구분해야 한다

처음에 `self IL=8192 < target IL=8192` 같은 앞뒤 안 맞는 진단을 뱉은 적이 있다.

| 실패 | 의미 |
|---|---|
| `SetWindowPos` 가 err 5 | **진짜 UIPI 차단.** 같은 IL 이면 Z-order 변경은 항상 통한다 |
| `SetForegroundWindow` 만 err 5 | 단순 포그라운드 잠금. 권한 문제가 아니다 |

권한 판단에는 `SetWindowPos` 쪽만 본다 (`Force.Result.IntegrityBlocked`).

### 2-5. 소유자 체인을 따라가면 안 된다 — 가장 크게 물린 버그

초기 구현은 `GetAncestor(hwnd, GA_ROOTOWNER)` 로 최상위 소유자를 찾아 조작했다.
그런데 **WebAuthn 은 호출자(브라우저)의 HWND 를 넘기므로 인증 창이 브라우저 창에
소유된 창으로 만들어진다.** 소유자 체인은 프로세스 경계를 넘으므로 결국 브라우저
창을 조작하게 된다. 증상 세 가지가 전부 이 하나에서 나왔다.

- 인증 창이 아니라 브라우저가 활성화 → 여전히 클릭해야 지문이 먹음
- 로그는 `포그라운드 획득` 이라고 찍힘 (브라우저 기준이라 참)
- 인증이 끝난 뒤 **브라우저에 '항상 위' 가 눌러붙음**

로그에 `owner=0x...` 필드를 넣어 확증했다. 반드시 규칙에 매칭된 창 자체를 다룰 것.

### 2-6. 소유된 창의 TOPMOST 는 활성화 중에 한 번 풀린다

`SetWindowPos(HWND_TOPMOST)` 가 성공해도 활성화 과정에서 창 관리자가 되돌린다.
소유된 창의 topmost 상태가 소유자와 묶여 있기 때문. 활성화가 끝난 뒤 한 번 더
적용한다.

### 2-7. XAML 콘텐츠는 비동기로 올라온다

셸 훅은 창 생성 1ms 안에 알려주지만 그 시점엔 내부 콘텐츠가 없어 포커스가 들어가지
않는다. 그래서 감지 즉시 + **0.2 / 0.7 / 1.5 / 2.5초** 에 예약 재시도한다.

### 2-8. `AttachThreadInput` 은 자주 부르면 안 된다

두 스레드의 입력 큐를 묶어 입력을 직렬화하므로 100ms 마다 반복하면 끊기는 느낌을
준다. 예약 시점과 포커스를 실제로 빼앗겼을 때만(최소 500ms 간격) 붙인다.
이미 포그라운드면 입력 큐를 건드리지 않고 Z-order 만 손본다.

### 2-9. 셸 훅에는 메시지 전용 창을 쓸 수 없다

`RegisterShellHookWindow` 는 `HWND_MESSAGE` 창에 메시지를 전달하지 않을 수 있다.
그래서 `WS_POPUP` + `WS_EX_TOOLWINDOW` 로 보이지 않는 **실제 최상위 창**을 만든다.
부수 효과로 `FindWindow` 로는 이 창을 못 찾는다 — 테스트 시 `EnumWindows` 를 쓸 것.

---

## 3. 설계 결정

### 3-1. 감지 (3중)

| 순위 | 방법 | 성격 |
|---|---|---|
| 1 | `RegisterShellHookWindow` → `HSHELL_WINDOWCREATED` / `WINDOWACTIVATED` | 주 감지기. 최상위 창 생성 시에만 오므로 비용 거의 0 |
| 2 | `SetWinEventHook(EVENT_SYSTEM_DIALOGSTART / FOREGROUND)` | 저빈도 보조 |
| 3 | `SetWinEventHook(EVENT_OBJECT_SHOW)` | 감지율 최대. 콜백 빈도도 최대 (`UseObjectShowHook` 로 끌 수 있음) |

`SafetyPollMs` 는 최후의 안전망이며 기본 0(사용 안 함). 실제로 필요했던 적은 없다.

### 3-2. 경량화

- **순수 Win32.** WinForms / System.Drawing / System.Diagnostics 미사용 →
  런타임에 로드되는 관리 어셈블리는 `mscorlib` 하나뿐. (WinForms 트레이 앱은 보통 15~25MB)
- **상시 폴링 없음.** 유휴 시 동작 중인 타이머 0개, `GetMessage` 에서 블록되어 스레드가 잠듦
- 재시도 타이머는 인증 창이 떠 있는 동안에만 켜고 닫히면 즉시 `KillTimer`
- 이벤트 콜백은 3단 필터(싼 검사부터)로 대부분 즉시 반려, 문자열 버퍼 재사용

실측: 유휴 CPU **0.000초 / 45초**, 워킹셋 약 **3MB**, 본체 약 **100KB**.

설치 관리자(`Setup.cs`)는 WinForms 를 써도 된다. 한 번 실행되고 끝나므로 경량화
대상이 아니다.

### 3-3. Program Files 에 설치하는 이유 (보안)

최고 권한으로 실행되는 작업 스케줄러 항목이 **사용자가 쓸 수 있는 폴더**를 가리키면,
그 파일을 바꿀 수 있는 누구든 다음 로그온 때 관리자 권한을 얻는다. 전형적인 권한
상승 통로다. 개발 중에는 작업 폴더에서 실행했지만 배포판은 반드시 Program Files.

같은 이유로 시작 메뉴 바로 가기도 exe 를 직접 가리키지 않고 `schtasks /run` 을 거친다.
직접 실행하면 일반 권한으로 떠서 인증 창을 조작하지 못한다.

### 3-4. 로그온 지연 45초 (1.0.3)

부팅 직후 네트워크가 안 잡히는 현상을 겪고 넣었다. **원인은 이 프로그램이 아닐
가능성이 높다** (5-2 참조). 다만 로그온 혼잡 구간을 피하는 것은 부작용이 없어 넣었다.
`<Delay>PT45S</Delay>`, 설치 시 `/delay:N` 으로 조정, `0` 이면 즉시 실행.

---

## 4. 빌드와 배포

.NET SDK 가 필요 없다. Windows 기본 포함된 .NET Framework 4.x 의 `csc.exe` 로 빌드한다.

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

| 명령 | 결과 |
|---|---|
| `powershell -ExecutionPolicy Bypass -File .\build.ps1` | `bin\OnTopPasskey.exe` (본체) |
| `powershell -ExecutionPolicy Bypass -File .\package.ps1` | `dist\OnTopPasskeySetup-x.y.z.exe` (배포용) |

`package.ps1` 은 본체 빌드 → 설치 관리자 빌드 → SHA256 출력까지 한 번에 한다.
본체에 박힌 버전과 패키지 버전이 다르면 중단한다.

### 4-1. 설치 관리자 구조

외부 설치 도구(Inno Setup / NSIS / WiX) 없이 `csc.exe` 만으로 만든다. 본체 exe 를
`/resource:bin\OnTopPasskey.exe,payload` 로 설치 관리자 안에 넣고, 실행 시 리소스에서
꺼내 쓴다. 결과물은 단일 파일 하나.

- 매니페스트 `requireAdministrator` → 실행하면 곧바로 UAC
- `/silent`, `/uninstall`, `/lang:ko|en`, `/delay:N` 지원
- 제거 시 자기 자신을 임시 폴더로 복사해 그쪽에서 재실행 (설치 폴더 안에서 돌면
  자기 파일이 잠겨 폴더를 못 지움)

### 4-2. 버전 올릴 때 고쳐야 하는 곳 (6군데)

빠뜨리면 `package.ps1` 의 버전 가드가 잡아준다.

```
src\OnTopPasskey.cs   AssemblyVersion / AssemblyFileVersion
src\Setup.cs          AssemblyVersion / AssemblyFileVersion / Program.Version
src\app.manifest      assemblyIdentity version
src\setup.manifest    assemblyIdentity version
package.ps1           $version
```

### 4-3. 릴리스

```bash
powershell -ExecutionPolicy Bypass -File .\package.ps1
gh release create v1.0.4 "dist\OnTopPasskeySetup-1.0.4.exe" --title "OnTopPasskey 1.0.4" --notes "..."
```

`bin/` 과 `dist/` 는 `.gitignore` 에 있다. 설치 파일은 저장소가 아니라 Releases 에 올린다.

### 4-4. 아이콘

`assets\make-icon.ps1` 이 GDI+ 로 미어캣을 그려 멀티 사이즈 `.ico` 로 묶는다.
빌드 시 `/win32icon` 으로 exe 에 포함되므로 런타임에 아이콘 파일은 필요 없다.

- `Bitmap.GetHicon()` + `Icon.Save()` 경로는 **이미지를 16색으로 떨어뜨린다.**
  그래서 32비트 BGRA DIB 를 직접 써서 `.ico` 컨테이너를 조립한다.
- 128px 이상은 무압축 DIB 대신 PNG 로 넣는다 (안 그러면 파일이 374KB 가 된다)
- 트레이 아이콘은 `LoadImage(hInst, 32512, IMAGE_ICON, SM_CXSMICON, ...)` 로 로드.
  `csc /win32icon` 이 넣는 아이콘 그룹의 리소스 ID 가 32512 다.

---

## 5. 미해결 / 확인 대기

### 5-1. 프리징 체감 (미확인)

인증이 동작하긴 하는데 잠깐 멈추는 느낌이 있다는 보고. `AttachThreadInput` 호출
빈도를 줄이는 조치(2-8)를 넣었지만 **개선 여부를 확인받지 못했다.**

가르는 방법: 트레이에서 `활성화` 를 끄고 인증해 본다. 그 상태에서도 똑같이 느리면
이 프로그램과 무관한 Windows Hello 초기화 지연이다.

### 5-2. 부팅 시 네트워크 미연결 (원인 미확정)

설치 이후 부팅 시 인터넷이 안 잡히고 케이블을 뺐다 꽂으면 해결된다는 보고.

조사 결과:

- 프로그램이 쓰는 네이티브 DLL 은 `advapi32`, `kernel32`, `shell32`, `user32` **뿐**.
  네트워크 API 를 소스에도 컴파일된 바이너리에도 참조하지 않는다
- 빠른 시작(Fast Startup) 은 **꺼져 있음** → 흔한 원인 하나 배제
- 최근 3일 `Tcpip` / `DHCP` / `NDIS` / `Realtek` 오류 **0건**
- `Kernel-PnP 219` (WUDFRd 로드 실패) 는 매 부팅 발생하나 **설치 이전부터** 있던 현상
- 더 유력한 후보: **Surfshark VPN**(자동 시작 + OpenVPN DCO 어댑터),
  Hyper-V 가상 스위치, `Not Present` 상태인 Npcap Loopback Adapter

"케이블 재연결로 해결"은 VPN 킬 스위치/자동 연결이나 네트워크 프로필 감지(NLA)
재평가에서 흔한 패턴이다.

**아직 안 한 결정적 검증** — 관리자 PowerShell 에서 작업을 끄고 2~3회 부팅:

```powershell
Disable-ScheduledTask -TaskName OnTopPasskey   # 되돌리기: Enable-ScheduledTask
```

끈 상태에서도 재현되면 이 프로그램은 무관하다.

### 5-3. 그 밖에

- `LICENSE` 의 저작권자가 GitHub 계정명 `sallyHADEV` 로 되어 있다. 실명이나
  조직명을 쓸지 결정 필요
- README 에서 빌드 방법을 의도적으로 뺐다 (사용자용 문서로 정리). 기여자용으로
  다시 넣거나 `CONTRIBUTING.md` 로 분리할 수 있다
- 권한 상승이 불가능한 환경을 지원해야 한다면 **UIAccess** 경로가 남아 있다.
  다만 Authenticode 서명 + Program Files 설치가 필수라 서명 인증서가 없으면 불가능

---

## 6. 검증 방법 (다시 쓸 때 유용)

이 프로젝트는 추측 대신 계측으로 판단해 왔다. 쓰던 방법들:

| 목적 | 방법 |
|---|---|
| 강제 로직이 통하는지 | 트레이 → **동작 자가 진단**. 문자표(charmap)를 대상으로 시험 |
| 실제 인증 창의 상태 | `LogLevel=2` 로 두면 500ms 마다 `GetGUIThreadInfo` 로 전경/활성/포커스 기록 |
| 규칙이 안 맞을 때 | **진단 모드**(`SpyMode`)로 나타나는 모든 창의 class/title/proc 기록 |
| 설치 마법사 화면 확인 | `/define:PREVIEW` 로 빌드하면 권한 검사를 건너뛴다. 배포본에는 안 들어감 |
| 창 캡처 | `PrintWindow` 는 메뉴에 실패할 때가 있다. 그때는 `CopyFromScreen` |

**주의:** 트레이 메뉴를 합성 키 입력으로 조작하는 테스트는 하지 말 것. 실제로
`기록 안 함` 이 눌려 로그가 꺼지고 프로그램이 종료된 사고가 있었다.

### 6-1. 로그와 설정 위치

```
%LOCALAPPDATA%\OnTopPasskey\config.ini
%LOCALAPPDATA%\OnTopPasskey\log.txt
```

규칙 항목(`ClassContains` 등)을 하나라도 직접 적으면 기본 규칙 대신 파일의 규칙만
쓴다. 테스트할 때 `ProcessName=charmap.exe` 만 적어 두면 문자표로 전체 흐름을
재현할 수 있다.

---

## 7. 환경 메모

### 7-1. PowerShell 인코딩 — 반복해서 물린 함정

한글이 든 `.ps1` / `.txt` 는 **UTF-8 BOM** 으로 저장해야 한다. Windows PowerShell
5.1 은 BOM 없는 UTF-8 을 ANSI 로 읽어 파싱 자체가 깨진다 (`Unexpected token '}'`
같은 엉뚱한 오류가 난다). 실제로 세 번 물렸다.

```powershell
$c = Get-Content -LiteralPath $f -Raw -Encoding utf8
Set-Content -LiteralPath $f -Value $c -Encoding utf8BOM -NoNewline
```

`.bat` 은 반대로 BOM 이 있으면 첫 줄이 깨진다. 그래서 배치 파일은 ASCII 로만 썼다
(현재는 설치 마법사로 대체되어 배치 파일이 없다).

### 7-2. 언어 전환

`L.T("한국어", "English")` 로 코드 옆에 두 문장을 나란히 둔다. 리소스 어셈블리를
쓰지 않으므로 실행 파일이 하나로 유지된다. 기본은 `GetUserDefaultUILanguage()` 의
기본 언어 ID 가 `LANG_KOREAN(0x12)` 인지로 판별.

**주의:** 설정 파일의 `Language` 는 **파싱이 끝난 뒤** 다시 적용해야 한다. 파싱 전에만
적용해서 `Language=en` 이 무시되던 버그가 있었다.

### 7-3. 개발 PC 상태 (2026-08-04 기준)

- git 2.54, GitHub CLI 2.97 설치됨. `gh` 는 `sallyHADEV` 계정으로 로그인됨
- 커밋 작성자는 **이 저장소에만** 로컬 설정 (전역 설정은 건드리지 않음)
  `sallyHADEV <127032738+sallyHADEV@users.noreply.github.com>`
- .NET SDK 없음. `csc.exe` 만으로 빌드하는 구조라 필요 없다
- 검증 환경: Windows 11 Pro 26200 (25H2), 64비트

---

## 8. 이력

| 버전 | 날짜 | 내용 |
|---|---|---|
| 1.0.0 | 08-02 | 최초. 감지 성공, 그러나 일반 권한이라 UIPI 로 차단됨 |
| 1.0.1 | 08-02 | 소유자 체인 버그 수정(2-5), 재시도 구조 개편, 로그 기록 수준 3단계 |
| 1.0.2 | 08-04 | 한국어/영어 자동 전환. 설치 마법사 exe 로 배포 방식 변경 |
| 1.0.3 | 08-04 | 로그온 후 45초 지연 실행 |

커밋 이력은 `git log` 로 볼 것. 저장소: https://github.com/sallyHADEV/OnTopPasskey

---

## 9. 다시 시작할 때

```bash
git clone https://github.com/sallyHADEV/OnTopPasskey.git
cd OnTopPasskey
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

읽는 순서는 이 문서 → `src\OnTopPasskey.cs` 의 헤더 주석 → `Force.Foreground()` →
`App.RetryTick()` 순이 빠르다. 그 세 곳에 이 프로젝트에서 어렵게 얻은 것이 모여 있다.

가장 먼저 확인할 것은 **5-1(프리징)** 과 **5-2(부팅 네트워크)** 두 가지다.
