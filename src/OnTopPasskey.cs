// OnTopPasskey - Windows Hello / 패스키 인증 창을 항상 위로 + 포커스 강제
//
// [문제]
//   Windows 11 에서 "Windows 보안" 자격 증명 창(지문/PIN/패스키)이 브라우저 창 뒤에
//   생성되고 포커스도 받지 못하는 경우가 간헐적으로 발생한다. 이 상태에서는 지문
//   센서에 손을 올려도 입력이 무시된다. (Windows 10 에는 없던 회귀 현상)
//
// [해결]
//   그 창이 만들어지는 순간을 감지해 즉시 TOPMOST 로 올리고 포그라운드/포커스를
//   강제로 가져온다.
//
// [대상 창 식별 근거]
//   최상위 창 클래스명 = "Credential Dialog Xaml Host" (CredentialUIBroker.exe).
//   제목은 로케일마다 다르므로("Windows 보안"/"Windows Security") 클래스명이 1순위,
//   제목/프로세스명은 보조 신호로만 쓴다.
//
// [경량화 설계]
//   * 순수 Win32 만 사용 - WinForms / System.Drawing / System.Diagnostics 미사용.
//     런타임에 로드되는 관리 어셈블리는 mscorlib 하나뿐이다.
//   * 주 감지기는 셸 훅(RegisterShellHookWindow). 최상위 창이 새로 만들어질 때만
//     메시지가 오므로 평상시 비용이 사실상 0 이다.
//   * 상시 폴링 없음. 유휴 상태에서 동작 중인 타이머 0개, CPU 0%.
//     대기 중에는 GetMessage 에서 블록되어 스레드가 잠들어 있다.
//   * 재시도 타이머는 인증 창이 떠 있는 동안에만 켰다가 닫히면 즉시 KillTimer.
//   * 문자열 버퍼 재사용 + PID→프로세스명 캐시로 이벤트당 힙 할당을 최소화.
//
// 빌드: build.ps1 (.NET Framework 4.x csc.exe, SDK 불필요)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("OnTopPasskey")]
// 파일 속성에 박히는 값은 실행 중 바꿀 수 없으므로 영어로 둔다.
[assembly: AssemblyDescription("Keeps the Windows Hello / passkey prompt on top and focused")]
[assembly: AssemblyProduct("OnTopPasskey")]
[assembly: AssemblyVersion("1.0.5.0")]
[assembly: AssemblyFileVersion("1.0.5.0")]

namespace OnTopPasskey
{
    internal static class App
    {
        private const string WndClassName = "OnTopPasskey.HiddenWnd";
        private const int TrayIconId = 1;

        private const int WM_APP_TRAY = Native.WM_APP + 1;
        private const int TIMER_RETRY = 1;
        private const int TIMER_SAFETY = 2;

        private const int CMD_ENABLED = 100;
        private const int CMD_SPY = 101;
        private const int CMD_STARTUP = 102;
        private const int CMD_LOG = 103;
        private const int CMD_CONFIG = 104;
        private const int CMD_RELOAD = 105;
        private const int CMD_TEST = 106;
        private const int CMD_ELEVATE = 107;
        private const int CMD_EXIT = 108;
        private const int CMD_LOG_OFF = 110;
        private const int CMD_LOG_NORMAL = 111;
        private const int CMD_LOG_VERBOSE = 112;
        private const int CMD_LOG_CLEAR = 113;
        private const int CMD_HELP_SHOW = 120;
        private const int CMD_HELP_EDIT = 121;
        private const int CMD_FILL_WINDOW = 122;

        // 1초 주기로 천천히 당겨온다. 감지 즉시 잡지 않고 첫 틱(1초 뒤)에 처음 강제한다.
        private const int RetryIntervalMs = 1000;
        private const int DenialsBeforeGiveUp = 5;      // UIPI 거부가 이만큼 이어지면 포기

        // 생성 직후 예약 재시도 틱. XAML 호스트는 내용이 비동기로 올라오므로
        // 창이 만들어진 순간 한 번 잡는 것만으로는 내부 콘텐츠에 포커스가 들어가지 않는다.
        private static readonly int[] ScheduledForceTicks = { 1, 2, 3 };   // 1 / 2 / 3초

        // 포커스를 빼앗겼을 때 다시 잡기까지의 최소 간격(틱).
        // AttachThreadInput 은 두 스레드의 입력 큐를 묶으므로 너무 자주 하면
        // 입력이 직렬화되어 끊기는 느낌을 준다. 그래서 간격을 둔다.
        private const int RegrabIntervalTicks = 1;      // 1초

        // --------------------------------------------------------------- 상태

        private static IntPtr _hwnd;
        internal static IntPtr Hwnd { get { return _hwnd; } }
        private static Native.WndProcDelegate _wndProc;       // GC 방지용 고정 참조
        private static Native.WinEventProc _winEventProc;     // GC 방지용 고정 참조
        private static readonly List<IntPtr> Hooks = new List<IntPtr>(3);
        private static uint _msgTaskbarCreated;
        private static uint _msgShellHook;

        private static bool _enabled = true;
        private static bool _retryTimerOn;
        private static bool _safetyTimerOn;

        /// <summary>추적 중인 인증 창 하나의 상태. 보통 0~1개만 존재한다.</summary>
        private sealed class Track
        {
            public int Ticks;          // 감지 후 경과한 100ms 틱 수
            public int LastForceTick;  // 마지막으로 강제를 시도한 틱
            public int Denials;        // UIPI 연속 거부 횟수
            public bool EverForeground;
        }

        private static readonly Dictionary<IntPtr, Track> Tracked = new Dictionary<IntPtr, Track>(2);
        private static readonly List<IntPtr> WasNotTopmost = new List<IntPtr>(8);
        private static readonly List<IntPtr> DiagDone = new List<IntPtr>(8);
        private static bool _privilegeWarned;

        private static readonly StringBuilder ClassBuf = new StringBuilder(128);
        private static readonly StringBuilder TitleBuf = new StringBuilder(160);

        // ------------------------------------------------------------- 진입점

