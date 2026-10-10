// OnTopPasskey 설치 관리자
//
// 본체(OnTopPasskey.exe)를 리소스로 품고 있는 단일 설치 파일이다.
// 외부 설치 도구(Inno Setup, WiX 등) 없이 Windows 에 기본 포함된 csc.exe 만으로 빌드된다.
//
// 하는 일
//   * %ProgramFiles%\OnTopPasskey\ 에 본체와 제거 관리자를 설치
//   * 작업 스케줄러에 로그온 시 '최고 권한' 실행 등록 (UAC 창 없이 자동 실행)
//   * 시작 메뉴 바로 가기
//   * '앱 및 기능' 등록
//
// Program Files 에 설치하는 이유
//   본체는 인증 창을 조작하려면 High 무결성으로 실행되어야 한다. 최고 권한으로 실행되는
//   작업이 사용자가 쓸 수 있는 폴더를 가리키면, 그 파일을 바꿀 수 있는 누구든 로그온 시
//   관리자 권한을 얻는 권한 상승 통로가 된다. Program Files 는 관리자만 쓸 수 있다.
//
// 명령줄
//   (없음)        설치 마법사
//   /uninstall    제거
//   /silent       UI 없이 설치 (자동 배포용)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("OnTopPasskey Setup")]
[assembly: AssemblyDescription("OnTopPasskey Setup")]
[assembly: AssemblyProduct("OnTopPasskey")]
[assembly: AssemblyVersion("1.0.5.0")]
[assembly: AssemblyFileVersion("1.0.5.0")]

namespace OnTopPasskeySetup
{
    /// <summary>
    /// 화면 문자열의 언어. 기본은 Windows 표시 언어를 따르고 /lang:ko|en 로 강제할 수 있다.
    /// 본체와 같은 방식이라 설치 파일 하나로 두 언어를 모두 배포한다.
    /// </summary>
    internal static class L
    {
        public static bool Ko = DetectKorean();

        public static void Apply(string setting)
        {
            if (string.Equals(setting, "ko", StringComparison.OrdinalIgnoreCase)) Ko = true;
            else if (string.Equals(setting, "en", StringComparison.OrdinalIgnoreCase)) Ko = false;
            else Ko = DetectKorean();
        }

        public static string T(string ko, string en) { return Ko ? ko : en; }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern ushort GetUserDefaultUILanguage();

        private static bool DetectKorean()
        {
            try { return (GetUserDefaultUILanguage() & 0x3FF) == 0x12; }   // LANG_KOREAN
            catch { return false; }
        }
    }

    internal static class Program
    {
        public const string AppName = "OnTopPasskey";
        public const string Version = "1.0.5";
        public const string TaskName = "OnTopPasskey";
        public const string ExeName = "OnTopPasskey.exe";
        public const string UninstExe = "uninstall.exe";

        /// <summary>
        /// 로그온 후 실행을 미루는 시간(초). /delay:N 으로 바꿀 수 있고 0 이면 즉시 실행한다.
        /// 부팅 직후 혼잡한 구간을 피해 다른 프로그램에 자리를 내주기 위한 값이다.
        /// </summary>
        public static int LogonDelaySeconds = 45;

