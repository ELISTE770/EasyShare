using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using EasyShare.Tests;

namespace EasyShare;

public partial class App : System.Windows.Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(int dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    private static string GetCurrentDesktopName()
    {
        try
        {
            int tid = GetCurrentThreadId();
            IntPtr hDesk = GetThreadDesktop(tid);
            if (hDesk != IntPtr.Zero)
            {
                var sb = new StringBuilder(256);
                if (GetUserObjectInformation(hDesk, 2, sb, 256, out _))
                {
                    return sb.ToString();
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private const int ASFW_ANY = -1;
    private const int ATTACH_PARENT_PROCESS = -1;
    private static System.Threading.Mutex? _mutex;

    public static bool IsRunningTests { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash_appdomain.txt"), ev.ExceptionObject.ToString()); } catch { }
        };
        DispatcherUnhandledException += (s, ev) =>
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash_dispatcher.txt"), ev.Exception.ToString()); } catch { }
        };

        base.OnStartup(e);

        // בדיקה האם המשתמש הריץ עם --test משורת הפקודה
        string[] allArgs = Environment.GetCommandLineArgs();
        if (Array.Exists(allArgs, a => a.Equals("--test", StringComparison.OrdinalIgnoreCase)) ||
            (e.Args != null && Array.Exists(e.Args, a => a.Equals("--test", StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                IsRunningTests = true;
                if (!AttachConsole(ATTACH_PARENT_PROCESS))
                {
                    AllocConsole();
                }

                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Console.OutputEncoding = Encoding.UTF8;
                bool passed = await SelfTestRunner.RunAllTestsAsync();
                Environment.Exit(passed ? 0 : 1);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText("test_crash.txt", ex.ToString()); } catch { }
                Environment.Exit(1);
            }
            return;
        }

        // בדיקת פקודת שיתוף מהיר מסייר הקבצים (Windows Shell Context Menu)
        string[] activeArgs = (e.Args != null && e.Args.Length > 0) ? e.Args : allArgs;
        int idx = Array.FindIndex(activeArgs, a => a.Equals("--quick-share", StringComparison.OrdinalIgnoreCase));
        if (idx != -1 && activeArgs.Length > idx + 2)
        {
            string channel = activeArgs[idx + 1];
            string targetPath = activeArgs[idx + 2];

            bool sent = await EasyShare.Services.SingleInstanceIpcService.SendShareCommandAsync(channel, targetPath);
            if (sent)
            {
                // נמסר למופע הפעיל ברקע - סגירת המופע המשני
                Environment.Exit(0);
                return;
            }
        }

        void LogStartup(string msg)
        {
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup_trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n"); } catch { }
        }

        LogStartup("App OnStartup initiated.");

        string deskName = GetCurrentDesktopName();
        LogStartup($"Current thread desktop: '{deskName}'");
        bool isDefaultDesktop = string.IsNullOrEmpty(deskName) || string.Equals(deskName, "Default", StringComparison.OrdinalIgnoreCase);

        // מופע יחיד מערכתי (Mutex): מניעת הפעלת מופע כפול וקונפליקטים ביציאות רשת
        string mutexName = isDefaultDesktop 
            ? @"Local\EasyShare_SingleInstance_Mutex_Eli" 
            : $@"Local\EasyShare_SingleInstance_Mutex_Eli_{deskName}";

        if (!isDefaultDesktop)
        {
            EasyShare.Services.SingleInstanceIpcService.PipeName = $"EasyShare_SingleInstance_IPC_Pipe_{deskName}";
        }

        bool createdNew = true;
        try
        {
            _mutex = new System.Threading.Mutex(true, mutexName, out createdNew);
        }
        catch (Exception ex)
        {
            LogStartup($"Mutex creation warning: {ex.Message}");
            createdNew = true;
        }

        if (!createdNew)
        {
            LogStartup("Existing instance detected via Mutex, attempting IPC ACTIVATE...");
            AllowSetForegroundWindow(ASFW_ANY);
            bool activated = await EasyShare.Services.SingleInstanceIpcService.SendShareCommandAsync("ACTIVATE", "");

            // אימות שהמופע הקיים אכן מציג חלון גלוי פיזית על שולחן העבודה
            bool hasRealVisibleWindow = false;
            if (activated)
            {
                await Task.Delay(250);
                int currentPid = Environment.ProcessId;
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName("EasyShare"))
                {
                    if (proc.Id != currentPid)
                    {
                        proc.Refresh();
                        if (proc.MainWindowHandle != IntPtr.Zero && IsWindow(proc.MainWindowHandle) && IsWindowVisible(proc.MainWindowHandle))
                        {
                            hasRealVisibleWindow = true;
                            break;
                        }
                    }
                }
            }

            if (activated && hasRealVisibleWindow)
            {
                LogStartup("Activated existing instance with verified visible window successfully. Exiting secondary process.");
                Environment.Exit(0);
                return;
            }

            LogStartup($"Existing instance failed to present a visible window (activated={activated}, hasRealVisibleWindow={hasRealVisibleWindow}). Cleaning up stale/ghost processes...");
            try
            {
                int currentPid = Environment.ProcessId;
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName("EasyShare"))
                {
                    if (proc.Id != currentPid)
                    {
                        try { proc.Kill(); proc.WaitForExit(1000); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                LogStartup($"Error cleaning up stale processes: {ex.Message}");
            }

            await Task.Delay(200);
            try
            {
                _mutex?.Dispose();
                _mutex = new System.Threading.Mutex(true, mutexName, out createdNew);
            }
            catch { }
        }

        try
        {
            LogStartup("Instantiating MainWindow...");
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            LogStartup("Calling mainWindow.Show()...");
            mainWindow.Show();
            mainWindow.Activate();
            mainWindow.Focus();
            var helper = new System.Windows.Interop.WindowInteropHelper(mainWindow);
            LogStartup($"MainWindow HWND: {helper.Handle}, IsVisible: {mainWindow.IsVisible}, WindowState: {mainWindow.WindowState}");
        }
        catch (Exception ex)
        {
            LogStartup($"FATAL startup exception: {ex}");
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash_startup.txt"), ex.ToString()); } catch { }
            System.Windows.MessageBox.Show("Error starting EasyShare:\n" + ex.ToString(), "EasyShare Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