        [STAThread]
        private static int Main(string[] args)
        {
            bool spy = false, replace = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--spy", StringComparison.OrdinalIgnoreCase)) spy = true;
                if (string.Equals(args[i], "--replace", StringComparison.OrdinalIgnoreCase)) replace = true;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "OnTopPasskey.SingleInstance", out createdNew))
            {
                // --replace: 권한 상승 재실행 시 이전 인스턴스가 종료될 때까지 잠시 기다린다.
                if (!createdNew && replace)
                {
                    try { createdNew = mutex.WaitOne(5000); }
                    catch (AbandonedMutexException) { createdNew = true; }
                }

                if (!createdNew)
                {
                    Native.MessageBox(IntPtr.Zero,
                        L.T("OnTopPasskey 가 이미 실행 중입니다.",
                            "OnTopPasskey is already running."),
                        "OnTopPasskey", Native.MB_ICONINFORMATION);
                    return 0;
                }

                Config.Load();
                if (spy) Config.SpyMode = true;

                if (!CreateHiddenWindow()) return 1;
                AddTrayIcon();
                InstallDetectors();
                if (Config.SafetyPollMs > 0) StartSafetyTimer();
                if (!Native.RegisterHotKey(_hwnd, MouseHelp.HotkeyId, MouseHelp.HotkeyMods, MouseHelp.HotkeyVk))
                    Log.Write(L.T("마우스 도움말 단축키 등록 실패 (다른 프로그램이 사용 중): ",
                                  "Mouse help shortcut is taken by another program: ") + MouseHelp.HotkeyText);
                if (!Native.RegisterHotKey(_hwnd, WindowFill.HotkeyId, WindowFill.HotkeyMods, WindowFill.HotkeyVk))
                    Log.Write(L.T("창 채우기 단축키 등록 실패 (다른 프로그램이 사용 중): ",
                                  "Window fill shortcut is taken by another program: ") + WindowFill.HotkeyText);

                Log.Write(L.T("=== 시작 ===  ", "=== start ===  ") + Diag.SelfDescription()
                          + "  logLevel=" + Config.LogLevel
                          + "  spy=" + Config.SpyMode
                          + "  objectShowHook=" + Config.UseObjectShowHook
                          + "  safetyPoll=" + Config.SafetyPollMs + "ms");
                Mem.Trim();

                int rc = MessageLoop();

                WindowFill.Shutdown();
                Native.UnregisterHotKey(_hwnd, WindowFill.HotkeyId);
                Native.UnregisterHotKey(_hwnd, MouseHelp.HotkeyId);
                RemoveDetectors();
                RemoveTrayIcon();
                Log.Write(L.T("=== 종료 ===", "=== exit ==="));
                return rc;
            }
        }

        private static int MessageLoop()
        {
            Native.MSG msg;
            while (true)
            {
                int r = Native.GetMessage(out msg, IntPtr.Zero, 0, 0);
                if (r == 0) return (int)msg.wParam;   // WM_QUIT
                if (r == -1) return 1;
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }

        // ----------------------------------------------------------- 숨김 창

        private static bool CreateHiddenWindow()
        {
            _wndProc = WndProc;

            var wc = new Native.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
                lpfnWndProc = _wndProc,
                hInstance = Native.GetModuleHandle(null),
                lpszClassName = WndClassName,
            };

            if (Native.RegisterClassEx(ref wc) == 0)
            {
                Log.Write(L.T("RegisterClassEx 실패: ", "RegisterClassEx failed: ")
                          + Marshal.GetLastWin32Error());
                return false;
            }

            // 셸 훅 메시지는 메시지 전용(HWND_MESSAGE) 창에는 전달되지 않을 수 있으므로
            // 실제 최상위 창을 만들되, 보이지 않고 작업 표시줄/Alt-Tab 에도 뜨지 않게 한다.
            _hwnd = Native.CreateWindowEx(
                Native.WS_EX_TOOLWINDOW, WndClassName, "OnTopPasskey",
                Native.WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Log.Write(L.T("CreateWindowEx 실패: ", "CreateWindowEx failed: ")
                          + Marshal.GetLastWin32Error());
                return false;
            }

            _msgTaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
            return true;
        }

        // ------------------------------------------------------------- 감지기

        private static void InstallDetectors()
        {
            // (1) 주 감지기: 셸 훅. 최상위 창 생성/활성화 시에만 메시지가 온다.
            _msgShellHook = Native.RegisterWindowMessage("SHELLHOOK");
            if (!Native.RegisterShellHookWindow(_hwnd))
                Log.Write(L.T("경고: RegisterShellHookWindow 실패 - 이벤트 훅만으로 동작합니다.",
                              "Warning: RegisterShellHookWindow failed - falling back to event hooks only."));

            // (2) 보조 감지기: 저빈도 시스템 이벤트 2종.
            _winEventProc = OnWinEvent;
            AddHook(Native.EVENT_SYSTEM_DIALOGSTART, Native.EVENT_SYSTEM_DIALOGSTART);
            AddHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND);

            // (3) 선택: 모든 창 표시 이벤트. 감지율이 가장 높지만 콜백 빈도도 가장 높다.
            if (Config.UseObjectShowHook)
                AddHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW);

            // (4) 창 드래그 이동 감지 (Alt+드래그 또는 허용 앱 자동 채우기)
            AddHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND);
        }

        private static void AddHook(uint min, uint max)
        {
            IntPtr h = Native.SetWinEventHook(min, max, IntPtr.Zero, _winEventProc, 0, 0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            if (h == IntPtr.Zero)
                Log.Write(L.T("훅 설치 실패: 0x", "Hook install failed: 0x") + min.ToString("X4"));
            else Hooks.Add(h);
        }

        private static void RemoveDetectors()
        {
            Native.DeregisterShellHookWindow(_hwnd);
            for (int i = 0; i < Hooks.Count; i++) Native.UnhookWinEvent(Hooks[i]);
            Hooks.Clear();
        }

        private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint thread, uint time)
        {
            // 가장 싼 1차 필터: 창 자체에 대한 이벤트가 아니면 즉시 반려한다.
            if (idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF) return;
            if (hwnd == IntPtr.Zero) return;

            if (eventType == Native.EVENT_SYSTEM_MOVESIZESTART)
            {
                WindowFill.OnMoveSizeStart(hwnd);
                return;
            }
            if (eventType == Native.EVENT_SYSTEM_MOVESIZEEND)
            {
                WindowFill.OnMoveSizeEnd(hwnd);
                return;
            }

            Consider(hwnd, "winevent");
        }

        // ------------------------------------------------------- 창 프로시저

        private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_msgShellHook != 0 && msg == _msgShellHook)
            {
                int ev = wParam.ToInt32() & 0x7FFF;   // RUDEAPPACTIVATED 의 0x8000 비트 제거
                if (ev == Native.HSHELL_WINDOWCREATED || ev == Native.HSHELL_WINDOWACTIVATED)
                {
                    if (lParam != IntPtr.Zero) Consider(lParam, "shellhook");
                }
                return IntPtr.Zero;
            }

            if (msg == WM_APP_TRAY)
            {
                int ev = (int)(lParam.ToInt64() & 0xFFFF);
                if (ev == Native.WM_RBUTTONUP || ev == Native.WM_LBUTTONUP || ev == Native.WM_CONTEXTMENU)
                    ShowMenu();
                return IntPtr.Zero;
            }

            if (msg == Native.WM_HOTKEY)
            {
                int hk = wParam.ToInt32();
                if (hk == MouseHelp.HotkeyId) RunHelp(false);
                else if (hk == WindowFill.HotkeyId) RunFill();
                return IntPtr.Zero;
            }

            if (msg == Native.WM_TIMER)
            {
                int id = wParam.ToInt32();
                if (id == TIMER_RETRY) RetryTick();
                else if (id == TIMER_SAFETY) SafetyScan();
                else if (id == WindowFill.PreviewTimerId) WindowFill.PreviewTick();
                return IntPtr.Zero;
            }

            if (msg == Native.WM_DESTROY)
            {
                WindowFill.Shutdown();
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
            }

            if (_msgTaskbarCreated != 0 && msg == _msgTaskbarCreated)
            {
                AddTrayIcon();   // 탐색기 재시작 시 트레이 아이콘 복구
                return IntPtr.Zero;
            }

            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        // -------------------------------------------------------- 판별과 강제

        private static void Consider(IntPtr hwnd, string source)
        {
            if (hwnd == _hwnd || WindowFill.IsPreviewWindow(hwnd)) return;
            if (!Native.IsWindowVisible(hwnd)) return;

            // 2차 필터: 클래스명 (커널 호출 1회, 매우 저렴)
            ClassBuf.Length = 0;
            int n = Native.GetClassName(hwnd, ClassBuf, ClassBuf.Capacity);
            string cls = n > 0 ? ClassBuf.ToString() : string.Empty;

            bool match = Rules.ClassMatches(cls);

            // 3차 필터: 제목 / 프로세스명 (클래스가 안 맞을 때만 평가)
            string title = null;
            if (!match)
            {
                title = GetTitle(hwnd);
                match = Rules.TitleMatches(title);
            }
            if (!match && Config.ProcessNames.Count > 0)
                match = Rules.ProcessMatches(ProcessNameOf(hwnd));

            if (Config.SpyMode)
            {
                if (title == null) title = GetTitle(hwnd);
                if (match || title.Length > 0)
                    Log.Write(L.T("[진단] ", "[spy] ") + "class=\"" + cls + "\" title=\"" + title + "\" proc="
                              + ProcessNameOf(hwnd) + " src=" + source
                              + (match ? L.T("  <== 일치", "  <== match") : ""));
            }

            if (!match || !_enabled) return;

            // 이미 추적 중이면 다시 강제하지 않는다. 강제 시점은 재시도 루프가 관리한다.
            if (Tracked.ContainsKey(hwnd)) return;

            Tracked[hwnd] = new Track();
            // 원래 '항상 위'가 아니었다면 창이 사라질 때 되돌릴 수 있도록 기억해 둔다.
            if (!Force.IsTopmost(hwnd) && !WasNotTopmost.Contains(hwnd))
            {
                if (WasNotTopmost.Count >= 8) WasNotTopmost.RemoveAt(0);
                WasNotTopmost.Add(hwnd);
            }

            if (title == null) title = GetTitle(hwnd);
            Log.Write(L.T("[감지] ", "[detect] ") + "class=\"" + cls + "\" title=\"" + title
                      + "\" src=" + source
                      + " hwnd=0x" + hwnd.ToInt64().ToString("X")
                      + " owner=0x" + Native.GetWindow(hwnd, Native.GW_OWNER).ToInt64().ToString("X"));
            if (!DiagDone.Contains(hwnd))
            {
                if (DiagDone.Count >= 8) DiagDone.RemoveAt(0);
                DiagDone.Add(hwnd);
                Log.Write(L.T("[계측] ", "[probe] ") + Diag.Target(hwnd));
            }
            StartRetryTimer();
        }

        /// <summary>
        /// UIPI 차단을 만났을 때의 안내. 자격 증명 UI 는 표준 Medium(8192)보다 살짝 높은
        /// 무결성 수준으로 실행되므로, 같은 계정의 일반 프로세스로는 창을 조작할 수 없다.
        /// High(12288)로 올리면 넘어선다.
        /// </summary>
        private static void WarnPrivilege(IntPtr hwnd)
        {
            int self = Diag.SelfRid;
            int target = Diag.RidOfWindow(hwnd);
            Log.Write(L.T("[권한] UIPI 차단 - self IL=", "[privilege] Blocked by UIPI - self IL=")
                      + Diag.Describe(self) + " < target IL=" + Diag.Describe(target)
                      + L.T(" 이라 창 조작이 거부되었습니다(ERROR_ACCESS_DENIED). "
                            + "트레이 메뉴의 '관리자 권한으로 다시 실행' 을 쓰거나 "
                            + "설치판을 쓰면 로그온 시 최고 권한으로 실행됩니다.",
                            ", so window manipulation was denied (ERROR_ACCESS_DENIED). "
                            + "Use 'Restart as administrator' in the tray menu, or install the "
                            + "packaged build, which runs with highest privileges at logon."));

            if (_privilegeWarned) return;
            _privilegeWarned = true;
            ShowBalloon(L.T("권한이 부족합니다", "Insufficient privileges"),
                L.T("인증 창이 더 높은 무결성 수준(" + Diag.Describe(target) + ")으로 실행 중이라\r\n"
                    + "일반 권한으로는 앞으로 가져올 수 없습니다.\r\n"
                    + "트레이 아이콘 우클릭 → '관리자 권한으로 다시 실행' 을 선택하세요.",
                    "The prompt runs at a higher integrity level (" + Diag.Describe(target) + "),\r\n"
                    + "so it cannot be brought forward without elevation.\r\n"
                    + "Right-click the tray icon and choose 'Restart as administrator'."));
        }

        private static string GetTitle(IntPtr hwnd)
        {
            int len = Native.GetWindowTextLength(hwnd);
            if (len <= 0) return string.Empty;
            if (TitleBuf.Capacity < len + 1) TitleBuf.Capacity = len + 1;
            TitleBuf.Length = 0;
            int n = Native.GetWindowText(hwnd, TitleBuf, TitleBuf.Capacity);
            return n > 0 ? TitleBuf.ToString() : string.Empty;
        }

        private static readonly Dictionary<uint, string> PidCache = new Dictionary<uint, string>(32);

        internal static string ProcessNameOf(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return string.Empty;

            string name;
            if (PidCache.TryGetValue(pid, out name)) return name;

            name = string.Empty;
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(320);
                    int cap = sb.Capacity;
                    if (Native.QueryFullProcessImageName(h, 0, sb, ref cap))
                    {
                        string full = sb.ToString();
                        int slash = full.LastIndexOf('\\');
                        name = slash >= 0 ? full.Substring(slash + 1) : full;
                    }
                }
                finally { Native.CloseHandle(h); }
            }

            if (PidCache.Count > 256) PidCache.Clear();   // PID 재사용 대비
            PidCache[pid] = name;
            return name;
        }

        // -------------------------------------------------------------- 타이머

        private static void StartRetryTimer()
        {
            if (_retryTimerOn) return;
            Native.SetTimer(_hwnd, new IntPtr(TIMER_RETRY), RetryIntervalMs, IntPtr.Zero);
            _retryTimerOn = true;
        }

        private static void StopRetryTimer()
        {
            if (!_retryTimerOn) return;
            Native.KillTimer(_hwnd, new IntPtr(TIMER_RETRY));
            _retryTimerOn = false;
            foreach (var h in Tracked.Keys) RestoreTopmost(h);
            Tracked.Clear();
            Mem.Trim();
        }

        private static void StartSafetyTimer()
        {
            StopSafetyTimer();
            if (Config.SafetyPollMs <= 0) return;
            Native.SetTimer(_hwnd, new IntPtr(TIMER_SAFETY), Config.SafetyPollMs, IntPtr.Zero);
            _safetyTimerOn = true;
        }

        private static void StopSafetyTimer()
        {
            if (!_safetyTimerOn) return;
            Native.KillTimer(_hwnd, new IntPtr(TIMER_SAFETY));
            _safetyTimerOn = false;
        }

        /// <summary>
        /// 인증 창이 떠 있는 동안에만 도는 루프. 창이 닫힐 때까지 유지한다.
        ///
        /// 강제 시점을 세 가지로 나눈다.
        ///   * 예약 재시도 - XAML 콘텐츠가 올라온 뒤 내부 포커스가 잡히도록 0.2~2.5초 사이 몇 번
        ///   * 재탈취      - 포커스를 빼앗겼을 때. 단 AttachThreadInput 이 입력을 직렬화하므로
        ///                   최소 간격(500ms)을 두어 끊김을 만들지 않는다
        ///   * 항상 위 유지 - 포그라운드일 때는 입력 큐를 건드리지 않고 Z-order 만 손본다
        /// </summary>
        private static void RetryTick()
        {
            if (Tracked.Count == 0) { StopRetryTimer(); return; }

            IntPtr fg = Native.GetForegroundWindow();
            var keys = new IntPtr[Tracked.Count];
            Tracked.Keys.CopyTo(keys, 0);
            List<IntPtr> dead = null;

            for (int i = 0; i < keys.Length; i++)
            {
                IntPtr h = keys[i];
                Track t = Tracked[h];

                if (!Native.IsWindow(h) || !Native.IsWindowVisible(h))
                {
                    Log.Write(L.T("[종료] 인증 창이 닫혔습니다. 유지 시간 ",
                                  "[closed] Prompt closed. Open for ")
                              + (t.Ticks * RetryIntervalMs) + "ms"
                              + (t.EverForeground
                                 ? L.T(", 포그라운드 확보 됨", ", foreground was acquired")
                                 : L.T(", 포그라운드 확보 안 됨", ", foreground was never acquired")));
                    RestoreTopmost(h);
                    (dead ?? (dead = new List<IntPtr>(1))).Add(h);
                    continue;
                }

                t.Ticks++;
                bool isFg = fg == h;
                if (isFg) t.EverForeground = true;

                if (Config.LogLevel >= Config.LogVerbose && Config.TraceMs > 0
                    && (t.Ticks * RetryIntervalMs) % Config.TraceMs == 0)
                    Log.Write(L.T("[추적] +", "[trace] +") + (t.Ticks * RetryIntervalMs) + "ms  "
                              + Diag.InputState(h));

                bool scheduled = Array.IndexOf(ScheduledForceTicks, t.Ticks) >= 0;
                bool lost = !isFg && (t.Ticks - t.LastForceTick) >= RegrabIntervalTicks;

                if (scheduled || lost)
                {
                    t.LastForceTick = t.Ticks;
                    Force.Result rr = Force.Foreground(h);
                    if (rr.GotForeground) t.EverForeground = true;
                    if (t.Ticks == 1) Log.Write(L.T("[강제] ", "[force] ") + rr);

                    // UIPI 로 막힌 창은 계속 두드려도 소용없다. 몇 번 확인 후 포기한다.
                    if (rr.IntegrityBlocked)
                    {
                        t.Denials++;
                        if (t.Denials == 1) WarnPrivilege(h);
                        if (t.Denials >= DenialsBeforeGiveUp)
                        {
                            Log.Write(L.T("[권한] " + t.Denials + "회 거부되어 이 창에 대한 재시도를 중단합니다.",
                                          "[privilege] Denied " + t.Denials
                                          + " times; giving up on this window."));
                            (dead ?? (dead = new List<IntPtr>(1))).Add(h);
                            continue;
                        }
                    }
                    else t.Denials = 0;
                }
                else if (isFg && Config.KeepTopmost && !Force.IsTopmost(h))
                {
                    // 입력 큐를 붙이지 않는 가벼운 경로
                    Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                }
            }

            if (dead != null)
                for (int i = 0; i < dead.Count; i++) Tracked.Remove(dead[i]);

            if (Tracked.Count == 0) StopRetryTimer();
        }

        /// <summary>
        /// 원래 '항상 위'가 아니었던 창이라면 그 상태를 되돌린다.
        /// 인증 창은 보통 곧 사라지지만, 혹시라도 다른 창에 TOPMOST 가 눌러붙는 일이
        /// 없도록 반드시 원복한다.
        /// </summary>
        private static void RestoreTopmost(IntPtr hwnd)
        {
            if (!WasNotTopmost.Remove(hwnd)) return;
            if (!Native.IsWindow(hwnd)) return;
            if (!Force.IsTopmost(hwnd)) return;

            Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Log.Write(L.T("[원복] 항상 위 해제 hwnd=0x", "[restore] Cleared topmost, hwnd=0x")
                      + hwnd.ToInt64().ToString("X"));
        }

        /// <summary>선택적 안전망 스캔 (기본 비활성).</summary>
        private static void SafetyScan()
        {
            if (!_enabled) return;
            Native.EnumWindows(SafetyEnumProc, IntPtr.Zero);
        }

        private static readonly Native.EnumWindowsProc SafetyEnumProc = (h, l) =>
        {
            Consider(h, "safety");
            return true;
        };

        // ------------------------------------------------------------- 트레이

        private static IntPtr _trayIcon;

        /// <summary>exe 에 포함된 미어캣 아이콘을 트레이 크기에 맞춰 한 번만 로드한다.</summary>
        private static IntPtr TrayIcon()
        {
            if (_trayIcon != IntPtr.Zero) return _trayIcon;

            // csc /win32icon 으로 넣은 아이콘 그룹의 리소스 ID 는 32512 다.
            _trayIcon = Native.LoadImage(Native.GetModuleHandle(null), new IntPtr(32512),
                Native.IMAGE_ICON,
                Native.GetSystemMetrics(Native.SM_CXSMICON),
                Native.GetSystemMetrics(Native.SM_CYSMICON),
                Native.LR_DEFAULTCOLOR | Native.LR_SHARED);

            if (_trayIcon == IntPtr.Zero)   // 아이콘 리소스가 없을 때의 대비책
                _trayIcon = Native.LoadIcon(IntPtr.Zero, new IntPtr(Native.IDI_APPLICATION));

            return _trayIcon;
        }

        private static Native.NOTIFYICONDATA MakeNid()
        {
            return new Native.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA)),
                hWnd = _hwnd,
                uID = TrayIconId,
                uFlags = Native.NIF_ICON | Native.NIF_MESSAGE | Native.NIF_TIP,
                uCallbackMessage = WM_APP_TRAY,
                hIcon = TrayIcon(),
                szTip = "OnTopPasskey - " + (_enabled
                    ? L.T("감시 중", "Watching")
                    : L.T("일시 중지", "Paused")),
            };
        }

        /// <summary>풍선 알림. 권한 부족처럼 사용자가 반드시 알아야 할 때만 쓴다.</summary>
        private static void ShowBalloon(string title, string text)
        {
            var nid = new Native.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA)),
                hWnd = _hwnd,
                uID = TrayIconId,
                uFlags = Native.NIF_INFO,
                szInfoTitle = title,
                szInfo = text,
                dwInfoFlags = Native.NIIF_WARNING,
            };
            Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref nid);
        }

        private static void AddTrayIcon()
        {
            var nid = MakeNid();
            Native.Shell_NotifyIcon(Native.NIM_ADD, ref nid);
        }

        private static void UpdateTrayIcon()
        {
            var nid = MakeNid();
            Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref nid);
        }

        private static void RemoveTrayIcon()
        {
            var nid = new Native.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA)),
                hWnd = _hwnd,
                uID = TrayIconId,
            };
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref nid);
        }

        private static void ShowMenu()
        {
            IntPtr menu = Native.CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                Native.AppendMenu(menu, Native.MF_STRING | (_enabled ? Native.MF_CHECKED : 0),
                    new IntPtr(CMD_ENABLED), L.T("활성화 (인증 창 앞으로 가져오기)",
                                                 "Enabled (bring the prompt forward)"));
                // 로그 관련 항목은 하위 메뉴로 묶는다.
                IntPtr logMenu = Native.CreatePopupMenu();
                Native.AppendMenu(logMenu, Native.MF_STRING
                    | (Config.LogLevel == Config.LogOff ? Native.MF_CHECKED : 0),
                    new IntPtr(CMD_LOG_OFF), L.T("기록 안 함", "Off"));
                Native.AppendMenu(logMenu, Native.MF_STRING
                    | (Config.LogLevel == Config.LogNormal ? Native.MF_CHECKED : 0),
                    new IntPtr(CMD_LOG_NORMAL), L.T("기본 (감지와 처리 결과만)",
                                                    "Normal (detections and outcome only)"));
                Native.AppendMenu(logMenu, Native.MF_STRING
                    | (Config.LogLevel >= Config.LogVerbose ? Native.MF_CHECKED : 0),
                    new IntPtr(CMD_LOG_VERBOSE), L.T("자세히 (입력 상태 추적까지)",
                                                     "Verbose (with input-state trace)"));
                Native.AppendMenu(logMenu, Native.MF_SEPARATOR, IntPtr.Zero, null);
                Native.AppendMenu(logMenu, Native.MF_STRING
                    | (Config.SpyMode ? Native.MF_CHECKED : 0),
                    new IntPtr(CMD_SPY), L.T("진단 모드 (모든 창 정보 기록)",
                                             "Diagnostic mode (log every window)"));
                Native.AppendMenu(logMenu, Native.MF_SEPARATOR, IntPtr.Zero, null);
                Native.AppendMenu(logMenu, Native.MF_STRING, new IntPtr(CMD_LOG),
                    L.T("로그 열기", "Open log"));
                Native.AppendMenu(logMenu, Native.MF_STRING, new IntPtr(CMD_LOG_CLEAR),
                    L.T("로그 비우기", "Clear log"));
                Native.AppendMenu(menu, Native.MF_STRING | Native.MF_POPUP, logMenu,
                    L.T("로그", "Logging"));

                // 설치판은 작업 스케줄러가 자동 실행을 관리한다. 이 항목까지 켜면 중복 실행된다.
                if (!Startup.IsInstalledBuild())
                    Native.AppendMenu(menu, Native.MF_STRING | (Startup.IsEnabled() ? Native.MF_CHECKED : 0),
                        new IntPtr(CMD_STARTUP), L.T("Windows 시작 시 자동 실행", "Start with Windows"));

                Native.AppendMenu(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
                Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(CMD_FILL_WINDOW),
                    L.T("창 빈 공간 채우기	", "Fill window into empty space	") + WindowFill.HotkeyText);
                Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(CMD_HELP_SHOW),
                    L.T("마우스 도움말 보기	", "Show mouse help	") + MouseHelp.HotkeyText);
                Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(CMD_HELP_EDIT),
                    L.T("마우스 도움말 편집...", "Edit mouse help..."));

                Native.AppendMenu(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
                if (Diag.SelfRid < Native.SECURITY_MANDATORY_HIGH_RID)
                    Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(CMD_ELEVATE),
                        L.T("관리자 권한으로 다시 실행 (권한 부족 시)",
                            "Restart as administrator (if blocked)"));
                Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(CMD_TEST),
                    L.T("동작 자가 진단 (문자표로 시험)", "Self test (uses Character Map)"));
                Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(CMD_CONFIG),
                    L.T("설정 파일 열기", "Open settings file"));
                Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(CMD_RELOAD),
                    L.T("설정 다시 읽기", "Reload settings"));
                Native.AppendMenu(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
                Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(CMD_EXIT), L.T("종료", "Exit"));

                Native.POINT pt;
                Native.GetCursorPos(out pt);
                IntPtr prevForeground = Native.GetForegroundWindow();
                Native.SetForegroundWindow(_hwnd);   // 메뉴가 즉시 닫히는 문제 방지

                int cmd = Native.TrackPopupMenu(menu,
                    Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY,
                    pt.x, pt.y, 0, _hwnd, IntPtr.Zero);

                if (cmd != 0) OnCommand(cmd, prevForeground);
            }
            finally
            {
                Native.DestroyMenu(menu);
                Mem.Trim();
            }
        }

        private static void OnCommand(int cmd, IntPtr targetHwnd)
        {
            switch (cmd)
            {
                case CMD_ENABLED:
                    _enabled = !_enabled;
                    if (!_enabled) StopRetryTimer();
                    UpdateTrayIcon();
                    Log.Write(L.T("활성화 = ", "Enabled = ") + _enabled);
                    break;

                case CMD_LOG_OFF: SetLogLevel(Config.LogOff); break;
                case CMD_LOG_NORMAL: SetLogLevel(Config.LogNormal); break;
                case CMD_LOG_VERBOSE: SetLogLevel(Config.LogVerbose); break;

                case CMD_LOG_CLEAR:
                    Log.Clear();
                    Log.Write(L.T("로그를 비웠습니다.", "Log cleared."));
                    break;

                case CMD_SPY:
                    Config.SpyMode = !Config.SpyMode;
                    // 진단 모드는 기록이 켜져 있어야 의미가 있다.
                    if (Config.SpyMode && Config.LogLevel == Config.LogOff)
                        Config.LogLevel = Config.LogVerbose;
                    Config.Save();
                    Log.Write(L.T("진단 모드 = ", "Diagnostic mode = ") + Config.SpyMode);
                    break;

                case CMD_STARTUP:
                    try { Startup.Set(!Startup.IsEnabled()); }
                    catch (Exception ex)
                    {
                        Native.MessageBox(_hwnd,
                            L.T("자동 실행 설정 실패: ", "Could not change auto-start: ") + ex.Message,
                            "OnTopPasskey", Native.MB_ICONWARNING);
                    }
                    break;

                case CMD_ELEVATE: Elevate(); break;
                case CMD_TEST: SelfTest.Run(_hwnd); break;
                case CMD_FILL_WINDOW:
                    WindowFill.Fill(WindowFill.MenuTarget(targetHwnd), true);
                    break;
                case CMD_HELP_SHOW: RunHelp(false); break;
                case CMD_HELP_EDIT: RunHelp(true); break;
                case CMD_LOG: Shell.OpenInNotepad(Log.FilePath); break;
                case CMD_CONFIG: Shell.OpenInNotepad(Config.ConfigPath); break;

                case CMD_RELOAD:
                    Config.Load();
                    if (Config.SafetyPollMs > 0) StartSafetyTimer(); else StopSafetyTimer();
                    Log.Write(L.T("설정 다시 읽음", "Settings reloaded"));
                    break;

                case CMD_EXIT:
                    Native.PostQuitMessage(0);
                    break;
            }
        }

        private static void RunFill()
        {
            WindowFill.Fill(Native.GetForegroundWindow(), true, true);
        }

        /// <summary>도움말 패널의 예외가 본체(인증 창 감시)를 죽이지 않게 막는다.</summary>
        private static void RunHelp(bool edit)
        {
            try { if (edit) MouseHelp.Edit(); else MouseHelp.Toggle(); }
            catch (Exception ex) { Log.WriteAlways(L.T("마우스 도움말 오류: ", "Mouse help error: ") + ex); }
        }

        /// <summary>
        /// 기록 수준을 바꾼다. 끄고 켜는 순간만은 수준과 무관하게 한 줄 남겨,
        /// 로그가 비어 있는 이유를 나중에 알 수 있게 한다.
        /// </summary>
        private static void SetLogLevel(int level)
        {
            if (Config.LogLevel == level) return;

            if (level == Config.LogOff)
            {
                Log.WriteAlways(L.T("로그 기록을 끕니다. 이후로는 아무것도 기록하지 않습니다.",
                                    "Logging is now off. Nothing further will be written."));
                Config.LogLevel = level;
            }
            else
            {
                Config.LogLevel = level;
                Log.WriteAlways(L.T("로그 기록 수준 = ", "Logging level = ")
                    + (level == Config.LogVerbose
                       ? L.T("자세히", "verbose") : L.T("기본", "normal")));
            }

            if (level == Config.LogOff) Config.SpyMode = false;
            Config.Save();
        }

        /// <summary>
        /// 자기 자신을 상승 권한으로 다시 실행한다. UAC 동의 창은 사용자가 직접 승인한다.
        /// 새 인스턴스는 --replace 로 시작해 이 프로세스가 빠질 때까지 잠시 기다린다.
        /// </summary>
        private static void Elevate()
        {
            var sb = new StringBuilder(520);
            if (Native.GetModuleFileName(IntPtr.Zero, sb, sb.Capacity) <= 0) return;

            IntPtr rc = Native.ShellExecute(IntPtr.Zero, "runas", sb.ToString(), "--replace",
                null, Native.SW_SHOWNORMAL);

            // ShellExecute 는 성공 시 32 보다 큰 값을 돌려준다.
            if (rc.ToInt64() > 32)
            {
                Log.Write(L.T("관리자 권한으로 다시 실행합니다 - 현재 인스턴스 종료",
                              "Restarting elevated - shutting down this instance"));
                Native.PostQuitMessage(0);
            }
            else
            {
                Log.Write(L.T("권한 상승 실행 취소 또는 실패 (rc=",
                              "Elevation cancelled or failed (rc=") + rc.ToInt64() + ")");
            }
        }
    }

    // ------------------------------------------------------------------- 언어

    /// <summary>
    /// 화면과 로그에 나가는 문자열의 언어. 기본은 Windows 표시 언어를 따르고,
    /// 설정 파일의 Language 로 강제할 수 있다.
    ///
    /// 리소스 어셈블리를 쓰지 않고 두 문장을 나란히 두는 방식이라, 실행 파일이
    /// 하나로 유지되고 번역이 코드 옆에 붙어 있어 어긋날 일이 없다.
    /// </summary>
    internal static class L
    {
        public static bool Ko = DetectKorean();

        /// <summary>"auto" / "ko" / "en"</summary>
        public static void Apply(string setting)
        {
            if (string.Equals(setting, "ko", StringComparison.OrdinalIgnoreCase)) Ko = true;
            else if (string.Equals(setting, "en", StringComparison.OrdinalIgnoreCase)) Ko = false;
            else Ko = DetectKorean();
        }

        public static string T(string ko, string en)
        {
            return Ko ? ko : en;
        }

        private static bool DetectKorean()
        {
            try
            {
                // PRIMARYLANGID(LANGID) == LANG_KOREAN(0x12)
                return (Native.GetUserDefaultUILanguage() & 0x3FF) == 0x12;
            }
            catch { return false; }
        }
    }

    // --------------------------------------------------------------- 매칭 규칙

    internal static class Rules
    {
        public static bool ClassMatches(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            var list = Config.ClassContains;
            for (int i = 0; i < list.Count; i++)
                if (cls.IndexOf(list[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static bool TitleMatches(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            var eq = Config.TitleEquals;
            for (int i = 0; i < eq.Count; i++)
                if (string.Equals(title, eq[i], StringComparison.OrdinalIgnoreCase)) return true;
            var ct = Config.TitleContains;
            for (int i = 0; i < ct.Count; i++)
                if (title.IndexOf(ct[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static bool ProcessMatches(string procName)
        {
            if (string.IsNullOrEmpty(procName)) return false;
            var list = Config.ProcessNames;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(procName, list[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    // ------------------------------------------------------ 최상위/포커스 강제

    internal static class Force
    {
        internal struct Result
        {
            public bool SetWindowPosOk;
            public int SetWindowPosErr;
            public bool AttachFgOk;
            public bool AttachTargetOk;
            public bool SetForegroundOk;
            public int SetForegroundErr;
            public bool GotForeground;
            public bool IsTopmostNow;

            /// <summary>
            /// 무결성 수준(UIPI) 차단 여부.
            ///
            /// SetForegroundWindow 의 ERROR_ACCESS_DENIED 는 단순한 포그라운드 잠금일 때도
            /// 나오므로 권한 판단에 쓰면 안 된다. 반면 SetWindowPos 로 Z-order 를 바꾸는 것은
            /// 같은 무결성 수준이면 항상 통하므로, 여기서의 거부만이 진짜 UIPI 신호다.
            /// </summary>
            public bool IntegrityBlocked
            {
                get { return SetWindowPosErr == Native.ERROR_ACCESS_DENIED; }
            }

            public override string ToString()
            {
                return "TOPMOST=" + (SetWindowPosOk ? "OK"
                            : L.T("실패(err ", "failed(err ") + SetWindowPosErr + ")")
                     + "  AttachThreadInput(fg=" + AttachFgOk + ",target=" + AttachTargetOk + ")"
                     + "  SetForegroundWindow=" + (SetForegroundOk ? "OK"
                            : L.T("실패(err ", "failed(err ") + SetForegroundErr + ")")
                     + (GotForeground ? L.T("  => 포그라운드 획득", "  => foreground acquired")
                                      : L.T("  => 포그라운드 미획득", "  => foreground not acquired"))
                     + (IsTopmostNow ? L.T(", 항상위 적용", ", topmost applied")
                                     : L.T(", 항상위 미적용", ", topmost not applied"));
            }
        }

        /// <summary>
        /// 대상 창을 최상위로 올리고 포그라운드/포커스를 강제로 가져온다.
        ///
        /// 주의: 여기서 소유자 체인(GA_ROOTOWNER)을 따라 올라가면 안 된다.
        /// WebAuthn 은 호출자(브라우저)의 HWND 를 넘기므로 인증 창이 브라우저 창에
        /// 소유된 창으로 만들어질 수 있고, 그러면 소유자 체인이 프로세스를 넘어
        /// 브라우저를 가리킨다. 그 창을 조작하면 인증 창이 아니라 브라우저가
        /// 활성화되고 브라우저에 '항상 위'가 눌러붙는다. 반드시 매칭된 창 자체를 다룬다.
        /// </summary>
        public static Result Foreground(IntPtr hwnd)
        {
            var r = new Result();
            if (!Native.IsWindow(hwnd)) return r;

            if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

            // 1) 활성화보다 먼저 Z-order 를 올려 "가려짐"부터 해소한다.
            //    SetWindowPos 는 UIPI 메시지 제한을 받지 않으므로 대개 여기까지는 성공한다.
            r.SetWindowPosOk = Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);
            if (!r.SetWindowPosOk) r.SetWindowPosErr = Marshal.GetLastWin32Error();

            // 2) 포그라운드 강제. SetForegroundWindow 는 호출자가 이미 포그라운드일 때만
            //    허용되므로, 현재 전경 스레드와 대상 스레드에 입력 큐를 잠시 붙였다 뗀다.
            uint dummy;
            IntPtr fg = Native.GetForegroundWindow();
            uint fgThread = fg != IntPtr.Zero ? Native.GetWindowThreadProcessId(fg, out dummy) : 0;
            uint targetThread = Native.GetWindowThreadProcessId(hwnd, out dummy);
            uint curThread = Native.GetCurrentThreadId();

            bool attachedFg = false, attachedTarget = false;
            try
            {
                if (fgThread != 0 && fgThread != curThread)
                    attachedFg = r.AttachFgOk = Native.AttachThreadInput(curThread, fgThread, true);
                if (targetThread != 0 && targetThread != curThread && targetThread != fgThread)
                    attachedTarget = r.AttachTargetOk = Native.AttachThreadInput(curThread, targetThread, true);

                Native.BringWindowToTop(hwnd);
                r.SetForegroundOk = Native.SetForegroundWindow(hwnd);
                if (!r.SetForegroundOk) r.SetForegroundErr = Marshal.GetLastWin32Error();
                Native.SetActiveWindow(hwnd);
                Native.SetFocus(hwnd);
            }
            finally
            {
                if (attachedTarget) Native.AttachThreadInput(curThread, targetThread, false);
                if (attachedFg) Native.AttachThreadInput(curThread, fgThread, false);
            }

            r.GotForeground = Native.GetForegroundWindow() == hwnd;

            // 소유된 창은 TOPMOST 상태가 소유자와 묶여 있어, 활성화 과정에서 창 관리자가
            // 방금 준 TOPMOST 를 한 번 되돌린다. 활성화가 끝난 뒤 다시 한 번 적용한다.
            if (Config.KeepTopmost && !IsTopmost(hwnd))
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            r.IsTopmostNow = IsTopmost(hwnd);
            return r;
        }

        public static bool IsTopmost(IntPtr hwnd)
        {
            return (Native.GetWindowLongSafe(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
        }
    }

    // ----------------------------------------------------------------- 진단

    internal static class Diag
    {
        private static int _selfRid = int.MinValue;

        /// <summary>자기 프로세스의 무결성 수준 값. 실패 시 -1.</summary>
        public static int SelfRid
        {
            get
            {
                if (_selfRid == int.MinValue) _selfRid = RidOfPid(Native.GetCurrentProcessId());
                return _selfRid;
            }
        }

        /// <summary>자기 자신의 권한 상태 - 조작이 막힐 때 원인 판단에 쓴다.</summary>
        public static string SelfDescription()
        {
            return "self pid=" + Native.GetCurrentProcessId() + " IL=" + Describe(SelfRid)
                 + " bitness=" + (IntPtr.Size * 8);
        }

        /// <summary>대상 창을 소유한 프로세스의 무결성 수준 등 조작 가능성 관련 정보.</summary>
        public static string Target(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            long ex = Native.GetWindowLongSafe(hwnd, Native.GWL_EXSTYLE);
            return "target pid=" + pid
                 + " proc=" + App.ProcessNameOf(hwnd)
                 + " IL=" + Describe(RidOfPid(pid))
                 + " exStyle=0x" + ex.ToString("X8")
                 + " (TOPMOST=" + ((ex & Native.WS_EX_TOPMOST) != 0)
                 + ", NOACTIVATE=" + ((ex & Native.WS_EX_NOACTIVATE) != 0) + ")";
        }

        /// <summary>
        /// 대상 스레드 입장에서 본 입력 상태. GetForegroundWindow 만으로는
        /// "창은 전경인데 내부 콘텐츠에 포커스가 없는" 상황을 구분할 수 없어서,
        /// GetGUIThreadInfo 로 활성/포커스 창까지 함께 본다.
        /// </summary>
        public static string InputState(IntPtr hwnd)
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint pid;
            uint tid = Native.GetWindowThreadProcessId(hwnd, out pid);

            var gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            bool ok = Native.GetGUIThreadInfo(tid, ref gti);

            return "fg=" + (fg == hwnd ? L.T("대상", "target") : "0x" + fg.ToInt64().ToString("X"))
                 + " topmost=" + Force.IsTopmost(hwnd)
                 + (ok
                    ? "  active=0x" + gti.hwndActive.ToInt64().ToString("X")
                      + " focus=0x" + gti.hwndFocus.ToInt64().ToString("X")
                      + " capture=0x" + gti.hwndCapture.ToInt64().ToString("X")
                      + " flags=0x" + gti.flags.ToString("X")
                    : L.T("  GUIThreadInfo 실패", "  GUIThreadInfo failed"));
        }

        public static int RidOfWindow(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return RidOfPid(pid);
        }

        public static string Describe(int rid)
        {
            return rid < 0 ? L.T("조회불가", "unavailable") : rid + "(" + Name(rid) + ")";
        }

        /// <summary>프로세스 토큰의 무결성 수준 RID 를 읽는다. 실패 시 -1.</summary>
        public static int RidOfPid(uint pid)
        {
            if (pid == 0) return -1;
            IntPtr hProc = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return -1;
            try
            {
                IntPtr token;
                if (!Native.OpenProcessToken(hProc, Native.TOKEN_QUERY, out token)) return -1;
                try
                {
                    int len;
                    Native.GetTokenInformation(token, Native.TokenIntegrityLevel, IntPtr.Zero, 0, out len);
                    if (len <= 0) return -1;
                    IntPtr buf = Marshal.AllocHGlobal(len);
                    try
                    {
                        if (!Native.GetTokenInformation(token, Native.TokenIntegrityLevel, buf, len, out len))
                            return -1;

                        // TOKEN_MANDATORY_LABEL.Label.Sid 가 구조체의 첫 포인터다.
                        IntPtr sid = Marshal.ReadIntPtr(buf);
                        IntPtr cntPtr = Native.GetSidSubAuthorityCount(sid);
                        if (cntPtr == IntPtr.Zero) return -1;
                        int cnt = Marshal.ReadByte(cntPtr);
                        if (cnt <= 0) return -1;
                        return Marshal.ReadInt32(Native.GetSidSubAuthority(sid, (uint)(cnt - 1)));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { Native.CloseHandle(token); }
            }
            finally { Native.CloseHandle(hProc); }
        }

        private static string Name(int rid)
        {
            if (rid >= 0x4000) return "System";
            if (rid >= 0x3000) return "High";
            if (rid >= 0x2100) return "MediumPlus";
            if (rid >= 0x2000) return "Medium";
            if (rid >= 0x1000) return "Low";
            return "Untrusted";
        }
    }

    /// <summary>
    /// 강제 로직이 타 프로세스 창에 실제로 통하는지 눈으로 확인하는 자가 진단.
    /// 대상은 문자표(charmap) - 문서를 담지 않는 앱이라 시험해도 잃을 것이 없다.
    /// </summary>
    internal static class SelfTest
    {
        private const string TestProcess = "charmap.exe";
        private static IntPtr _found;

        public static void Run(IntPtr owner)
        {
            WindowFill.RunSelfTest();

            Log.Write(L.T("[자가진단] 시작 - " + TestProcess + " 를 대상으로 강제 포그라운드를 시험합니다.",
                          "[selftest] Start - trying forced foreground against " + TestProcess));
            Native.ShellExecute(IntPtr.Zero, "open", TestProcess, null, null, Native.SW_SHOWNORMAL);

            // 창이 뜰 때까지 최대 3초 대기 (자가 진단 시에만 도는 짧은 루프)
            IntPtr target = IntPtr.Zero;
            for (int i = 0; i < 30 && target == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                target = FindTestWindow();
            }

            if (target == IntPtr.Zero)
            {
                Log.Write(L.T("[자가진단] " + TestProcess + " 창을 찾지 못했습니다.",
                              "[selftest] Could not find a window for " + TestProcess));
                Native.MessageBox(owner,
                    L.T(TestProcess + " 창을 찾지 못해 자가 진단을 마치지 못했습니다.",
                        "Self test could not finish: no window found for " + TestProcess + "."),
                    "OnTopPasskey", Native.MB_ICONWARNING);
                return;
            }

            Log.Write(L.T("[자가진단] ", "[selftest] ") + Diag.Target(target));
            Force.Result r = Force.Foreground(target);
            Log.Write(L.T("[자가진단] ", "[selftest] ") + r);

            // 시험 대상은 원래대로 되돌린다 (사용자 창을 계속 항상 위로 두지 않는다).
            Native.SetWindowPos(target, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            Native.MessageBox(owner,
                L.T("자가 진단 결과\r\n\r\n" + r + "\r\n\r\n자세한 내용은 로그를 확인하세요.",
                    "Self test result\r\n\r\n" + r + "\r\n\r\nSee the log for details."),
                "OnTopPasskey", Native.MB_ICONINFORMATION);
        }

        private static IntPtr FindTestWindow()
        {
            _found = IntPtr.Zero;
            Native.EnumWindows(EnumProc, IntPtr.Zero);
            return _found;
        }

        private static readonly Native.EnumWindowsProc EnumProc = (h, l) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (!string.Equals(App.ProcessNameOf(h), TestProcess, StringComparison.OrdinalIgnoreCase))
                return true;
            _found = h;
            return false;   // 찾았으므로 열거 중단
        };
    }

    // ------------------------------------------------------------------ 설정

    internal static class Config
    {
        public static readonly List<string> ClassContains = new List<string>(4);
        public static readonly List<string> ProcessNames = new List<string>(4);
        public static readonly List<string> TitleEquals = new List<string>(4);
        public static readonly List<string> TitleContains = new List<string>(4);

        public static readonly List<string> FillAutoProcesses = new List<string>(4);
        public static readonly List<string> FillIgnoreProcesses = new List<string>(4);
        public static bool FillOnAltDrag = true;

        /// <summary>0 = 기록 안 함, 1 = 기본(감지/결과만), 2 = 자세히(추적 포함)</summary>
        public static int LogLevel = 1;

        public const int LogOff = 0;
        public const int LogNormal = 1;
        public const int LogVerbose = 2;

        /// <summary>"auto" = Windows 표시 언어를 따름, "ko", "en"</summary>
        public static string Language = "auto";

        public static bool SpyMode;
        public static bool KeepTopmost = true;
        public static bool UseObjectShowHook = true;
        public static int SafetyPollMs;    // 0 = 사용 안 함 (기본)
        public static int TraceMs = 500;   // 자세히 기록일 때 입력 상태를 남기는 주기

        private static string _baseDir;

        public static string BaseDir
        {
            get
            {
                if (_baseDir == null)
                {
                    _baseDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "OnTopPasskey");
                    if (!Directory.Exists(_baseDir)) Directory.CreateDirectory(_baseDir);
                }
                return _baseDir;
            }
        }

        public static string ConfigPath { get { return Path.Combine(BaseDir, "config.ini"); } }

        private static void ApplyDefaults()
        {
            ClassContains.Clear(); ProcessNames.Clear();
            TitleEquals.Clear(); TitleContains.Clear();
            FillAutoProcesses.Clear(); FillIgnoreProcesses.Clear();
            FillOnAltDrag = true;

            // 1순위 신호: 최상위 창 클래스명. 제목과 달리 로케일에 영향받지 않는다.
            ClassContains.Add("Credential Dialog Xaml Host");

            ProcessNames.Add("CredentialUIBroker.exe");
            ProcessNames.Add("LogonUI.exe");

            // 제목 규칙은 기본으로 두지 않는다. "Windows 보안" 제목은 보안 앱(SecHealthUI)과
            // 시스템 대화상자까지 잡아 엉뚱한 창을 당겨왔다. 클래스명과 프로세스명이면 충분하다.

            Language = "auto";
            LogLevel = LogNormal;
            SpyMode = false;
            KeepTopmost = true;
            UseObjectShowHook = true;
            SafetyPollMs = 0;
            TraceMs = 500;
        }

        public static void Load()
        {
            ApplyDefaults();
            L.Apply(Language);
            try
            {
                // 설정 파일을 처음 만들 때도 해석된 언어로 주석을 쓰도록 순서를 지킨다.
                if (!File.Exists(ConfigPath)) { Save(); return; }

                bool sawRule = false;
                bool sawFillAuto = false;
                bool sawFillIgn = false;
                var cls = new List<string>();
                var prc = new List<string>();
                var teq = new List<string>();
                var tct = new List<string>();
                var fauto = new List<string>();
                var fign = new List<string>();

                foreach (var raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    if (val.Length == 0) continue;

                    switch (key)
                    {
                        case "classcontains": cls.Add(val); sawRule = true; break;
                        case "processname": prc.Add(val); sawRule = true; break;
                        case "titleequals": teq.Add(val); sawRule = true; break;
                        case "titlecontains": tct.Add(val); sawRule = true; break;
                        case "fillautoprocess":
                        case "fillautoprocesses":
                            AddSplit(fauto, val);
                            sawFillAuto = true;
                            break;
                        case "fillignoreprocess":
                        case "fillignoreprocesses":
                            AddSplit(fign, val);
                            sawFillIgn = true;
                            break;
                        case "fillonaltdrag": FillOnAltDrag = ParseBool(val); break;
                        case "language": Language = val; break;
                        case "loglevel":
                            int lv;
                            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv))
                                LogLevel = lv < LogOff ? LogOff : (lv > LogVerbose ? LogVerbose : lv);
                            break;
                        case "spymode": SpyMode = ParseBool(val); break;
                        case "keeptopmost": KeepTopmost = ParseBool(val); break;
                        case "useobjectshowhook": UseObjectShowHook = ParseBool(val); break;
                        case "safetypollms":
                            int p;
                            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out p))
                                SafetyPollMs = p <= 0 ? 0 : Math.Max(500, p);
                            break;
                        case "tracems":
                            int tm;
                            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out tm))
                                TraceMs = tm <= 0 ? 0 : Math.Max(100, (tm / 100) * 100);
                            break;
                    }
                }

                // 파일에서 읽은 Language 를 여기서 다시 적용한다.
                // 파싱 전에 한 번 적용해 두는 것은 파일이 없어 Save() 로 넘어가는 경우에만 쓰인다.
                L.Apply(Language);

                // 예전 기본 제목 규칙이 그대로 저장된 설정이면 걷어내고 다시 저장한다.
                bool oldTitleDefaults = teq.Count == 2 && teq.Contains("Windows 보안") && teq.Contains("Windows Security")
                                        && tct.Count == 1 && tct[0] == "Windows Hello";
                if (oldTitleDefaults) { teq.Clear(); tct.Clear(); }

                if (sawRule)
                {
                    Swap(ClassContains, cls);
                    Swap(ProcessNames, prc);
                    Swap(TitleEquals, teq);
                    Swap(TitleContains, tct);
                }
                if (sawFillAuto) Swap(FillAutoProcesses, fauto);
                if (sawFillIgn) Swap(FillIgnoreProcesses, fign);
                if (oldTitleDefaults) Save();
            }
            catch (Exception ex)
            {
                Log.Write(L.T("설정 읽기 실패: ", "Failed to read settings: ") + ex.Message);
            }
        }

        private static void AddSplit(List<string> target, string val)
        {
            string[] parts = val.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim();
                if (s.Length > 0) target.Add(s);
            }
        }

        private static void Swap(List<string> target, List<string> src)
        {
            target.Clear();
            target.AddRange(src);
        }

        private static bool ParseBool(string v)
        {
            v = v.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder(1600);
                sb.AppendLine(L.T("# OnTopPasskey 설정 (UTF-8)",
                                  "# OnTopPasskey settings (UTF-8)"));
                sb.AppendLine(L.T("# 인증 창이 감지되지 않으면 트레이 메뉴에서 '진단 모드'를 켜고 창을 띄운 뒤,",
                                  "# If the prompt is not detected, turn on 'Diagnostic mode' in the tray menu,"));
                sb.AppendLine(L.T("# 로그에 기록된 class / title / proc 값을 아래 규칙에 추가하세요.",
                                  "# reproduce it, then add the class / title / proc values from the log below."));
                sb.AppendLine(L.T("# 규칙 항목을 하나라도 직접 적으면 기본 규칙 대신 파일의 규칙만 사용합니다.",
                                  "# Writing any rule entry here replaces the built-in rules entirely."));
                sb.AppendLine();
                sb.AppendLine(L.T("# 표시 언어: auto = Windows 표시 언어를 따름, ko = 한국어, en = English",
                                  "# UI language: auto = follow Windows display language, ko = Korean, en = English"));
                sb.AppendLine("Language=" + Language);
                sb.AppendLine();
                for (int i = 0; i < ClassContains.Count; i++) sb.AppendLine("ClassContains=" + ClassContains[i]);
                for (int i = 0; i < ProcessNames.Count; i++) sb.AppendLine("ProcessName=" + ProcessNames[i]);
                for (int i = 0; i < TitleEquals.Count; i++) sb.AppendLine("TitleEquals=" + TitleEquals[i]);
                for (int i = 0; i < TitleContains.Count; i++) sb.AppendLine("TitleContains=" + TitleContains[i]);
                sb.AppendLine();
                sb.AppendLine(L.T("# 창 빈 공간 채우기 (단축키: Ctrl+Alt+Shift+F11)",
                                  "# Fill window into empty space (Shortcut: Ctrl+Alt+Shift+F11)"));
                sb.AppendLine(L.T("# Alt 키를 누른 채 창을 드래그하여 놓았을 때 빈 공간 채우기 자동 실행 여부 (1=사용, 0=사용 안 함)",
                                  "# Whether to auto-fill when dropping a dragged window while holding Alt (1=enabled, 0=disabled)"));
                sb.AppendLine("FillOnAltDrag=" + B(FillOnAltDrag));
                sb.AppendLine(L.T("# Alt 키 없이도 드래그 후 놓으면 자동으로 빈 공간을 채울 대상 프로세스 목록 (콤마 구분 또는 여러 줄)",
                                  "# Process names to auto-fill on drag-drop without holding Alt (comma-separated or multiple lines)"));
                if (FillAutoProcesses.Count > 0)
                {
                    for (int i = 0; i < FillAutoProcesses.Count; i++)
                        sb.AppendLine("FillAutoProcesses=" + FillAutoProcesses[i]);
                }
                else
                {
                    sb.AppendLine(L.T("# FillAutoProcesses=Muse.exe", "# FillAutoProcesses=Muse.exe"));
                }
                sb.AppendLine(L.T("# 빈 공간 계산 시 장애물(벽)에서 제외할 프로세스 목록",
                                  "# Process names to ignore as obstacles during empty space calculation"));
                if (FillIgnoreProcesses.Count > 0)
                {
                    for (int i = 0; i < FillIgnoreProcesses.Count; i++)
                        sb.AppendLine("FillIgnoreProcesses=" + FillIgnoreProcesses[i]);
                }
                else
                {
                    sb.AppendLine(L.T("# FillIgnoreProcesses=Rainmeter.exe", "# FillIgnoreProcesses=Rainmeter.exe"));
                }
                sb.AppendLine();
                sb.AppendLine(L.T("# 로그 기록 수준", "# Logging level"));
                sb.AppendLine(L.T("#   0 = 기록 안 함 (파일에 아무것도 쓰지 않습니다)",
                                  "#   0 = off (nothing is written to disk at all)"));
                sb.AppendLine(L.T("#   1 = 기본 - 인증 창을 감지하고 처리한 결과만 (기본값)",
                                  "#   1 = normal - detections and their outcome only (default)"));
                sb.AppendLine(L.T("#   2 = 자세히 - 창이 떠 있는 동안의 입력 상태 추적까지",
                                  "#   2 = verbose - also traces input state while the prompt is open"));
                sb.AppendLine("LogLevel=" + LogLevel.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine(L.T("# 나타나는 모든 창 정보를 로그에 남깁니다 (진단용, 평소엔 0)",
                                  "# Log every window that appears (for diagnosis; keep 0 normally)"));
                sb.AppendLine("SpyMode=" + B(SpyMode));
                sb.AppendLine(L.T("# 인증 창이 떠 있는 동안 '항상 위' 상태를 유지",
                                  "# Keep the prompt topmost for as long as it is open"));
                sb.AppendLine("KeepTopmost=" + B(KeepTopmost));
                sb.AppendLine(L.T("# 보조 감지기(EVENT_OBJECT_SHOW). 감지율이 가장 높지만 콜백 빈도도 가장 높습니다.",
                                  "# Secondary detector (EVENT_OBJECT_SHOW): best coverage, most callbacks."));
                sb.AppendLine(L.T("# 셸 훅만으로 충분하다고 판단되면 0 으로 두면 더 가볍습니다.",
                                  "# Set 0 to run lighter if the shell hook alone proves sufficient."));
                sb.AppendLine("UseObjectShowHook=" + B(UseObjectShowHook));
                sb.AppendLine(L.T("# 최후의 안전망 스캔 주기(ms). 0 = 사용 안 함(권장). 켜려면 2000~5000 권장.",
                                  "# Last-resort scan interval (ms). 0 = off (recommended); use 2000-5000 if needed."));
                sb.AppendLine("SafetyPollMs=" + SafetyPollMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine(L.T("# LogLevel=2 일 때 입력 상태(전경/활성/포커스)를 기록하는 주기(ms).",
                                  "# Interval (ms) for input-state trace lines when LogLevel=2."));
                sb.AppendLine("TraceMs=" + TraceMs.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Log.Write(L.T("설정 저장 실패: ", "Failed to save settings: ") + ex.Message);
            }
        }

        private static string B(bool v) { return v ? "1" : "0"; }
    }

    // ------------------------------------------------------------------- 로그

    internal static class Log
    {
        private const long MaxBytes = 256 * 1024;
        private static string _path;

        public static string FilePath
        {
            get { return _path ?? (_path = Path.Combine(Config.BaseDir, "log.txt")); }
        }

        /// <summary>기록 안 함 모드에서는 파일에 아무것도 쓰지 않는다.</summary>
        public static bool Enabled
        {
            get { return Config.LogLevel > Config.LogOff; }
        }

        /// <summary>기록 수준과 무관하게 반드시 남겨야 하는 줄에만 쓴다(모드 전환 안내 등).</summary>
        public static void WriteAlways(string message)
        {
            Emit(message);
        }

        public static void Write(string message)
        {
            if (!Enabled) return;
            Emit(message);
        }

        public static void Clear()
        {
            try
            {
                File.WriteAllText(FilePath, string.Empty, Encoding.UTF8);
                string old = FilePath + ".1";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }
        }

        private static void Emit(string message)
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string old = FilePath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(FilePath, old);
                }
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    // ------------------------------------------------- 워킹셋 / 자동 실행 / 셸

    internal static class Mem
    {
        /// <summary>쓰지 않는 워킹셋을 OS 에 반납한다.</summary>
        public static void Trim()
        {
            try { Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); }
            catch { }
        }
    }

    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "OnTopPasskey";

        private static string ExePath
        {
            get
            {
                var sb = new StringBuilder(520);
                int n = Native.GetModuleFileName(IntPtr.Zero, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : string.Empty;
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && !string.IsNullOrEmpty(k.GetValue(ValueName) as string);
            }
            catch { return false; }
        }

        /// <summary>
        /// Program Files 아래에서 돌고 있으면 설치판이다. 이때 자동 실행은 작업 스케줄러가
        /// 관리하므로, 레지스트리 자동 실행까지 켜면 중복 실행된다.
        /// </summary>
        public static bool IsInstalledBuild()
        {
            try
            {
                string exe = ExePath;
                if (exe.Length == 0) return false;
                foreach (Environment.SpecialFolder f in new Environment.SpecialFolder[] {
                    Environment.SpecialFolder.ProgramFiles,
                    Environment.SpecialFolder.ProgramFilesX86 })
                {
                    string dir = Environment.GetFolderPath(f);
                    if (dir.Length > 0 && exe.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        public static void Set(bool enable)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k == null) return;
                if (enable) k.SetValue(ValueName, "\"" + ExePath + "\"");
                else k.DeleteValue(ValueName, false);
            }
        }
    }

    internal static class Shell
    {
        public static void OpenInNotepad(string path)
        {
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, string.Empty, Encoding.UTF8);
                Native.ShellExecute(IntPtr.Zero, "open", "notepad.exe", "\"" + path + "\"",
                    null, Native.SW_SHOWNORMAL);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Win32

    internal static class Native
    {
        public const int WM_APP = 0x8000;
        public const uint WM_DESTROY = 0x0002;
        public const uint WM_TIMER = 0x0113;
        public const uint WM_HOTKEY = 0x0312;
        public const uint WM_NCHITTEST = 0x0084;
        public const uint WM_MOUSEACTIVATE = 0x0021;
        public const int HTTRANSPARENT = -1;
        public const int MA_NOACTIVATE = 3;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_CONTEXTMENU = 0x007B;

        public const int HSHELL_WINDOWCREATED = 1;
        // HSHELL_RUDEAPPACTIVATED(0x8004) 는 최상위 비트를 떼면 HSHELL_WINDOWACTIVATED(4) 와 같다.
        public const int HSHELL_WINDOWACTIVATED = 4;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_LAYERED = 0x00080000;
        public const uint LWA_ALPHA = 0x00000002;
        public const int GRAY_BRUSH = 2;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_RESTORE = 9;

        public const uint GA_ROOTOWNER = 3;
        public const uint GW_OWNER = 4;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int VK_MENU = 0x12;

        public const int OBJID_WINDOW = 0;
        public const int CHILDID_SELF = 0;

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        public const uint EVENT_SYSTEM_DIALOGSTART = 0x0010;
        public const uint EVENT_OBJECT_SHOW = 0x8002;

        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWA_CLOAKED = 14;

        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        public const int NIM_ADD = 0;
        public const int NIM_MODIFY = 1;
        public const int NIM_DELETE = 2;
        public const int NIF_MESSAGE = 0x01;
        public const int NIF_ICON = 0x02;
        public const int NIF_TIP = 0x04;
        public const int IDI_APPLICATION = 32512;

        public const uint IMAGE_ICON = 1;
        public const uint LR_DEFAULTCOLOR = 0x0000;
        public const uint LR_SHARED = 0x8000;
        public const int SM_CXSMICON = 49;
        public const int SM_CYSMICON = 50;

        public const int SECURITY_MANDATORY_HIGH_RID = 0x3000;

        public const uint MF_STRING = 0x0000;
        public const uint MF_POPUP = 0x0010;
        public const uint MF_CHECKED = 0x0008;
        public const uint MF_SEPARATOR = 0x0800;
        public const uint TPM_RIGHTBUTTON = 0x0002;
        public const uint TPM_NONOTIFY = 0x0080;
        public const uint TPM_RETURNCMD = 0x0100;

        public const uint MB_ICONINFORMATION = 0x00000040;
        public const uint MB_ICONWARNING = 0x00000030;

        public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint TOKEN_QUERY = 0x0008;
        public const int TokenIntegrityLevel = 25;
        public const int ERROR_ACCESS_DENIED = 5;

        public const int NIF_INFO = 0x0010;
        public const int NIIF_WARNING = 0x0002;

        public delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd; public uint message; public IntPtr wParam;
            public IntPtr lParam; public uint time; public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        // --- user32
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("gdi32.dll")]
        public static extern IntPtr GetStockObject(int fnObject);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        public static extern bool RegisterShellHookWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool DeregisterShellHookWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadImage(IntPtr hInst, IntPtr name, uint type,
            int cx, int cy, uint fuLoad);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
            int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, int uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        public static long GetWindowLongSafe(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex).ToInt64()
                : GetWindowLong32(hWnd, nIndex);
        }

        // --- kernel32
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetModuleFileName(IntPtr hModule, StringBuilder lpFilename, int nSize);

        [DllImport("kernel32.dll")]
        public static extern ushort GetUserDefaultUILanguage();

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags,
            StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll")]
        public static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

        // --- advapi32 (진단 전용, 감지 경로에서는 호출하지 않음)
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
            IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

        // --- shell32
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpdata);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr ShellExecute(IntPtr hwnd, string lpOperation, string lpFile,
            string lpParameters, string lpDirectory, int nShowCmd);
    }
}
