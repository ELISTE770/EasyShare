using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using EasyShare.Services;
using Clipboard = System.Windows.Clipboard;

namespace EasyShare.Core;

/// <summary>
/// מנהל קיצור מקשים גלובלי (Alt+S) ברמת מערכת ההפעלה.
/// בלחיצה: שיתוף מהיר של קובץ מהלוח, תמונה מהלוח, או ביצוע צילום מסך חי והעלאתו.
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    private const int HOTKEY_ID = 9001;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_S = 0x53; // מקש 'S'

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private readonly Window _targetWindow;

    public event Action<string>? OnQuickShareCompleted; // מחזיר את הקישור שנוצר

    public GlobalHotkeyManager(Window window)
    {
        _targetWindow = window;
        try
        {
            var helper = new WindowInteropHelper(window);
            IntPtr handle = helper.EnsureHandle();
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(HwndHook);

            RegisterHotKey(handle, HOTKEY_ID, MOD_ALT | MOD_NOREPEAT, VK_S);
        }
        catch { }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            _ = HandleGlobalHotkeyTriggerAsync();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async Task HandleGlobalHotkeyTriggerAsync()
    {
        try
        {
            // 1. האם יש קובץ מועתק בלוח?
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0 && !string.IsNullOrEmpty(files[0]))
                {
                    string target = files[0]!;
                    string? url = await SecureTransferService.UploadToDirectPublicCloudAsync(target);
                    if (!string.IsNullOrEmpty(url))
                    {
                        Clipboard.SetText(url);
                        OnQuickShareCompleted?.Invoke(url);
                        return;
                    }
                }
            }

            // 2. האם יש תמונה מועתקת בלוח (למשל מ-Snipping Tool)?
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    string tempPng = Path.Combine(Path.GetTempPath(), $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                    using (var fs = new FileStream(tempPng, FileMode.Create))
                    {
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                        encoder.Save(fs);
                    }

                    string? url = await SecureTransferService.UploadToDirectPublicCloudAsync(tempPng);
                    if (!string.IsNullOrEmpty(url))
                    {
                        Clipboard.SetText(url);
                        OnQuickShareCompleted?.Invoke(url);
                        return;
                    }
                }
            }

            // 3. אם הלוח ריק - מבצע צילום מסך מלא של שולחן העבודה ומעלה מיד!
            string screenPath = CaptureFullScreen();
            string? directUrl = await SecureTransferService.UploadToDirectPublicCloudAsync(screenPath);
            if (!string.IsNullOrEmpty(directUrl))
            {
                Clipboard.SetText(directUrl);
                OnQuickShareCompleted?.Invoke(directUrl);
            }
        }
        catch { }
    }

    private static string CaptureFullScreen()
    {
        int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
        int screenHeight = (int)SystemParameters.PrimaryScreenHeight;

        using var bmp = new Bitmap(screenWidth, screenHeight);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(screenWidth, screenHeight));
        }

        string tempPath = Path.Combine(Path.GetTempPath(), $"ScreenCapture_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        bmp.Save(tempPath, ImageFormat.Png);
        return tempPath;
    }

    public void Dispose()
    {
        if (_source != null)
        {
            try
            {
                var helper = new WindowInteropHelper(_targetWindow);
                UnregisterHotKey(helper.Handle, HOTKEY_ID);
                _source.RemoveHook(HwndHook);
            }
            catch { }
            _source = null;
        }
    }
}