        [STAThread]
        private static int Main(string[] args)
        {
            bool uninstall = false, silent = false, fromTemp = false;
            foreach (string a in args)
            {
                string s = a.TrimStart('-', '/').ToLowerInvariant();
                if (s == "uninstall" || s == "u") uninstall = true;
                else if (s == "silent" || s == "s" || s == "quiet") silent = true;
                else if (s == "fromtemp") fromTemp = true;
                else if (s.StartsWith("lang:")) L.Apply(s.Substring(5));
                else if (s.StartsWith("delay:"))
                {
                    int d;
                    if (int.TryParse(s.Substring(6), out d) && d >= 0 && d <= 3600)
                        LogonDelaySeconds = d;
                }
            }

            // PREVIEW 는 배포본에 들어가지 않는다. 화면 배치를 눈으로 확인하기 위한
            // 빌드 전용 스위치로, 권한 검사와 실제 설치 동작을 건너뛴다.
            bool requireAdmin = true;
#if PREVIEW
            requireAdmin = false;
#endif

            if (requireAdmin && !Installer.IsAdministrator())
            {
                // 매니페스트에 requireAdministrator 가 있어 정상 경로에서는 여기 오지 않는다.
                MessageBox.Show(
                    L.T("관리자 권한이 필요합니다. 파일을 마우스 오른쪽 버튼으로 눌러\r\n"
                        + "'관리자 권한으로 실행' 을 선택하세요.",
                        "Administrator rights are required. Right-click this file and\r\n"
                        + "choose 'Run as administrator'."),
                    AppName + L.T(" 설치", " Setup"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (uninstall) return RunUninstall(silent, fromTemp);
            return RunInstall(silent);
        }

        private static int RunInstall(bool silent)
        {
            if (silent)
            {
                var log = new StringBuilder();
                try
                {
                    Installer.Install(delegate(string m) { log.AppendLine(m); });
                    Installer.StartApp();
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }
            }

            using (var f = new WizardForm())
            {
                Application.Run(f);
                return f.Failed ? 1 : 0;
            }
        }

        private static int RunUninstall(bool silent, bool fromTemp)
        {
            // 제거 관리자가 설치 폴더 안에서 돌고 있으면 자기 자신이 잠겨 폴더를 지울 수 없다.
            // 임시 폴더로 자신을 복사해 그쪽에서 다시 실행한다. 일반적인 설치 관리자의 방식이다.
            if (!fromTemp && Installer.RunningInsideInstallDir())
            {
                try
                {
                    string tmp = Path.Combine(Path.GetTempPath(),
                        AppName + "-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                    File.Copy(Application.ExecutablePath, tmp, true);

                    var psi = new ProcessStartInfo(tmp, "/uninstall /fromtemp"
                        + (silent ? " /silent" : "") + (L.Ko ? " /lang:ko" : " /lang:en"));
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    return 0;
                }
                catch
                {
                    // 복사에 실패하면 그냥 이 자리에서 진행한다 (설치 폴더만 남을 수 있음)
                }
            }

            if (!silent)
            {
                var r = MessageBox.Show(
                    L.T(AppName + " 을(를) 제거할까요?\r\n\r\n"
                        + "프로그램과 자동 실행 등록이 모두 삭제됩니다.\r\n"
                        + "설정과 로그는 남습니다.",
                        "Remove " + AppName + "?\r\n\r\n"
                        + "The program and its auto-start entry will be deleted.\r\n"
                        + "Settings and logs are kept."),
                    AppName + L.T(" 제거", " Uninstall"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return 0;
            }

            var sb = new StringBuilder();
            try
            {
                Installer.Uninstall(delegate(string m) { sb.AppendLine(m); });
            }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show(
                        L.T("제거 중 문제가 발생했습니다.\r\n\r\n", "Uninstall ran into a problem.\r\n\r\n")
                        + ex.Message,
                        AppName + L.T(" 제거", " Uninstall"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            if (!silent)
                MessageBox.Show(
                    L.T("제거가 완료되었습니다.\r\n\r\n설정과 로그는 다음 위치에 남아 있습니다.\r\n",
                        "Uninstall complete.\r\n\r\nSettings and logs remain here:\r\n")
                    + Installer.DataDir,
                    AppName + L.T(" 제거", " Uninstall"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
    }

    // ------------------------------------------------------------ 설치 로직

    internal static class Installer
    {
        public static string InstallDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Program.AppName);
            }
        }

        public static string DataDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Program.AppName);
            }
        }

        public static string TargetExe { get { return Path.Combine(InstallDir, Program.ExeName); } }
        public static string UninstallerPath { get { return Path.Combine(InstallDir, Program.UninstExe); } }

        private const string UninstKeyPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OnTopPasskey";
        private const string RunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";

        private static string StartMenuDir
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms); }
        }

        private static string ShortcutRun
        {
            get { return Path.Combine(StartMenuDir, Program.AppName + L.T(" 시작", "") + ".lnk"); }
        }

        private static string ShortcutLog
        {
            get { return Path.Combine(StartMenuDir, Program.AppName + L.T(" 로그", " Log") + ".lnk"); }
        }

        /// <summary>
        /// 바로 가기 이름은 언어에 따라 달라진다. 제거할 때는 설치 당시 언어를 알 수 없으므로
        /// 두 언어의 이름을 모두 지운다.
        /// </summary>
        private static string[] AllShortcutPaths()
        {
            return new string[] {
                Path.Combine(StartMenuDir, Program.AppName + " 시작.lnk"),
                Path.Combine(StartMenuDir, Program.AppName + " 로그.lnk"),
                Path.Combine(StartMenuDir, Program.AppName + ".lnk"),
                Path.Combine(StartMenuDir, Program.AppName + " Log.lnk"),
            };
        }

        public static bool IsAdministrator()
        {
            try
            {
                var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static bool RunningInsideInstallDir()
        {
            try
            {
                string self = Path.GetDirectoryName(Application.ExecutablePath);
                return string.Equals(self.TrimEnd('\\'), InstallDir.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // -------------------------------------------------------------- 설치

        public static void Install(Action<string> log)
        {
            log(L.T("이전 버전 정리 중...", "Cleaning up any previous version..."));
            StopApp();
            RemoveTask(log);
            RemoveRunEntry(log);

            log(L.T("파일 복사 중...", "Copying files..."));
            Directory.CreateDirectory(InstallDir);
            ExtractPayload(TargetExe);
            log("  " + TargetExe);

            File.Copy(Application.ExecutablePath, UninstallerPath, true);
            log("  " + UninstallerPath);

            log(L.T("자동 실행 등록 중 (로그온 시 최고 권한)...",
                    "Registering auto-start (highest privileges at logon)..."));
            RegisterTask(log);
            if (Program.LogonDelaySeconds > 0)
                log(L.T("  로그온 후 " + Program.LogonDelaySeconds + "초 뒤에 시작합니다 (부팅 혼잡 회피)",
                        "  Starts " + Program.LogonDelaySeconds
                        + "s after logon, to stay out of the boot rush"));

            log(L.T("시작 메뉴 바로 가기 생성 중...", "Creating Start menu shortcuts..."));
            CreateShortcuts(log);

            log(L.T("'앱 및 기능' 등록 중...", "Registering in Apps & features..."));
            RegisterUninstallEntry();

            log("");
            log(L.T("설치가 완료되었습니다.", "Installation complete."));
        }

        /// <summary>리소스로 품고 있는 본체를 꺼내 쓴다.</summary>
        private static void ExtractPayload(string destination)
        {
            using (Stream src = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload"))
            {
                if (src == null)
                    throw new InvalidOperationException(
                        L.T("설치 파일이 손상되었습니다. 본체가 들어 있지 않습니다.",
                            "This setup file is corrupt: the payload is missing."));

                using (var dst = new FileStream(destination, FileMode.Create, FileAccess.Write))
                {
                    var buf = new byte[64 * 1024];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
                }
            }
        }

        // ------------------------------------------------------------- 제거

        public static void Uninstall(Action<string> log)
        {
            StopApp();
            RemoveTask(log);
            RemoveRunEntry(log);

            foreach (string s in AllShortcutPaths())
            {
                try
                {
                    if (File.Exists(s))
                    {
                        File.Delete(s);
                        log(L.T("바로 가기 삭제: ", "Removed shortcut: ") + s);
                    }
                }
                catch { }
            }

            try
            {
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(UninstKeyPath, false);
                log(L.T("'앱 및 기능' 등록 삭제", "Removed the Apps & features entry"));
            }
            catch { }

            // 설치 폴더 삭제. 잠긴 파일이 있으면 재부팅 시 삭제되도록 예약한다.
            try
            {
                if (Directory.Exists(InstallDir))
                {
                    Directory.Delete(InstallDir, true);
                    log(L.T("설치 폴더 삭제: ", "Removed install folder: ") + InstallDir);
                }
            }
            catch
            {
                ScheduleDeleteOnReboot(InstallDir);
                log(L.T("일부 파일이 사용 중이라 재부팅 시 삭제되도록 예약했습니다.",
                        "Some files were in use; scheduled for deletion on next restart."));
            }
        }

        // ------------------------------------------------------- 개별 동작들

        public static void StopApp()
        {
            try
            {
                Process[] list = Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(Program.ExeName));
                foreach (Process p in list)
                {
                    try { p.Kill(); p.WaitForExit(3000); }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }

        public static void StartApp()
        {
            // 반드시 작업 스케줄러를 거쳐야 최고 권한으로 뜬다.
            RunTool("schtasks.exe", "/run /tn \"" + Program.TaskName + "\"");
        }

        private static void RemoveTask(Action<string> log)
        {
            string outText;
            RunTool("schtasks.exe", "/end /tn \"" + Program.TaskName + "\"", out outText);
            int rc = RunTool("schtasks.exe", "/delete /tn \"" + Program.TaskName + "\" /f", out outText);
            if (rc == 0) log(L.T("기존 자동 실행 등록 삭제", "Removed the previous scheduled task"));
        }

        private static void RemoveRunEntry(Action<string> log)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k != null && k.GetValue(Program.AppName) != null)
                    {
                        k.DeleteValue(Program.AppName, false);
                        log(L.T("기존 자동 실행 항목(HKCU Run) 삭제",
                                "Removed the previous HKCU Run entry"));
                    }
                }
            }
            catch { }
        }

        private static void RegisterTask(Action<string> log)
        {
            string user = WindowsIdentity.GetCurrent().Name;   // DOMAIN\사용자
            string xmlPath = Path.Combine(Path.GetTempPath(),
                "OnTopPasskey-task-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".xml");

            // schtasks /create /xml 은 UTF-16 파일을 요구한다.
            File.WriteAllText(xmlPath, BuildTaskXml(user, TargetExe), Encoding.Unicode);

            try
            {
                string outText;
                int rc = RunTool("schtasks.exe",
                    "/create /tn \"" + Program.TaskName + "\" /xml \"" + xmlPath + "\" /f", out outText);
                if (rc != 0)
                    throw new InvalidOperationException(
                        L.T("작업 스케줄러 등록에 실패했습니다.\r\n",
                            "Could not register the scheduled task.\r\n") + outText);
                log(L.T("  로그온 시 자동 실행 (계정: " + user + ")",
                        "  Runs at logon (account: " + user + ")"));
            }
            finally
            {
                try { File.Delete(xmlPath); }
                catch { }
            }
        }

        private static string BuildTaskXml(string user, string exePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <RegistrationInfo>");
            sb.AppendLine("    <Description>"
                + L.T("Windows Hello / 패스키 인증 창을 항상 위로 올리고 포커스를 강제합니다.",
                      "Keeps the Windows Hello / passkey prompt on top and focused.")
                + "</Description>");
            sb.AppendLine("    <URI>\\" + Program.TaskName + "</URI>");
            sb.AppendLine("  </RegistrationInfo>");
            sb.AppendLine("  <Triggers>");
            sb.AppendLine("    <LogonTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <UserId>" + Esc(user) + "</UserId>");
            // 로그온 직후는 드라이버 초기화와 자동 실행 프로그램이 몰리는 구간이다.
            // 인증 창은 그 시점에 뜰 일이 없으므로 잠시 물러나 있는 편이 안전하다.
            if (Program.LogonDelaySeconds > 0)
                sb.AppendLine("      <Delay>PT" + Program.LogonDelaySeconds + "S</Delay>");
            sb.AppendLine("    </LogonTrigger>");
            sb.AppendLine("  </Triggers>");
            sb.AppendLine("  <Principals>");
            sb.AppendLine("    <Principal id=\"Author\">");
            sb.AppendLine("      <UserId>" + Esc(user) + "</UserId>");
            sb.AppendLine("      <LogonType>InteractiveToken</LogonType>");
            sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
            sb.AppendLine("    </Principal>");
            sb.AppendLine("  </Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
            sb.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
            sb.AppendLine("    <IdleSettings>");
            sb.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
            sb.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
            sb.AppendLine("    </IdleSettings>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <Hidden>false</Hidden>");
            sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
            sb.AppendLine("    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>");
            sb.AppendLine("    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>");
            sb.AppendLine("    <WakeToRun>false</WakeToRun>");
            sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
            sb.AppendLine("    <Priority>7</Priority>");
            sb.AppendLine("    <RestartOnFailure>");
            sb.AppendLine("      <Interval>PT1M</Interval>");
            sb.AppendLine("      <Count>3</Count>");
            sb.AppendLine("    </RestartOnFailure>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\">");
            sb.AppendLine("    <Exec>");
            sb.AppendLine("      <Command>" + Esc(exePath) + "</Command>");
            sb.AppendLine("    </Exec>");
            sb.AppendLine("  </Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static void CreateShortcuts(Action<string> log)
        {
            // 본체를 직접 가리키면 일반 권한으로 떠서 인증 창을 조작하지 못한다.
            // 반드시 작업 스케줄러를 거치게 한다.
            CreateShortcut(ShortcutRun,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                "/run /tn \"" + Program.TaskName + "\"",
                TargetExe + ",0",
                L.T(Program.AppName + " 시작 (최고 권한)",
                    "Start " + Program.AppName + " (highest privileges)"),
                7);
            log("  " + ShortcutRun);

            CreateShortcut(ShortcutLog,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"),
                "\"" + Path.Combine(DataDir, "log.txt") + "\"",
                null,
                L.T(Program.AppName + " 로그 보기", "View " + Program.AppName + " log"),
                1);
            log("  " + ShortcutLog);
        }

        /// <summary>WScript.Shell 을 늦은 바인딩으로 써서 별도 COM 참조 없이 바로 가기를 만든다.</summary>
        private static void CreateShortcut(string path, string target, string arguments,
            string iconLocation, string description, int windowStyle)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            object shell = Activator.CreateInstance(shellType);
            try
            {
                object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                    null, shell, new object[] { path });
                Type lt = lnk.GetType();

                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                if (arguments != null)
                    lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { arguments });
                if (iconLocation != null)
                    lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { iconLocation });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { description });
                lt.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, lnk, new object[] { windowStyle });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            finally
            {
                try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
                catch { }
            }
        }

        private static void RegisterUninstallEntry()
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(UninstKeyPath))
            {
                if (k == null) return;
                string quoted = "\"" + UninstallerPath + "\"";
                long size = 0;
                try { size = new FileInfo(TargetExe).Length; }
                catch { }

                k.SetValue("DisplayName", L.T(
                    Program.AppName + " (Windows Hello 인증 창 포커스 도우미)",
                    Program.AppName + " (Windows Hello prompt focus helper)"));
                k.SetValue("DisplayVersion", Program.Version);
                k.SetValue("Publisher", Program.AppName);
                k.SetValue("DisplayIcon", TargetExe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", quoted + " /uninstall");
                k.SetValue("QuietUninstallString", quoted + " /uninstall /silent");
                k.SetValue("EstimatedSize", (int)(size / 1024),
                    Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
        }

        // ------------------------------------------------------------ 도우미

        private static int RunTool(string exe, string args)
        {
            string ignored;
            return RunTool(exe, args, out ignored);
        }

        private static int RunTool(string exe, string args, out string output)
        {
            output = string.Empty;
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet =
            System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string newName, int flags);

        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        private static void ScheduleDeleteOnReboot(string dir)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    MoveFileEx(f, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                MoveFileEx(dir, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------ 설치 화면

    internal sealed class WizardForm : Form
    {
        private readonly Label _title = new Label();
        private readonly Label _subtitle = new Label();
        // 안내문은 Label 로 둔다. 읽기 전용 TextBox 를 쓰면 내용이 전부 선택된 채
        // 파랗게 칠해져 보이고 캐럿까지 깜빡인다.
        private readonly Label _details = new Label();
        private readonly TextBox _logBox = new TextBox();
        private readonly CheckBox _runAfter = new CheckBox();
        private readonly Button _primary = new Button();
        private readonly Button _cancel = new Button();
        private readonly Panel _header = new Panel();

        private bool _installed;
        public bool Failed { get; private set; }

        public WizardForm()
        {
            Text = Program.AppName + " " + Program.Version + L.T(" 설치", " Setup");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 440);
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.White;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            _header.Dock = DockStyle.Top;
            _header.Height = 72;
            _header.BackColor = Color.FromArgb(247, 244, 239);

            _title.Text = Program.AppName;
            _title.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
            _title.ForeColor = Color.FromArgb(60, 45, 33);
            _title.SetBounds(20, 14, 480, 28);
            _title.BackColor = Color.Transparent;

            _subtitle.Text = L.T(
                "Windows Hello / 패스키 인증 창을 항상 위로 올리고 포커스를 가져옵니다.",
                "Brings the Windows Hello / passkey prompt to the front and gives it focus.");
            _subtitle.ForeColor = Color.FromArgb(110, 90, 70);
            _subtitle.SetBounds(22, 42, 480, 20);
            _subtitle.BackColor = Color.Transparent;

            _header.Controls.Add(_title);
            _header.Controls.Add(_subtitle);

            _details.AutoSize = false;
            _details.UseMnemonic = false;      // '&' 를 밑줄 기호로 해석하지 않도록
            _details.BackColor = Color.Transparent;
            _details.ForeColor = Color.FromArgb(45, 45, 45);
            _details.SetBounds(22, 88, 478, 252);
            _details.Text = BuildIntroText();

            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.TabStop = false;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.BackColor = Color.FromArgb(250, 250, 250);
            _logBox.Font = new Font("Consolas", 8.5F);
            _logBox.SetBounds(20, 88, 480, 252);
            _logBox.Visible = false;

            _runAfter.Text = L.T("설치 후 바로 실행", "Run after install");
            _runAfter.Checked = true;
            _runAfter.SetBounds(22, 352, 200, 22);

            _primary.Text = L.T("설치", "Install");
            _primary.SetBounds(300, 386, 95, 32);
            _primary.Click += OnPrimary;

            _cancel.Text = L.T("취소", "Cancel");
            _cancel.SetBounds(405, 386, 95, 32);
            _cancel.Click += delegate { Close(); };

            Controls.Add(_header);
            Controls.Add(_details);
            Controls.Add(_logBox);
            Controls.Add(_runAfter);
            Controls.Add(_primary);
            Controls.Add(_cancel);

            AcceptButton = _primary;
        }

        private static string BuildIntroText()
        {
            var sb = new StringBuilder();
            if (L.Ko)
            {
                sb.AppendLine("인증 창이 다른 창에 가려지거나 포커스를 받지 못해 지문 센서가 반응하지");
                sb.AppendLine("않는 문제를 해결합니다. 인증 자체를 대신하거나 우회하지는 않습니다.");
                sb.AppendLine();
                sb.AppendLine("설치 위치");
                sb.AppendLine("    " + Installer.InstallDir);
                sb.AppendLine();
                sb.AppendLine("설치 내용");
                sb.AppendLine("    · 로그온 시 자동 실행 (작업 스케줄러, UAC 창 없음)");
                sb.AppendLine("    · 시작 메뉴 바로 가기");
                sb.AppendLine("    · '앱 및 기능' 에 등록 (설정에서 제거 가능)");
                sb.AppendLine();
                sb.AppendLine("관리자 권한이 필요한 이유");
                sb.AppendLine("    인증 창을 띄우는 CredentialUIBroker 는 무결성 수준이 일반");
                sb.AppendLine("    프로그램보다 높게 설정되어 있어, 일반 권한으로는 Windows 가");
                sb.AppendLine("    창 조작을 거부합니다.");
            }
            else
            {
                sb.AppendLine("Fixes the case where the prompt appears behind other windows or without");
                sb.AppendLine("focus, so the fingerprint reader ignores your finger. It does not perform");
                sb.AppendLine("or bypass the authentication itself.");
                sb.AppendLine();
                sb.AppendLine("Install location");
                sb.AppendLine("    " + Installer.InstallDir);
                sb.AppendLine();
                sb.AppendLine("This will");
                sb.AppendLine("    · start automatically at logon (Task Scheduler, no UAC prompt)");
                sb.AppendLine("    · add Start menu shortcuts");
                sb.AppendLine("    · register in Apps & features so it can be uninstalled");
                sb.AppendLine();
                sb.AppendLine("Why administrator rights are required");
                sb.AppendLine("    CredentialUIBroker, which shows the prompt, runs at a higher");
                sb.AppendLine("    integrity level than ordinary programs, so Windows refuses");
                sb.AppendLine("    window manipulation from a normal process.");
            }
            return sb.ToString();
        }

        private void OnPrimary(object sender, EventArgs e)
        {
            if (_installed) { Close(); return; }

            _primary.Enabled = false;
            _cancel.Enabled = false;
            _details.Visible = false;
            _logBox.Visible = true;
            _subtitle.Text = L.T("설치하고 있습니다. 잠시만 기다려 주세요...",
                                 "Installing, please wait...");
            Application.DoEvents();

            try
            {
                Installer.Install(Log);

                if (_runAfter.Checked)
                {
                    Installer.StartApp();
                    Log("");
                    Log(L.T("프로그램을 시작했습니다. 트레이에 미어캣 아이콘이 나타납니다.",
                            "Started. A meerkat icon appears in the notification area."));
                }

                Log("");
                if (L.Ko)
                {
                    Log("확인 방법: 패스키/지문 인증을 한 번 띄운 뒤,");
                    Log("           시작 메뉴의 'OnTopPasskey 로그' 를 열어");
                    Log("           '포그라운드 획득' 이 기록되었는지 보면 됩니다.");
                }
                else
                {
                    Log("To verify: trigger a passkey / fingerprint prompt once, then open");
                    Log("           'OnTopPasskey Log' from the Start menu and check that");
                    Log("           'foreground acquired' was recorded.");
                }

                _installed = true;
                _subtitle.Text = L.T("설치가 완료되었습니다.", "Installation complete.");
                Finish();
            }
            catch (Exception ex)
            {
                Failed = true;
                Log("");
                Log(L.T("설치 실패: ", "Installation failed: ") + ex.Message);
                _subtitle.Text = L.T("설치에 실패했습니다.", "Installation failed.");
                _installed = true;
                Finish();
            }
        }

        /// <summary>설치가 끝난 뒤의 버튼 상태. 취소는 더 이상 의미가 없으므로 감춘다.</summary>
        private void Finish()
        {
            _primary.Text = L.T("닫기", "Close");
            _primary.Enabled = true;
            _cancel.Visible = false;
            _runAfter.Visible = false;
        }

        private void Log(string message)
        {
            _logBox.AppendText(message + Environment.NewLine);
            Application.DoEvents();
        }
    }
}
