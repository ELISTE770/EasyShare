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

    private const int ATTACH_PARENT_PROCESS = -1;
    private static System.Threading.Mutex? _mutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // בדיקה האם המשתמש הריץ עם --test משורת הפקודה
        if (Array.Exists(e.Args, a => a.Equals("--test", StringComparison.OrdinalIgnoreCase)))
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
            {
                AllocConsole();
            }

            Console.OutputEncoding = Encoding.UTF8;
            bool passed = await SelfTestRunner.RunAllTestsAsync();
            Environment.Exit(passed ? 0 : 1);
            return;
        }

        // בדיקת פקודת שיתוף מהיר מסייר הקבצים (Windows Shell Context Menu)
        int idx = Array.FindIndex(e.Args, a => a.Equals("--quick-share", StringComparison.OrdinalIgnoreCase));
        if (idx != -1 && e.Args.Length > idx + 2)
        {
            string channel = e.Args[idx + 1];
            string targetPath = e.Args[idx + 2];

            bool sent = await EasyShare.Services.SingleInstanceIpcService.SendShareCommandAsync(channel, targetPath);
            if (sent)
            {
                // נמסר למופע הפעיל ברקע - סגירת המופע המשני
                Environment.Exit(0);
                return;
            }
        }

        // מופע יחיד מערכתי (Global Mutex): מניעת הפעלת מופע כפול וקונפליקטים ביציאות רשת
        const string mutexName = "Global\\EasyShare_SingleInstance_Mutex_Eli";
        _mutex = new System.Threading.Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            // קיים כבר מופע פעיל - נעיר אותו ונביא אותו לקדמת המסך
            await EasyShare.Services.SingleInstanceIpcService.SendShareCommandAsync("ACTIVATE", "");
            Environment.Exit(0);
            return;
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
