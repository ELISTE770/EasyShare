using Microsoft.Toolkit.Uwp.Notifications;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using EasyShare.Core;
using EasyShare.Models;
using EasyShare.Services;
using EasyShare.Views;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using MessageBox = EasyShare.Views.ModernDialog;
using Clipboard = System.Windows.Clipboard;
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;

namespace EasyShare;

/// <summary>
/// ממשק המשתמש הראשי עבור EasyShare PRO (Desktop GUI).
/// מחבר את שרת הרשת המקומי, מנהור Cloudflare, שירותי ההעברה המאובטחת ומגש המערכת.
/// </summary>
public partial class MainWindow : Window
{
    private readonly LocalWebServerService _server;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private bool _isExplicitExit = false;

    private Views.FloatingDropZoneWindow? _dropZoneWindow;
    private Core.GlobalHotkeyManager? _hotkeyManager;
    private WatchFolderService? _watchFolderService;
    private readonly List<double> _speedHistory = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<PeerDevice> _discoveredPeers = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<PeerChatMessage> _activeChatMessages = new();
    private string? _activeChatPeerId;
    private bool _isProgrammaticTunnelToggle = false;

    public MainWindow()
    {
        _server = new LocalWebServerService();
        InitializeComponent();

        // רישום לאירועי שרת ומנהור
        _server.OnLog += msg => Dispatcher.Invoke(() => AppendLog(msg));
        _server.TunnelService.OnUrlChanged += url => Dispatcher.Invoke(() => HandleTunnelUrlChanged(url));
        _server.TunnelService.OnStateChanged += state => Dispatcher.Invoke(() => AppendLog($"[TUNNEL] {state}"));
        _server.TunnelService.OnError += err => Dispatcher.Invoke(() => AppendLog($"[TUNNEL ERROR] {err}"));
        Loaded += MainWindow_Loaded;
        Closing += (s, e) =>
        {
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup_trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [WINDOW] Closing event fired! Cancel={e.Cancel}, _isExplicitExit={_isExplicitExit}\r\n"); } catch { }
        };
        Closed += (s, e) =>
        {
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup_trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [WINDOW] Closed event fired!\r\n"); } catch { }
            if (App.IsRunningTests) return;
            try { ToastNotificationManagerCompat.History.Clear(); } catch { }
            try { SingleInstanceIpcService.StopIpcServer(); } catch { }
            try { _notifyIcon?.Dispose(); } catch { }
            try { _server.Dispose(); } catch { }
            Environment.Exit(0);
        };
        StateChanged += (s, e) =>
        {
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup_trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [WINDOW] StateChanged to {WindowState}\r\n"); } catch { }
        };
        IsVisibleChanged += (s, e) =>
        {
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup_trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [WINDOW] IsVisibleChanged to {IsVisible}\r\n"); } catch { }
        };
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            void LogLoad(string m) { try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup_trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [LOADED] {m}\r\n"); } catch { } }
            LogLoad("MainWindow_Loaded began.");

            // אתחול ערכת נושא (Theme)
            ThemeService.Instance.SetTheme(_server.Settings.ThemeMode ?? ThemeService.ThemeDark);
            ThemeService.Instance.OnThemeChanged += theme => Dispatcher.Invoke(() => ApplyThemeToUi(theme));

            LocalizationService.Instance.SetLanguage(_server.Settings.Language ?? "he");
            LocalizationService.Instance.OnLanguageChanged += lang => Dispatcher.Invoke(() => ApplyLanguage(lang));
            ApplyLanguage(LocalizationService.Instance.CurrentLanguage);
            ApplyThemeToUi(ThemeService.Instance.CurrentTheme);

            InitializeTrayIcon();
            LoadSettingsToUi();
            StartServer();
            TxtSecurePin.Text = SecureTransferService.GenerateRandomPin(8);
            RefreshTransfersList();
            UpdateCloudServicesStatus();

            // 1. הפעלת שרת IPC לקבלת שיתופים מסייר הקבצים
            SingleInstanceIpcService.StartIpcServer();
            SingleInstanceIpcService.OnShareRequestedWithResult += (channel, path) => Dispatcher.Invoke(() => HandleExternalShareRequest(channel, path));

            // 2. הפעלת מנהל קיצור מקשים גלובלי Alt+S
            if (_server.Settings.EnableGlobalHotkey)
            {
                try
                {
                    _hotkeyManager = new Core.GlobalHotkeyManager(this);
                    _hotkeyManager.OnQuickShareCompleted += url => Dispatcher.Invoke(() =>
                    {
                        var loc = LocalizationService.Instance;
                        ShowNotification(loc["HeaderTitle"], string.Format(loc["BalloonQuickShareCreated"], url));
                        AppendLog($"[HOTKEY] Created quick share: {url}");
                    });
                }
                catch (Exception ex)
                {
                    LogLoad($"Hotkey manager warning: {ex.Message}");
                }
            }

            // 3. הפעלת מעקב תיקיית סנכרון (Watch Folder)
            if (_server.Settings.EnableWatchFolder && !string.IsNullOrEmpty(_server.Settings.WatchFolderPath))
            {
                try
                {
                    _watchFolderService = new WatchFolderService(_server.Settings.WatchFolderPath);
                    _watchFolderService.OnFileSynced += (path, url) => Dispatcher.Invoke(() =>
                    {
                        var loc = LocalizationService.Instance;
                        ShowNotification(loc["BalloonAutoSyncTitle"], string.Format(loc["BalloonAutoSync"], Path.GetFileName(path)));
                        AppendLog($"[WATCH FOLDER] Auto-synced {Path.GetFileName(path)}: {url}");
                    });
                    _watchFolderService.Start();
                }
                catch (Exception ex)
                {
                    LogLoad($"WatchFolder warning: {ex.Message}");
                }
            }

            // 4. חיבור ניטור רשת חי
            try
            {
                LiveNetworkMonitorService.Instance.OnSpeedUpdated += speedMbps => Dispatcher.Invoke(() => UpdateSpeedGraph(speedMbps));
                LiveNetworkMonitorService.Instance.OnConnectionsChanged += () => Dispatcher.Invoke(RefreshLiveConnections);
                SecureTransferService.OnTransfersChanged += () => Dispatcher.Invoke(() =>
                {
                    RefreshTransfersList();
                    RefreshMonitorSharedLinks();
                });
            }
            catch (Exception ex)
            {
                LogLoad($"Monitor warning: {ex.Message}");
            }

            // 5. ניטור חסימות IP וצ'אט מהיר
            try
            {
                RefreshBlockedIpsList();
                RefreshMonitorSharedLinks();
                _server.OnIpBlacklistedChanged += _ => Dispatcher.Invoke(RefreshBlockedIpsList);
                _server.OnMessageReceived += msg => Dispatcher.Invoke(() =>
                {
                    AppendLog($"[CHAT/DROP] מ-{msg.Sender}: {msg.Text}");
                    if (TxtClipboardPreview != null)
                    {
                        TxtClipboardPreview.Text = msg.Text.Length > 25 ? msg.Text[..25] + "..." : msg.Text;
                    }
                    ShowNotification("EasyShare PRO | הודעה חדשה", $"{msg.Sender}: {msg.Text}");
                });
            }
            catch (Exception ex)
            {
                LogLoad($"Chat/Blacklist init warning: {ex.Message}");
            }

            // 6. סנכרון תמידי לשינוי ערכת נושא של מערכת ההפעלה (WM_SETTINGCHANGE)
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                source?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    const int WM_SETTINGCHANGE = 0x001A;
                    if (msg == WM_SETTINGCHANGE)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ThemeService.Instance.SyncWithSystemTheme();
                            ApplyThemeToUi(ThemeService.Instance.CurrentTheme);
                        });
                    }
                    return IntPtr.Zero;
                });
            }
            catch { }

            // 7. בדיקת ווידג'ט צף
            if (_server.Settings.EnableFloatingDropZone)
            {
                ToggleDropZone();
            }

            // 8. אתחול שירות גילוי עמיתים ודרופ ישיר
            LstDiscoveredPeers.ItemsSource = _discoveredPeers;
            LstChatMessages.ItemsSource = _activeChatMessages;
            TxtMyPeerIdHeader.Text = _server.PeerDiscovery.LocalPeerId;
            TxtLocalPeerIdDisplay.Text = _server.PeerDiscovery.LocalPeerId;
            TxtLocalDeviceName.Text = _server.PeerDiscovery.LocalDeviceName;
            ChkPeerDiscoveryActive.IsChecked = _server.Settings.EnablePeerDiscovery;
            ChkAutoAcceptDrops.IsChecked = _server.Settings.AutoAcceptPeerDrops;

            if (_server.Settings.InternetVisibilityMode == "Visible")
            {
                RbVisibilityVisible.IsChecked = true;
            }
            else
            {
                RbVisibilityHidden.IsChecked = true;
            }

            _server.PeerDiscovery.OnPeerDiscovered += peer => Dispatcher.Invoke(() => AddOrUpdatePeerInUi(peer));
            _server.PeerDiscovery.OnPeerUpdated += peer => Dispatcher.Invoke(() => AddOrUpdatePeerInUi(peer));
            _server.PeerDiscovery.OnPeerLost += peerId => Dispatcher.Invoke(() => RemovePeerFromUi(peerId));
            _server.PeerDiscovery.OnDirectFileReceived += (senderId, senderName, fileName, savedPath) => Dispatcher.Invoke(() => HandleIncomingFileDrop(senderId, senderName, fileName, savedPath));
            _server.PeerDiscovery.OnDirectMessageReceived += (senderId, senderName, message) => Dispatcher.Invoke(() => HandleIncomingPeerMessage(senderId, senderName, message));
            _server.PeerDiscovery.OnChatMessageAdded += msg => Dispatcher.Invoke(() => HandleChatMessageAdded(msg));

            foreach (var p in _server.PeerDiscovery.GetDiscoveredPeers())
            {
                _discoveredPeers.Add(p);
            }

            // פתיחת טאב עמיתים וצ'אט כברירת מחדל (לבקשת המשתמש)
            MainTabControl.SelectedIndex = 1;
            if (_discoveredPeers.Count > 0)
            {
                SelectPeerForChat(_discoveredPeers[0]);
            }

            // 9. בדיקת עדכונים שקטה ברקע מ-GitHub
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3000);
                    await CheckForUpdatesInternalAsync(isManualCheck: false);
                }
                catch { }
            });

            LogLoad("MainWindow_Loaded completed successfully.");
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash_loaded.txt"), ex.ToString()); } catch { }
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup_trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [FATAL LOADED] {ex}\r\n"); } catch { }
        }
    }

    private void ShowNotification(string title, string text)
    {
        try 
        {
            new Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder()
                .AddText(title)
                .AddText(text)
                .Show();
        }
        catch (Exception ex)
        {
            AppendLog($"[TOAST ERROR] {ex.Message}");
        }
    }

    private void UpdateCloudServicesStatus()
    {
        _ = Task.Run(() => 
        {
            try
            {
                var oneDrive = CloudDriveService.GetOneDriveInfo();
                var gdrive = CloudDriveService.GetGoogleDriveInfo();
                
                Dispatcher.Invoke(() => 
                {
                    bool isHe = LocalizationService.Instance.CurrentLanguage == LocalizationService.LanguageHebrew;
                    string od = oneDrive.IsAvailableLocally 
                        ? (isHe ? "OneDrive: מסונכרן מקומית" : "OneDrive: Locally synced")
                        : (isHe ? "OneDrive: זמין ב-Web" : "OneDrive: Available on Web");
                    string gd = gdrive.IsAvailableLocally 
                        ? (isHe ? "Google Drive: כונן מקומי פעיל" : "Google Drive: Local drive active")
                        : (isHe ? "Google Drive: זמין ב-Web" : "Google Drive: Available on Web");

                    TxtCloudServicesStatus.Text = $"{od}  |  {gd}";
                });
            }
            catch { }
        });
    }

    #region הגדרות מגש מערכת (System Tray)

    private void InitializeTrayIcon()
    {
        try
        {
            System.Drawing.Icon? appIcon = null;
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (!File.Exists(iconPath)) iconPath = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "app.ico");

                if (File.Exists(iconPath))
                {
                    appIcon = new System.Drawing.Icon(iconPath);
                }
                else
                {
                    var uri = new Uri("pack://application:,,,/Assets/app.ico");
                    var sri = Application.GetResourceStream(uri);
                    if (sri != null)
                    {
                        using var s = sri.Stream;
                        appIcon = new System.Drawing.Icon(s);
                    }
                }
            }
            catch { }

            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = appIcon ?? System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = LocalizationService.Instance["HeaderTitle"]
            };

            UpdateTrayMenu();
            _notifyIcon.DoubleClick += (s, e) => Dispatcher.Invoke(RestoreFromTray);
        }
        catch (Exception ex)
        {
            AppendLog($"[TRAY WARNING] Could not initialize tray icon: {ex.Message}");
        }
    }

    private void UpdateTrayMenu()
    {
        if (_notifyIcon == null) return;

        var loc = LocalizationService.Instance;
        var menu = new System.Windows.Forms.ContextMenuStrip();

        menu.Items.Add(loc["TrayOpen"], null, (s, e) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add(loc["TrayDropZone"], null, (s, e) => Dispatcher.Invoke(ToggleDropZone));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        menu.Items.Add(loc["TrayCopyLocal"], null, (s, e) => Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrEmpty(_server.LocalUrl)) Clipboard.SetText(_server.LocalUrl);
        }));

        menu.Items.Add(loc["TrayCopyTunnel"], null, (s, e) => Dispatcher.Invoke(() =>
        {
            string url = _server.TunnelService.CurrentUrl ?? TxtTunnelUrl.Text;
            if (!string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                Clipboard.SetText(url);
            }
        }));

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(loc["TrayExit"], null, (s, e) => Dispatcher.Invoke(ExitApplication));

        _notifyIcon.ContextMenuStrip = menu;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fUnknown);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private const int SW_SHOWNORMAL = 1;
    private const int SW_RESTORE = 9;

    private void RestoreFromTray()
    {
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            helper.EnsureHandle();

            if (Visibility != Visibility.Visible)
            {
                Show();
            }
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            if (helper.Handle != IntPtr.Zero)
            {
                ShowWindowAsync(helper.Handle, SW_RESTORE);
                SetForegroundWindow(helper.Handle);
                SwitchToThisWindow(helper.Handle, true);
            }
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup_trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [RESTORE ERROR] {ex.Message}\r\n"); } catch { }
        }
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;
        try { ToastNotificationManagerCompat.History.Clear(); } catch { }
        try { SingleInstanceIpcService.StopIpcServer(); } catch { }
        try { _server.Dispose(); } catch { }
        try { _notifyIcon?.Dispose(); } catch { }
        _notifyIcon = null;
        try { Close(); } catch { }
        try { Application.Current?.Shutdown(); } catch { }
        if (!App.IsRunningTests)
        {
            Environment.Exit(0);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup_trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [WINDOW] OnClosing called! _isExplicitExit={_isExplicitExit}\r\n"); } catch { }
        if (_isExplicitExit)
        {
            try { SingleInstanceIpcService.StopIpcServer(); } catch { }
            try { _server.Dispose(); } catch { }
            try { _notifyIcon?.Dispose(); } catch { }
            _notifyIcon = null;
            base.OnClosing(e);
            if (!App.IsRunningTests)
            {
                Environment.Exit(0);
            }
            return;
        }

        bool minimizeToTray = !string.Equals(_server.Settings.CloseAction, "ExitApplication", StringComparison.OrdinalIgnoreCase);
        if (minimizeToTray && _notifyIcon != null)
        {
            // מזעור למגש המערכת במקום סגירה מלאה שמפילה את השרת
            e.Cancel = true;
            Hide();
            var loc = LocalizationService.Instance;
            ShowNotification(loc["HeaderTitle"], loc["BalloonServerRunningBg"]);
            return;
        }

        // סגירה רגילה של החלון ושחרור משאבים
        _isExplicitExit = true;
        try { SingleInstanceIpcService.StopIpcServer(); } catch { }
        try { _server.Dispose(); } catch { }
        try { _notifyIcon?.Dispose(); } catch { }
        _notifyIcon = null;
        base.OnClosing(e);
        if (!App.IsRunningTests)
        {
            Environment.Exit(0);
        }
    }

    private void BtnMinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        var loc = LocalizationService.Instance;
        ShowNotification(loc["HeaderTitle"], loc["BalloonTrayMinimized"]);
    }

    #endregion

    #region ניהול שרת רשת ומדיה

    private enum ServerState
    {
        Running,
        Stopped,
        Error
    }

    private void ApplyServerState(ServerState state, string? errorMessage = null)
    {
        var loc = LocalizationService.Instance;
        switch (state)
        {
            case ServerState.Running:
                TxtServerStatus.Text = loc["ServerRunning"];
                TxtServerStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Emerald Green
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                BtnToggleServerTop.Content = loc["StopServer"];
                BtnToggleServerTop.Background = new SolidColorBrush(Color.FromArgb(40, 239, 68, 68));
                BtnToggleServerTop.BorderBrush = new SolidColorBrush(Color.FromArgb(180, 239, 68, 68));
                BtnToggleServerTop.BorderThickness = new Thickness(1);
                BtnToggleServerTop.Foreground = new SolidColorBrush(Color.FromRgb(254, 202, 202));

                TxtLocalUrl.Text = _server.LocalUrl;
                UpdateQrCodeImage(_server.LocalUrl);
                TxtFooterInfo.Text = $"{loc["HeaderTitle"]} | Port {_server.Port} | Mode: {_server.Settings.AccessMode}";
                break;

            case ServerState.Stopped:
                TxtServerStatus.Text = loc["ServerStopped"];
                TxtServerStatus.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)); // Gray Muted
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                BtnToggleServerTop.Content = loc["StartServer"];
                BtnToggleServerTop.Background = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129));
                BtnToggleServerTop.BorderBrush = new SolidColorBrush(Color.FromArgb(180, 16, 185, 129));
                BtnToggleServerTop.BorderThickness = new Thickness(1);
                BtnToggleServerTop.Foreground = new SolidColorBrush(Color.FromRgb(167, 243, 208));

                TxtLocalUrl.Text = string.Empty;
                ImgQrCode.Source = null;
                TxtFooterInfo.Text = $"{loc["HeaderTitle"]} | {loc["ServerStopped"]}";
                break;

            case ServerState.Error:
                TxtServerStatus.Text = loc["MsgErrorTitle"];
                TxtServerStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                BtnToggleServerTop.Content = loc["StartServer"];
                BtnToggleServerTop.Background = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129));
                BtnToggleServerTop.BorderBrush = new SolidColorBrush(Color.FromArgb(180, 16, 185, 129));
                BtnToggleServerTop.BorderThickness = new Thickness(1);
                BtnToggleServerTop.Foreground = new SolidColorBrush(Color.FromRgb(167, 243, 208));

                TxtLocalUrl.Text = string.Empty;
                ImgQrCode.Source = null;
                TxtFooterInfo.Text = $"{loc["HeaderTitle"]} | {errorMessage ?? loc["MsgErrorTitle"]}";
                break;
        }
    }

    private void StartServer()
    {
        try
        {
            _server.Start();
            ApplyServerState(ServerState.Running);
        }
        catch (Exception ex)
        {
            ApplyServerState(ServerState.Error, ex.Message);
            AppendLog($"[SERVER ERROR] {ex.Message}");
            MessageBox.Show($"Error starting server on port {_server.Port}:\n{ex.Message}", LocalizationService.Instance["MsgErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopServer()
    {
        _server.Stop();
        ApplyServerState(ServerState.Stopped);
        AppendLog("[SERVER] Server was stopped by user.");
    }

    private void BtnToggleServer_Click(object sender, RoutedEventArgs e)
    {
        if (_server.IsRunning)
        {
            StopServer();
        }
        else
        {
            StartServer();
        }
    }

    private void BtnOpenLocalBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_server.LocalUrl))
        {
            OpenUrlInBrowser(_server.LocalUrl);
        }
    }

    private void BtnCopyLocalUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtLocalUrl.Text))
        {
            Clipboard.SetText(TxtLocalUrl.Text);
            AppendLog($"[CLIPBOARD] Copied local URL: {TxtLocalUrl.Text}");
        }
    }

    private void RbTunnelMode_Changed(object sender, RoutedEventArgs e)
    {
        if (PnlCustomDomainConfig == null || RbTunnelCustom == null) return;
        bool isCustom = (RbTunnelCustom.IsChecked == true);
        PnlCustomDomainConfig.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

        if (_server?.Settings != null)
        {
            _server.Settings.TunnelMode = isCustom ? "CustomDomain" : "Random";
        }
    }

    private void SetTunnelUrlText(string text, bool isError = false)
    {
        if (TxtTunnelUrl == null) return;

        bool isUrl = text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (isUrl)
        {
            TxtTunnelUrl.FlowDirection = System.Windows.FlowDirection.LeftToRight;
            TxtTunnelUrl.TextAlignment = TextAlignment.Left;
            TxtTunnelUrl.Text = text;
            TxtTunnelUrl.Foreground = (Brush)FindResource("BrushAccentCyan");
        }
        else
        {
            bool isHe = LocalizationService.Instance.CurrentLanguage == "he";
            TxtTunnelUrl.FlowDirection = isHe ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;
            TxtTunnelUrl.TextAlignment = isHe ? TextAlignment.Right : TextAlignment.Left;

            // הוספת תו כיווניות RLM בסוף המחרוזת למניעת היפוך סוגריים וסימני פיסוק בעברית
            string displayText = (isHe && !text.EndsWith("\u200F")) ? text + "\u200F" : text;
            TxtTunnelUrl.Text = displayText;
            TxtTunnelUrl.Foreground = isError
                ? (Brush)FindResource("BrushDanger")
                : (Brush)FindResource("BrushTextMuted");
        }
    }

    private void HandleTunnelUrlChanged(string url)
    {
        SetTunnelUrlText(url);
        BtnOpenTunnelBrowser.IsEnabled = true;
        BtnCopyTunnelUrl.IsEnabled = true;
        UpdateQrCodeImage(url);
        AppendLog($"[TUNNEL] Public HTTPS URL active: {url}");
    }

    private void ChkEnableTunnel_Checked(object sender, RoutedEventArgs e)
    {
        string mode = (RbTunnelCustom.IsChecked == true) ? "CustomDomain" : "Random";
        string? token = TxtTunnelToken.Text?.Trim();
        string? customDomain = TxtCustomDomainUrl.Text?.Trim();

        if (mode == "CustomDomain" && string.IsNullOrWhiteSpace(token))
        {
            var loc = LocalizationService.Instance;
            MessageBox.Show(loc["MissingTunnelTokenMsg"], loc["MissingTunnelTokenTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            ChkEnableTunnel.IsChecked = false;
            return;
        }

        // שמירת הגדרות מעודכנות
        _server.Settings.TunnelMode = mode;
        _server.Settings.CloudflareTunnelToken = token;
        _server.Settings.CloudflareCustomDomain = customDomain;
        _server.Settings.EnableCloudflareTunnel = true;
        _server.SettingsManager.Save();

        if (mode == "CustomDomain" && !string.IsNullOrWhiteSpace(customDomain))
        {
            string displayUrl = customDomain.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? customDomain : $"https://{customDomain}";
            SetTunnelUrlText(displayUrl);
            BtnOpenTunnelBrowser.IsEnabled = true;
            BtnCopyTunnelUrl.IsEnabled = true;
            UpdateQrCodeImage(displayUrl);
        }
        else
        {
            SetTunnelUrlText("יוצר חיבור מנהור מהיר דרך Cloudflare...");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                bool ok = await _server.TunnelService.StartAsync(
                    _server.Port,
                    mode,
                    token,
                    customDomain,
                    msg => Dispatcher.Invoke(() => AppendLog($"[TUNNEL] {msg}")));

                if (!ok)
                {
                    Dispatcher.Invoke(() =>
                    {
                        string errMsg = "שגיאה בהפעלת מנהור Cloudflare (בדוק חיבור אינטרנט או תקינות ה-Token).";
                        _isProgrammaticTunnelToggle = true;
                        try { ChkEnableTunnel.IsChecked = false; } finally { _isProgrammaticTunnelToggle = false; }
                        SetTunnelUrlText(errMsg, isError: true);
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    string errMsg = $"שגיאה בהפעלת מנהור: {ex.Message}";
                    _isProgrammaticTunnelToggle = true;
                    try { ChkEnableTunnel.IsChecked = false; } finally { _isProgrammaticTunnelToggle = false; }
                    SetTunnelUrlText(errMsg, isError: true);
                    AppendLog($"[TUNNEL ERROR] {ex.Message}");
                });
            }
        });
    }

    private void ChkEnableTunnel_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isProgrammaticTunnelToggle) return;

        _server.Settings.EnableCloudflareTunnel = false;
        _server.SettingsManager.Save();

        _server.TunnelService.Stop();
        SetTunnelUrlText(LocalizationService.Instance["TunnelDisabled"]);
        BtnOpenTunnelBrowser.IsEnabled = false;
        BtnCopyTunnelUrl.IsEnabled = false;

        // החזרת ה-QR לכתובת המקומית
        if (!string.IsNullOrEmpty(_server.LocalUrl))
        {
            UpdateQrCodeImage(_server.LocalUrl);
        }

        AppendLog("[TUNNEL] Cloudflare Tunnel stopped.");
    }

    private void BtnOpenTunnelBrowser_Click(object sender, RoutedEventArgs e)
    {
        string url = _server.TunnelService.CurrentUrl ?? TxtTunnelUrl.Text;
        if (!string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            OpenUrlInBrowser(url);
        }
    }

    private void BtnCopyTunnelUrl_Click(object sender, RoutedEventArgs e)
    {
        string url = _server.TunnelService.CurrentUrl ?? TxtTunnelUrl.Text;
        if (!string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Clipboard.SetText(url);
            AppendLog($"[CLIPBOARD] Copied tunnel URL: {url}");
        }
    }

    private void UpdateQrCodeImage(string url)
    {
        try
        {
            ImgQrCode.Source = GenerateQrBitmapSource(url, 6);
        }
        catch (Exception ex)
        {
            AppendLog($"[QR ERROR] {ex.Message}");
        }
    }

    private static BitmapSource GenerateQrBitmapSource(string text, int moduleSize = 6)
    {
        bool[,] matrix = QrCodeGenerator.GenerateQrMatrix(text);
        int size = matrix.GetLength(0);
        int quietZone = 4;
        int totalModules = size + (quietZone * 2);
        int pixelWidth = totalModules * moduleSize;
        int pixelHeight = totalModules * moduleSize;
        int stride = pixelWidth * 4;
        byte[] pixels = new byte[pixelHeight * stride];

        // רקע לבן
        Array.Fill(pixels, (byte)255);

        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                if (matrix[r, c])
                {
                    int startX = (c + quietZone) * moduleSize;
                    int startY = (r + quietZone) * moduleSize;
                    for (int y = 0; y < moduleSize; y++)
                    {
                        int rowOffset = (startY + y) * stride;
                        for (int x = 0; x < moduleSize; x++)
                        {
                            int offset = rowOffset + ((startX + x) * 4);
                            pixels[offset] = 0;       // Blue
                            pixels[offset + 1] = 0;   // Green
                            pixels[offset + 2] = 0;   // Red
                            pixels[offset + 3] = 255; // Alpha
                        }
                    }
                }
            }
        }

        var bitmap = BitmapSource.Create(
            pixelWidth,
            pixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride
        );
        bitmap.Freeze();
        return bitmap;
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        TxtEventLog.AppendText(line + Environment.NewLine);
        TxtEventLog.ScrollToEnd();
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        TxtEventLog.Clear();
    }

    private void BtnCopyQrImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ImgQrCode?.Source is BitmapSource bs)
            {
                Clipboard.SetImage(bs);
                var loc = LocalizationService.Instance;
                ShowNotification(loc["HeaderTitle"], "תמונת ה-QR הועתקה ללוח בהצלחה!");
                AppendLog("[QR] Copied QR Code image to clipboard.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[QR ERROR] Could not copy QR image: {ex.Message}");
        }
    }

    private void BtnSaveQrImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ImgQrCode?.Source is BitmapSource bs)
            {
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "PNG Image (*.png)|*.png",
                    FileName = "EasyShare_QR.png"
                };
                if (sfd.ShowDialog() == true)
                {
                    using var fileStream = new FileStream(sfd.FileName, FileMode.Create);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bs));
                    encoder.Save(fileStream);
                    AppendLog($"[QR] Saved QR image to: {sfd.FileName}");
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[QR ERROR] Could not save QR image: {ex.Message}");
        }
    }

    private static void OpenUrlInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            var loc = LocalizationService.Instance;
            MessageBox.Show(string.Format(loc["BrowserOpenError"], ex.Message), loc["MsgErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    #endregion

    #region ניהול שיתוף מאובטח והעלאה לענן (Security & Cloud Drops)

    private void BtnSelectFile_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = loc["ChooseFileShareTitle"],
            Filter = loc["FilterAllFiles"]
        };

        if (dlg.ShowDialog() == true)
        {
            TxtSecureTarget.Text = dlg.FileName;
        }
    }

    private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = loc["ChooseFolderShareTitle"]
        };

        if (dlg.ShowDialog() == true)
        {
            TxtSecureTarget.Text = dlg.FolderName;
        }
    }

    private void BtnGenPin_Click(object sender, RoutedEventArgs e)
    {
        TxtSecurePin.Text = SecureTransferService.GenerateRandomPin(8);
    }

    private async void BtnCreateSecureShare_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        string targetPath = TxtSecureTarget.Text.Trim();
        if (string.IsNullOrEmpty(targetPath) || targetPath == loc["ChoosePlaceholder"] || targetPath == "בחר קובץ או תיקייה לשיתוף..." || targetPath == "Select file or folder to share..." || (!File.Exists(targetPath) && !Directory.Exists(targetPath)))
        {
            MessageBox.Show(loc["SelectValidFileOrFolder"], loc["MsgErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string channel = (CmbShareChannel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "LAN";

        if (channel == "GoogleDrive")
        {
            await ExecuteCloudShareAsync("GoogleDrive", targetPath);
            return;
        }
        if (channel == "OneDrive")
        {
            await ExecuteCloudShareAsync("OneDrive", targetPath);
            return;
        }

        string pin = TxtSecurePin.Text.Trim();
        if (string.IsNullOrWhiteSpace(pin))
        {
            pin = SecureTransferService.GenerateRandomPin(8);
            TxtSecurePin.Text = pin;
        }

        int expMinutes = int.TryParse((CmbExpiration.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int exp) ? exp : 60;
        int maxDownloads = int.TryParse((CmbMaxDownloads.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int dl) ? dl : 1;

        string? baseUrl = null;
        if (channel == "LAN")
        {
            baseUrl = _server.LocalUrl;
        }
        else if (channel == "Tunnel")
        {
            if (!_server.TunnelService.IsRunning || string.IsNullOrEmpty(_server.TunnelService.CurrentUrl))
            {
                var answer = MessageBox.Show(
                    loc["PromptStartTunnel"],
                    loc["PromptStartTunnelTitle"],
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (answer == MessageBoxResult.Yes)
                {
                    ChkEnableTunnel.IsChecked = true;
                    // נמתין לקבלת URL
                    for (int i = 0; i < 30 && string.IsNullOrEmpty(_server.TunnelService.CurrentUrl); i++)
                    {
                        await Task.Delay(500);
                    }
                }

                if (string.IsNullOrEmpty(_server.TunnelService.CurrentUrl))
                {
                    MessageBox.Show(loc["MsgTunnelInactive"], loc["MsgTunnelInactiveTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            baseUrl = _server.TunnelService.CurrentUrl;
        }

        BtnCreateSecureShare.IsEnabled = false;
        BtnCreateSecureShare.Content = channel == "DirectCloud" ? loc["BtnCreateShareDirectCloudLoading"] : loc["BtnCreateShareLoading"];

        try
        {
            var item = await _server.SecurityService.CreateTransferAsync(
                targetPath: targetPath,
                pinCode: pin,
                expirationMinutes: expMinutes,
                maxDownloads: maxDownloads,
                channel: channel,
                serverBaseUrl: baseUrl
            );

            _lastSharedCloudPath = targetPath;
            BorderShareResult.Visibility = Visibility.Visible;
            if (channel == "DirectCloud")
            {
                TxtResultHeader.Text = loc["DirectCloudSuccessHeader"];
                TxtResultUrl.Text = item.ShareUrl ?? "";
                TxtResultPinDisplay.Text = loc["DirectCloudPinNote"];
            }
            else
            {
                TxtResultHeader.Text = loc["SecureShareSuccessHeader"];
                TxtResultUrl.Text = item.ShareUrl ?? "";
                TxtResultPinDisplay.Text = item.PinCode;
            }

            // העתקה אוטומטית של הקישור ללוח
            if (!string.IsNullOrEmpty(item.ShareUrl))
            {
                Clipboard.SetText(item.ShareUrl);
            }

            RefreshTransfersList();
            AppendLog($"[SECURE SHARE] Created {channel} share: {item.FileName} (PIN: {item.PinCode})");
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(loc["ErrorCreatingShare"], ex.Message), loc["MsgErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnCreateSecureShare.IsEnabled = true;
            BtnCreateSecureShare.Content = loc["BtnCreateShare"];
        }
    }

    private string? _lastSharedCloudPath;

    private async void BtnShareGoogleDriveDirect_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        string path = TxtSecureTarget.Text.Trim();
        if (string.IsNullOrEmpty(path) || path == loc["ChoosePlaceholder"] || path == "בחר קובץ או תיקייה לשיתוף..." || path == "Select file or folder to share..." || (!File.Exists(path) && !Directory.Exists(path)))
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = loc["ChooseFileGdriveTitle"],
                Filter = loc["FilterAllFiles"]
            };
            if (dlg.ShowDialog() == true)
            {
                path = dlg.FileName;
                TxtSecureTarget.Text = path;
            }
            else return;
        }

        await ExecuteCloudShareAsync("GoogleDrive", path);
    }

    private async void BtnShareOneDriveDirect_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        string path = TxtSecureTarget.Text.Trim();
        if (string.IsNullOrEmpty(path) || path == loc["ChoosePlaceholder"] || path == "בחר קובץ או תיקייה לשיתוף..." || path == "Select file or folder to share..." || (!File.Exists(path) && !Directory.Exists(path)))
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = loc["ChooseFileOneDriveTitle"],
                Filter = loc["FilterAllFiles"]
            };
            if (dlg.ShowDialog() == true)
            {
                path = dlg.FileName;
                TxtSecureTarget.Text = path;
            }
            else return;
        }

        await ExecuteCloudShareAsync("OneDrive", path);
    }

    private async Task ExecuteCloudShareAsync(string provider, string path)
    {
        var loc = LocalizationService.Instance;
        try
        {
            BtnCreateSecureShare.IsEnabled = false;
            BtnCreateSecureShare.Content = loc["BtnCreateShareCloudProcessing"];

            CloudShareResult result;
            if (provider == "OneDrive")
            {
                result = await CloudDriveService.ShareViaOneDriveAsync(path);
            }
            else
            {
                result = await CloudDriveService.ShareViaGoogleDriveAsync(path);
            }

            _lastSharedCloudPath = result.SavedPath;

            BorderShareResult.Visibility = Visibility.Visible;
            TxtResultHeader.Text = result.Message;
            TxtResultUrl.Text = !string.IsNullOrEmpty(result.SavedPath) ? result.SavedPath : result.WebUrl;
            TxtResultPinDisplay.Text = string.Format(loc["SecuredInAccount"], result.ProviderName);

            SecureTransferService.RegisterExternalCloudTransfer(
                Path.GetFileName(result.SavedPath),
                result.SavedPath,
                result.ProviderName,
                result.WebUrl);

            RefreshTransfersList();
            AppendLog($"[CLOUD SHARE] Shared {Path.GetFileName(result.SavedPath)} via {result.ProviderName}");

            MessageBox.Show(result.Message, loc["MsgCloudShareComplete"], MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(loc["ErrorCloudShare"], provider, ex.Message), loc["CloudShareErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnCreateSecureShare.IsEnabled = true;
            BtnCreateSecureShare.Content = loc["BtnCreateShare"];
        }
    }

    private void BtnResultOpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        string url = TxtResultUrl.Text.Trim();
        if (!string.IsNullOrEmpty(url) && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            OpenUrlInBrowser(url);
        }
        else if (!string.IsNullOrEmpty(_lastSharedCloudPath))
        {
            var oneDrive = CloudDriveService.GetOneDriveInfo();
            var gdrive = CloudDriveService.GetGoogleDriveInfo();
            string webUrl = _lastSharedCloudPath.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ? oneDrive.WebUrl : gdrive.WebUrl;
            OpenUrlInBrowser(webUrl);
        }
    }

    private void BtnResultShowExplorer_Click(object sender, RoutedEventArgs e)
    {
        string path = _lastSharedCloudPath ?? TxtSecureTarget.Text.Trim();
        if (File.Exists(path) || Directory.Exists(path))
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch { }
        }
        else if (Directory.Exists(Path.GetDirectoryName(path)))
        {
            try
            {
                Process.Start("explorer.exe", Path.GetDirectoryName(path)!);
            }
            catch { }
        }
    }

    private void BtnCopyShareUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtResultUrl.Text))
        {
            Clipboard.SetText(TxtResultUrl.Text);
            AppendLog($"[CLIPBOARD] Copied share link: {TxtResultUrl.Text}");
        }
    }

    private void BtnCopyShareFullMessage_Click(object sender, RoutedEventArgs e)
    {
        string link = TxtResultUrl.Text;
        string pin = TxtResultPinDisplay.Text;
        string target = Path.GetFileName(TxtSecureTarget.Text);
        var loc = LocalizationService.Instance;

        string message = string.Format(loc["ShareMessageTemplate"], target, link, pin);

        Clipboard.SetText(message);
        MessageBox.Show(loc["MsgReadyMessageCopied"], loc["MsgCopied"], MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnCancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string token)
        {
            SecureTransferService.CancelTransfer(token);
            RefreshTransfersList();
            AppendLog($"[TRANSFER CANCELLED] Token: {token}");
        }
    }

    private void RefreshTransfersList()
    {
        var items = SecureTransferService.GetAllActiveTransfers();
        LstActiveTransfers.ItemsSource = null;
        LstActiveTransfers.ItemsSource = items;
    }

    #endregion

    #region ניהול הגדרות מתקדמות (Advanced Settings)

    private void LoadSettingsToUi()
    {
        var settings = _server.Settings;
        TxtSettingsPort.Text = settings.ServerPort.ToString();

        // מצב גישה
        foreach (ComboBoxItem item in CmbSettingsAccessMode.Items)
        {
            if (string.Equals(item.Tag?.ToString(), settings.AccessMode, StringComparison.OrdinalIgnoreCase))
            {
                CmbSettingsAccessMode.SelectedItem = item;
                break;
            }
        }

        TxtSettingsSharedFolder.Text = settings.SharedFolderPath;
        ChkSettingsRecycleBin.IsChecked = settings.SendToRecycleBin;
        ChkSettingsReadOnly.IsChecked = settings.ServerReadOnly;
        ChkSettingsHidden.IsChecked = settings.ShowHiddenFiles;
        ChkSettingsHotkey.IsChecked = settings.EnableGlobalHotkey;
        ChkSettingsDropZone.IsChecked = settings.EnableFloatingDropZone;
        ChkSettingsContextMenu.IsChecked = ShellContextMenuService.IsContextMenuRegistered();
        ChkSettingsAutoStartTunnel.IsChecked = settings.EnableCloudflareTunnel;

        // הגדרות מנהור Cloudflare
        if (string.Equals(settings.TunnelMode, "CustomDomain", StringComparison.OrdinalIgnoreCase))
        {
            RbTunnelCustom.IsChecked = true;
            PnlCustomDomainConfig.Visibility = Visibility.Visible;
        }
        else
        {
            RbTunnelRandom.IsChecked = true;
            PnlCustomDomainConfig.Visibility = Visibility.Collapsed;
        }

        TxtCustomDomainUrl.Text = settings.CloudflareCustomDomain ?? string.Empty;
        TxtTunnelToken.Text = settings.CloudflareTunnelToken ?? string.Empty;
        if (settings.EnableCloudflareTunnel && !ChkEnableTunnel.IsChecked.GetValueOrDefault())
        {
            ChkEnableTunnel.IsChecked = true;
        }

        // פעולת כפתור סגירה (X)
        if (CmbSettingsCloseAction != null)
        {
            foreach (ComboBoxItem item in CmbSettingsCloseAction.Items)
            {
                string tag = item.Tag?.ToString() ?? "";
                if (string.Equals(tag, settings.CloseAction, StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(settings.CloseAction, "Exit", StringComparison.OrdinalIgnoreCase) && string.Equals(tag, "ExitApplication", StringComparison.OrdinalIgnoreCase)))
                {
                    CmbSettingsCloseAction.SelectedItem = item;
                    break;
                }
            }
        }

        // טעינת ערכת נושא (Theme)
        string themeMode = settings.ThemeMode ?? ThemeService.ThemeDark;
        ThemeService.Instance.SetTheme(themeMode);
        if (CmbSettingsTheme != null)
        {
            foreach (ComboBoxItem item in CmbSettingsTheme.Items)
            {
                if (string.Equals(item.Tag?.ToString(), themeMode, StringComparison.OrdinalIgnoreCase))
                {
                    CmbSettingsTheme.SelectedItem = item;
                    break;
                }
            }
        }

        // טעינת הגבלת רוחב פס (Bandwidth Throttling)
        if (CmbSettingsBandwidthLimit != null)
        {
            foreach (ComboBoxItem item in CmbSettingsBandwidthLimit.Items)
            {
                if (int.TryParse(item.Tag?.ToString(), out int kbps) && kbps == settings.MaxDownloadSpeedKbps)
                {
                    CmbSettingsBandwidthLimit.SelectedItem = item;
                    break;
                }
            }
        }
        BandwidthThrottler.MaxKbps = settings.MaxDownloadSpeedKbps;

        // טעינת שפה ולוקליזציה
        LocalizationService.Instance.SetLanguage(settings.Language ?? LocalizationService.LanguageHebrew);
        ApplyLocalization();
        ApplyThemeToUi(ThemeService.Instance.CurrentTheme);
    }

    private void BtnThemeToggleTop_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Instance.ToggleTheme();
        _server.Settings.ThemeMode = ThemeService.Instance.CurrentTheme;
        _server.SettingsManager.Save();
        ApplyThemeToUi(ThemeService.Instance.CurrentTheme);
    }

    private void CmbSettingsTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbSettingsTheme?.SelectedItem is ComboBoxItem item && item.Tag is string theme)
        {
            ThemeService.Instance.SetTheme(theme);
            _server.Settings.ThemeMode = theme;
            _server.SettingsManager.Save();
            ApplyThemeToUi(theme);
        }
    }

    private void ApplyThemeToUi(string currentTheme)
    {
        bool isDark = string.Equals(currentTheme, ThemeService.ThemeDark, StringComparison.OrdinalIgnoreCase);
        bool isHe = LocalizationService.Instance.CurrentLanguage == LocalizationService.LanguageHebrew;

        if (BtnThemeToggleTop != null)
        {
            BtnThemeToggleTop.Content = isDark 
                ? (isHe ? "☀️ בהיר" : "☀️ Light")
                : (isHe ? "🌙 כהה" : "🌙 Dark");
            BtnThemeToggleTop.ToolTip = isDark
                ? (isHe ? "עבור למצב בהיר (Nordic Frost)" : "Switch to Light Theme (Nordic Frost)")
                : (isHe ? "עבור למצב כהה (Nordic Cyber-Teal)" : "Switch to Dark Theme (Nordic Cyber-Teal)");
        }

        if (CmbSettingsTheme != null)
        {
            foreach (ComboBoxItem item in CmbSettingsTheme.Items)
            {
                if (string.Equals(item.Tag?.ToString(), currentTheme, StringComparison.OrdinalIgnoreCase))
                {
                    if (CmbSettingsTheme.SelectedItem != item)
                    {
                        CmbSettingsTheme.SelectedItem = item;
                    }
                    break;
                }
            }
        }

        UpdateTitleBarTheme(isDark);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void UpdateTitleBarTheme(bool isDark)
    {
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                int darkMode = isDark ? 1 : 0;
                // DWMWA_USE_IMMERSIVE_DARK_MODE (20 on Windows 11 / Win10 20H1+, 19 on older builds)
                if (DwmSetWindowAttribute(helper.Handle, 20, ref darkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(helper.Handle, 19, ref darkMode, sizeof(int));
                }
            }
        }
        catch
        {
            // הגנה על מערכות ישנות יותר
        }
    }

    private void BtnLanguageToggleTop_Click(object sender, RoutedEventArgs e)
    {
        string nextLang = LocalizationService.Instance.CurrentLanguage == LocalizationService.LanguageHebrew
            ? LocalizationService.LanguageEnglish
            : LocalizationService.LanguageHebrew;
        LocalizationService.Instance.SetLanguage(nextLang);
        ApplyLocalization();
    }

    private void CmbSettingsLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbSettingsLanguage?.SelectedItem is ComboBoxItem item && item.Tag is string lang)
        {
            LocalizationService.Instance.SetLanguage(lang);
            ApplyLocalization();
        }
    }

    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;
        bool isHe = loc.CurrentLanguage == LocalizationService.LanguageHebrew;
        FlowDirection = loc.CurrentFlowDirection;
        Title = loc["AppTitle"];

        if (BtnLanguageToggleTop != null)
            BtnLanguageToggleTop.Content = isHe ? "English" : "עברית";

        if (TxtAppTitle != null) TxtAppTitle.Text = loc["HeaderTitle"];
        if (TxtAppSubtitle != null) TxtAppSubtitle.Text = loc["HeaderSubtitle"];

        if (TxtServerStatus != null && BtnToggleServerTop != null)
        {
            if (_server.IsRunning)
            {
                TxtServerStatus.Text = loc["ServerRunning"];
                BtnToggleServerTop.Content = loc["StopServer"];
            }
            else
            {
                TxtServerStatus.Text = loc["ServerStopped"];
                BtnToggleServerTop.Content = loc["StartServer"];
            }
        }

        // Tabs
        if (TabServer != null) TabServer.Header = loc["TabWebServer"];
        if (TabSecureShare != null) TabSecureShare.Header = loc["TabSecureShare"];
        if (TabSettings != null) TabSettings.Header = loc["TabSettings"];
        if (TabMonitor != null) TabMonitor.Header = loc["TabMonitor"];

        // Tab 1: Web Server
        if (LblLocalNetworkTitle != null) LblLocalNetworkTitle.Text = loc["LocalNetworkTitle"];
        if (LblLocalNetworkSubtitle != null) LblLocalNetworkSubtitle.Text = loc["LocalNetworkSubtitle"];
        if (BtnOpenLocalBrowser != null) BtnOpenLocalBrowser.Content = loc["OpenInBrowser"];
        if (BtnCopyLocalUrl != null) BtnCopyLocalUrl.Content = loc["CopyAddress"];

        if (LblTunnelTitle != null) LblTunnelTitle.Text = loc["TunnelTitle"];
        if (LblTunnelSubtitle != null) LblTunnelSubtitle.Text = loc["TunnelSubtitle"];
        if (ChkEnableTunnel != null) ChkEnableTunnel.Content = loc["EnableTunnel"];
        if (RbTunnelRandom != null)
        {
            var tb = new TextBlock();
            if (isHe)
            {
                tb.Inlines.Add(new Run("מנהור מהיר "));
                tb.Inlines.Add(new Run("(Cloudflare Quick Tunnel)") { FlowDirection = System.Windows.FlowDirection.LeftToRight });
            }
            else
            {
                tb.Inlines.Add(new Run("Quick Tunnel (Cloudflare)"));
            }
            RbTunnelRandom.Content = tb;
        }
        if (RbTunnelCustom != null)
        {
            var tb = new TextBlock();
            if (isHe)
            {
                tb.Inlines.Add(new Run("דומיין אישי "));
                tb.Inlines.Add(new Run("(Cloudflare Token)") { FlowDirection = System.Windows.FlowDirection.LeftToRight });
            }
            else
            {
                tb.Inlines.Add(new Run("Custom Domain (with Cloudflare Token)"));
            }
            RbTunnelCustom.Content = tb;
        }
        if (LblCustomDomainUrl != null) LblCustomDomainUrl.Text = loc["LblCustomDomainUrl"];
        if (LblTunnelToken != null)
        {
            if (isHe)
            {
                LblTunnelToken.Text = "";
                LblTunnelToken.Inlines.Clear();
                LblTunnelToken.Inlines.Add(new Run("טוקן "));
                LblTunnelToken.Inlines.Add(new Run("(Tunnel Token):") { FlowDirection = System.Windows.FlowDirection.LeftToRight });
            }
            else
            {
                LblTunnelToken.Inlines.Clear();
                LblTunnelToken.Text = loc["LblTunnelToken"];
            }
        }
        if (TxtCustomDomainUrl != null) TxtCustomDomainUrl.ToolTip = loc["TipCustomDomainUrl"];
        if (TxtTunnelToken != null) TxtTunnelToken.ToolTip = loc["TipTunnelToken"];
        if (TxtTunnelUrl != null && !_server.TunnelService.IsRunning)
        {
            SetTunnelUrlText(loc["TunnelDisabled"]);
        }
        if (BtnOpenTunnelBrowser != null) BtnOpenTunnelBrowser.Content = loc["OpenPublicUrl"];
        if (BtnCopyTunnelUrl != null) BtnCopyTunnelUrl.Content = loc["CopyLink"];

        if (LblEventLogTitle != null) LblEventLogTitle.Text = loc["ConsoleLogTitle"];
        if (LblQrTitle != null) LblQrTitle.Text = loc["QrScanTitle"];
        if (LblQrSubtitle != null) LblQrSubtitle.Text = loc["QrScanSubtitle"];
        if (LblQrDesc != null) LblQrDesc.Text = isHe ? "כוון את מצלמת הסמארטפון כדי לגלוש לקבצים, לנגן וידאו ולהעלות תמונות מיידית." : "Point smartphone camera to browse files, stream media and upload photos instantly.";
        if (LblRealtimeTrafficTitle != null) LblRealtimeTrafficTitle.Text = isHe ? "תעבורת רשת ונתוני מערכת בזמן אמת" : "Realtime Traffic & System Telemetry";
        if (LblRealtimeTrafficSubtitle != null) LblRealtimeTrafficSubtitle.Text = isHe ? "ניטור חי של מהירות שידור, חיבורים פעילים ונפח תעבורה" : "Live monitoring of transfer speed, active connections and volume";
        if (BtnCopyQrImage != null) BtnCopyQrImage.Content = isHe ? "העתק תמונת QR" : "Copy QR Image";
        if (BtnSaveQrImage != null) BtnSaveQrImage.Content = isHe ? "שמור קוד QR" : "Save QR Code";
        if (TxtHomeAccessMode != null) TxtHomeAccessMode.Text = _server.Settings.AccessMode == "SingleFolder" ? (isHe ? "תיקייה ייעודית (מוגבל)" : "Dedicated Folder (Limited)") : (isHe ? "גישה מלאה (Full)" : "Full Computer Access");
        if (TxtHomePinStatus != null) TxtHomePinStatus.Text = !string.IsNullOrEmpty(_server.Settings.SecurityPin) ? (isHe ? $"מוגן PIN ({_server.Settings.SecurityPin})" : $"Protected ({_server.Settings.SecurityPin})") : (isHe ? "ללא PIN" : "No PIN");

        // Tab 2: Secure Share
        if (LblSecureShareTitle != null) LblSecureShareTitle.Text = isHe ? "יצירת קישור שיתוף מאובטח לקובץ או תיקייה" : "Create Secure Share Link for File or Folder";
        if (LblSecureShareSubtitle != null) LblSecureShareSubtitle.Text = isHe ? "שתף קבצים ברשת המקומית, באינטרנט, בענן מוצפן, או ישירות דרך Google Drive ו-OneDrive" : "Share files over LAN, Internet, encrypted Cloud, or directly via Google Drive & OneDrive";
        if (TxtSecureTarget != null && (TxtSecureTarget.Text == "בחר קובץ או תיקייה לשיתוף..." || TxtSecureTarget.Text == "Select file or folder to share..."))
        {
            TxtSecureTarget.Text = loc["ChoosePlaceholder"];
        }
        if (BtnSelectFile != null) BtnSelectFile.Content = loc["BtnChooseFile"];
        if (BtnSelectFolder != null) BtnSelectFolder.Content = loc["BtnChooseFolder"];
        if (LblCloudDirectTitle != null) LblCloudDirectTitle.Text = isHe ? "שיתוף ישיר לשירותי ענן (Google Drive & OneDrive)" : "Direct Share to Cloud Services (Google Drive & OneDrive)";
        if (BtnShareGoogleDriveDirect != null)
        {
            BtnShareGoogleDriveDirect.Content = isHe ? "שיתוף ב-Google Drive" : "Share to Google Drive";
            BtnShareGoogleDriveDirect.ToolTip = loc["TipShareGoogleDrive"];
        }
        if (BtnShareOneDriveDirect != null)
        {
            BtnShareOneDriveDirect.Content = isHe ? "שיתוף ב-OneDrive" : "Share to OneDrive";
            BtnShareOneDriveDirect.ToolTip = loc["TipShareOneDrive"];
        }
        if (LblChannel != null) LblChannel.Text = loc["ChannelTitle"];
        if (CmbChanLan != null) CmbChanLan.Content = loc["ChanLan"];
        if (CmbChanTunnel != null) CmbChanTunnel.Content = loc["ChanTunnel"];
        if (CmbChanDirectCloud != null) CmbChanDirectCloud.Content = loc["ChanDirectCloud"];
        if (CmbChanCloud != null) CmbChanCloud.Content = loc["ChanCloud"];
        if (CmbChanGoogleDrive != null) CmbChanGoogleDrive.Content = loc["ChanGoogleDrive"];
        if (CmbChanOneDrive != null) CmbChanOneDrive.Content = loc["ChanOneDrive"];

        if (LblPinCode != null) LblPinCode.Text = loc["PinCodeLabel"];
        if (BtnGenPin != null) BtnGenPin.ToolTip = loc["TipGenPin"];

        if (LblExpiration != null) LblExpiration.Text = loc["ExpirationLabel"];
        if (CmbExpUnlimited != null) CmbExpUnlimited.Content = loc["ExpUnlimitedItem"];
        if (CmbExp15Min != null) CmbExp15Min.Content = loc["Exp15MinItem"];
        if (CmbExp1Hour != null) CmbExp1Hour.Content = loc["Exp1HourItem"];
        if (CmbExp6Hours != null) CmbExp6Hours.Content = loc["Exp6HoursItem"];
        if (CmbExp24Hours != null) CmbExp24Hours.Content = loc["Exp24HoursItem"];

        if (LblMaxDownloads != null) LblMaxDownloads.Text = loc["MaxDownloadsLabel"];
        if (CmbMaxDl1 != null) CmbMaxDl1.Content = loc["MaxDl1Item"];
        if (CmbMaxDl3 != null) CmbMaxDl3.Content = loc["MaxDl3Item"];
        if (CmbMaxDl10 != null) CmbMaxDl10.Content = loc["MaxDl10Item"];
        if (CmbMaxDlUnlimited != null) CmbMaxDlUnlimited.Content = loc["MaxDlUnlimitedItem"];

        if (BtnCreateSecureShare != null) BtnCreateSecureShare.Content = loc["BtnCreateShare"];
        if (BtnCopyShareUrl != null) BtnCopyShareUrl.Content = loc["BtnCopyShareUrl"];
        if (BtnResultOpenBrowser != null) BtnResultOpenBrowser.Content = loc["BtnOpenInBrowser"];
        if (BtnResultShowExplorer != null) BtnResultShowExplorer.Content = loc["BtnShowInFolder"];
        if (LblPinVerification != null) LblPinVerification.Text = isHe ? "קוד אימות PIN / אבטחה:" : "Security / PIN verification code:";
        if (BtnCopyShareFullMessage != null) BtnCopyShareFullMessage.Content = loc["BtnCopyFullMessage"];
        if (LblActiveTransfersTitle != null) LblActiveTransfersTitle.Text = loc["ActiveSharesTitle"];

        // Tab 3: Settings
        if (LblSettingsHeader != null) LblSettingsHeader.Text = loc["SettingsTitle"];
        if (LblSettingsPort != null) LblSettingsPort.Text = loc["ServerPortLabel"];
        if (LblSettingsAccessMode != null) LblSettingsAccessMode.Text = loc["AccessModeLabel"];
        if (CmbItemAccessFull != null) CmbItemAccessFull.Content = loc["AccessModeFull"];
        if (CmbItemAccessSingle != null) CmbItemAccessSingle.Content = loc["AccessModeSingle"];
        if (LblSettingsLanguage != null) LblSettingsLanguage.Text = loc["LanguageSettingLabel"];
        if (LblSettingsTheme != null) LblSettingsTheme.Text = loc["ThemeSettingLabel"];
        if (CmbItemThemeDark != null) CmbItemThemeDark.Content = loc["ThemeDark"];
        if (CmbItemThemeLight != null) CmbItemThemeLight.Content = loc["ThemeLight"];
        if (CmbItemThemeAuto != null) CmbItemThemeAuto.Content = loc["ThemeAuto"];
        if (LblSettingsBandwidth != null) LblSettingsBandwidth.Text = loc["BandwidthLimitLabel"];
        if (CmbBwUnlimited != null) CmbBwUnlimited.Content = loc["BandwidthUnlimited"];
        if (CmbBw5MB != null) CmbBw5MB.Content = loc["Bandwidth5MB"];
        if (CmbBw10MB != null) CmbBw10MB.Content = loc["Bandwidth10MB"];
        if (CmbBw25MB != null) CmbBw25MB.Content = loc["Bandwidth25MB"];
        if (CmbBw50MB != null) CmbBw50MB.Content = loc["Bandwidth50MB"];
        if (LblSettingsSharedFolder != null) LblSettingsSharedFolder.Text = loc["SharedFolderLabel"];
        if (BtnSettingsBrowseFolder != null) BtnSettingsBrowseFolder.Content = loc["BtnBrowseFolder"];
        if (ChkSettingsRecycleBin != null) ChkSettingsRecycleBin.Content = loc["ChkRecycleBin"];
        if (ChkSettingsReadOnly != null) ChkSettingsReadOnly.Content = loc["ChkReadOnly"];
        if (ChkSettingsHidden != null) ChkSettingsHidden.Content = loc["ChkHiddenFiles"];
        if (ChkSettingsHotkey != null) ChkSettingsHotkey.Content = loc["ChkHotkey"];
        if (ChkSettingsContextMenu != null) ChkSettingsContextMenu.Content = loc["ChkContextMenu"];
        if (ChkSettingsDropZone != null) ChkSettingsDropZone.Content = loc["ChkDropZone"];
        if (ChkSettingsAutoStartTunnel != null) ChkSettingsAutoStartTunnel.Content = isHe ? "הפעל מנהור Cloudflare אוטומטית בהפעלת השרת" : "Auto-start Cloudflare Tunnel on server start";
        if (LblSettingsCloseAction != null) LblSettingsCloseAction.Text = loc["CloseActionLabel"];
        if (CmbItemCloseTray != null) CmbItemCloseTray.Content = loc["CloseActionTray"];
        if (CmbItemCloseExit != null) CmbItemCloseExit.Content = loc["CloseActionExit"];
        if (BtnSaveSettings != null) BtnSaveSettings.Content = loc["BtnSaveSettings"];
        if (BtnClearLog != null) BtnClearLog.Content = loc["ClearLog"];

        ApplyThemeToUi(ThemeService.Instance.CurrentTheme);

        if (CmbSettingsLanguage != null)
        {
            foreach (ComboBoxItem item in CmbSettingsLanguage.Items)
            {
                if (string.Equals(item.Tag?.ToString(), loc.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    CmbSettingsLanguage.SelectedItem = item;
                    break;
                }
            }
        }

        // Tab 4: Monitor
        if (LblBandwidthTitle != null) LblBandwidthTitle.Text = loc["BandwidthTitle"];
        if (LblBandwidthDetails != null) LblBandwidthDetails.Text = loc["BandwidthPortDetails"];
        if (LblMonitorSharedTitle != null) LblMonitorSharedTitle.Text = loc["MonitorSharedTitle"];
        if (LblMonitorSharedSafeNote != null) LblMonitorSharedSafeNote.Text = loc["MonitorSharedSafeNote"];
        if (BtnRevokeAllMonitorShares != null) BtnRevokeAllMonitorShares.Content = loc["BtnRevokeAllShares"] + " ⚠️";
        if (BtnRefreshMonitorShares != null) BtnRefreshMonitorShares.Content = loc["BtnRefreshShares"];
        if (TxtSearchMonitorShares != null) TxtSearchMonitorShares.ToolTip = loc["SearchSharesPlaceholder"];
        if (LblLiveConnectionsTitle != null) LblLiveConnectionsTitle.Text = loc["LiveConnectionsTitle"];
        if (ColSource != null) ColSource.Header = loc["ColSource"];
        if (ColClientIp != null) ColClientIp.Header = loc["ColClientIp"];
        if (ColTargetFile != null) ColTargetFile.Header = loc["ColTargetFile"];
        if (ColProgress != null) ColProgress.Header = loc["ColProgress"];
        if (ColActions != null) ColActions.Header = loc["ColActions"];
        if (LblBlockedIpsTitle != null) LblBlockedIpsTitle.Text = loc["BlockedIpsTitle"];
        if (ColBlockedIp != null) ColBlockedIp.Header = loc["ColClientIp"];
        if (ColBlockedReason != null) ColBlockedReason.Header = loc["ColBlockReason"];
        if (ColBlockedAction != null) ColBlockedAction.Header = loc["ColActions"];

        // Overlays & Quick Clipboard
        if (TxtDropOverlayTitle != null) TxtDropOverlayTitle.Text = loc["DragDropOverlayText"];
        if (BtnSendClipboard != null) BtnSendClipboard.Content = isHe ? "שלח" : "Send";
        if (BtnGetClipboard != null) BtnGetClipboard.Content = isHe ? "קבל" : "Get";

        // Bottom Bar
        if (TxtFooterInfo != null) TxtFooterInfo.Text = $"{loc["HeaderTitle"]} | Port {_server.Port} | Mode: {_server.Settings.AccessMode}";
        if (BtnBottomCheckUpdates != null) BtnBottomCheckUpdates.Content = loc["BtnCheckUpdates"];
        if (BtnBottomDropZone != null) BtnBottomDropZone.Content = loc["BtnBottomDropZone"];
        if (BtnBottomContextMenu != null) BtnBottomContextMenu.Content = loc["BtnBottomContextMenu"];
        if (BtnBottomMinimize != null) BtnBottomMinimize.Content = loc["BtnBottomMinimize"];

        _server.Settings.Language = loc.CurrentLanguage;
        UpdateTrayMenu();
        UpdateCloudServicesStatus();
        RefreshTransfersList();
        RefreshMonitorSharedLinks();
    }

    private void ApplyLanguage(string lang)
    {
        ApplyLocalization();
    }

    private void BtnSettingsBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = loc["ChooseDedicatedFolderTitle"]
        };

        if (dlg.ShowDialog() == true)
        {
            TxtSettingsSharedFolder.Text = dlg.FolderName;
        }
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;

        if (!int.TryParse(TxtSettingsPort.Text.Trim(), out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show(loc["MsgInvalidPort"], loc["MsgErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = _server.Settings;
        bool needServerRestart = (settings.ServerPort != port);

        settings.ServerPort = port;
        settings.AccessMode = (CmbSettingsAccessMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "FullComputer";
        settings.Language = (CmbSettingsLanguage.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "he";
        loc.SetLanguage(settings.Language);

        settings.ThemeMode = (CmbSettingsTheme?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ThemeService.ThemeDark;
        ThemeService.Instance.SetTheme(settings.ThemeMode);

        settings.SharedFolderPath = TxtSettingsSharedFolder.Text.Trim();
        settings.SendToRecycleBin = ChkSettingsRecycleBin.IsChecked ?? true;
        settings.ServerReadOnly = ChkSettingsReadOnly.IsChecked ?? false;
        settings.ShowHiddenFiles = ChkSettingsHidden.IsChecked ?? false;
        settings.EnableGlobalHotkey = ChkSettingsHotkey.IsChecked ?? true;
        settings.EnableFloatingDropZone = ChkSettingsDropZone.IsChecked ?? false;
        settings.EnableCloudflareTunnel = ChkSettingsAutoStartTunnel.IsChecked ?? false;
        settings.TunnelMode = (RbTunnelCustom.IsChecked == true) ? "CustomDomain" : "Random";
        settings.CloudflareCustomDomain = TxtCustomDomainUrl.Text.Trim();
        settings.CloudflareTunnelToken = TxtTunnelToken.Text.Trim();
        settings.CloseAction = (CmbSettingsCloseAction.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "MinimizeToTray";

        if (CmbSettingsBandwidthLimit?.SelectedItem is ComboBoxItem bwItem &&
            int.TryParse(bwItem.Tag?.ToString(), out int bwKbps))
        {
            settings.MaxDownloadSpeedKbps = bwKbps;
            BandwidthThrottler.MaxKbps = bwKbps;
        }

        bool shouldRegisterContext = ChkSettingsContextMenu.IsChecked ?? false;
        if (shouldRegisterContext != ShellContextMenuService.IsContextMenuRegistered())
        {
            if (shouldRegisterContext) ShellContextMenuService.RegisterContextMenu();
            else ShellContextMenuService.UnregisterContextMenu();
        }

        _server.SettingsManager.Save();
        AppendLog("[SETTINGS] Settings updated and saved to disk.");

        if (needServerRestart && _server.IsRunning)
        {
            var res = MessageBox.Show(
                loc["MsgPortChangedRestart"],
                loc["MsgApplySettingsTitle"],
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                StopServer();
                StartServer();
            }
        }
        else
        {
            MessageBox.Show(loc["MsgSettingsSaved"], loc["MsgSuccessTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    #endregion

    #region ניטור תעבורה, ווידג'ט צף וסייר קבצים

    private void UpdateSpeedGraph(double currentMbps)
    {
        TxtCurrentSpeedDisplay.Text = $"{currentMbps:F2} Mbps";
        if (TxtHomeUploadSpeed != null) TxtHomeUploadSpeed.Text = $"{currentMbps:F2} Mbps";
        if (TxtHomeTotalTransferred != null) TxtHomeTotalTransferred.Text = LiveNetworkMonitorService.Instance.FormattedTotalTransferred;

        _speedHistory.Add(currentMbps);
        if (_speedHistory.Count > 45) _speedHistory.RemoveAt(0);

        double w = BandwidthCanvas.ActualWidth;
        double h = BandwidthCanvas.ActualHeight;
        if (w <= 10 || h <= 10) return;

        double maxSpeed = Math.Max(5.0, _speedHistory.Max() * 1.2);
        var points = new PointCollection();

        double stepX = w / Math.Max(1, _speedHistory.Count - 1);
        for (int i = 0; i < _speedHistory.Count; i++)
        {
            double x = i * stepX;
            double y = Math.Max(5, h - ((_speedHistory[i] / maxSpeed) * (h - 15)) - 10);
            points.Add(new System.Windows.Point(x, y));
        }

        SpeedLine.Points = points;
    }

    private void RefreshLiveConnections()
    {
        var conns = LiveNetworkMonitorService.Instance.GetActiveConnections();
        LstLiveConnections.ItemsSource = conns;
        TxtActiveConnCount.Text = string.Format(LocalizationService.Instance["ActiveConnectionsCount"], conns.Count);
        if (TxtHomeConnectedClients != null)
        {
            bool isHe = LocalizationService.Instance.CurrentLanguage == LocalizationService.LanguageHebrew;
            TxtHomeConnectedClients.Text = isHe ? $"{conns.Count} פעילים" : $"{conns.Count} active";
        }
    }

    private void BtnTerminateConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string connId)
        {
            LiveNetworkMonitorService.Instance.TerminateConnection(connId);
            RefreshLiveConnections();
            AppendLog($"[MONITOR] Terminated connection: {connId}");
        }
    }

    private void BtnBlockIp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string ip && !string.IsNullOrWhiteSpace(ip))
        {
            _server.BlacklistIp(ip);
            var conns = LiveNetworkMonitorService.Instance.GetActiveConnections();
            foreach (var conn in conns.Where(c => string.Equals(c.ClientIp, ip, StringComparison.OrdinalIgnoreCase)))
            {
                LiveNetworkMonitorService.Instance.TerminateConnection(conn.ConnectionId);
            }
            RefreshLiveConnections();
            RefreshBlockedIpsList();
            AppendLog($"[SECURITY] כתובת IP נחסמה: {ip}");
        }
    }

    private void BtnUnblockIp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string ip && !string.IsNullOrWhiteSpace(ip))
        {
            _server.UnblacklistIp(ip);
            RefreshBlockedIpsList();
            AppendLog($"[SECURITY] חסימת IP שוחררה: {ip}");
        }
    }

    private void RefreshBlockedIpsList()
    {
        if (LstBlockedIps == null) return;
        var ips = _server.GetBlacklistedIps().Select(ip => new KeyValuePair<string, string>(ip, "חסימה מותאמת")).ToList();
        LstBlockedIps.ItemsSource = ips;
        if (TxtBlockedIpsCount != null)
        {
            TxtBlockedIpsCount.Text = $"({ips.Count} חסומים)";
        }
    }

    #region ניטור קישורים וקבצים משותפים פעילים (Active Shares & Link Destruction)

    private void RefreshMonitorSharedLinks()
    {
        if (LstMonitorSharedLinks == null) return;

        var allTransfers = SecureTransferService.GetAllActiveTransfers();
        string filter = TxtSearchMonitorShares?.Text?.Trim() ?? "";

        IEnumerable<SecureTransferItem> filtered = allTransfers;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            filtered = allTransfers.Where(t =>
                (t.FileName != null && t.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                (t.Token != null && t.Token.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                (t.Channel != null && t.Channel.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                (t.ShareUrl != null && t.ShareUrl.Contains(filter, StringComparison.OrdinalIgnoreCase))
            );
        }

        var items = filtered.ToList();
        LstMonitorSharedLinks.ItemsSource = null;
        LstMonitorSharedLinks.ItemsSource = items;

        if (TxtMonitorSharedCount != null)
        {
            int activeCount = allTransfers.Count(t => !t.IsExpired && !t.IsCancelled);
            TxtMonitorSharedCount.Text = string.Format(LocalizationService.Instance["MonitorSharedCount"], activeCount);
        }
    }

    private void TxtSearchMonitorShares_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshMonitorSharedLinks();
    }

    private void BtnRefreshMonitorShares_Click(object sender, RoutedEventArgs e)
    {
        RefreshMonitorSharedLinks();
        RefreshTransfersList();
    }

    private void BtnCopyMonitorShare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url && !string.IsNullOrWhiteSpace(url) && url != "—")
        {
            try
            {
                Clipboard.SetText(url);
                AppendLog($"[SHARE COPIED] {url}");
                MessageBox.Show(LocalizationService.Instance["ShareCopied"], LocalizationService.Instance["MsgSuccessTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog($"[CLIPBOARD ERROR] {ex.Message}");
            }
        }
    }

    private void BtnRevokeShareLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string token && !string.IsNullOrWhiteSpace(token))
        {
            var item = SecureTransferService.GetTransfer(token);
            string name = item?.FileName ?? token;

            string confirmMsg = string.Format(LocalizationService.Instance["ConfirmRevokeShare"], name);
            var res = MessageBox.Show(confirmMsg, LocalizationService.Instance["ConfirmRevokeShareTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                // משמיד אך ורק את הקישור והגישה - הקובץ המקורי במחשב נשמר בבטחה מלאה!
                bool success = SecureTransferService.RevokeTransfer(token, removeFromList: true);
                RefreshMonitorSharedLinks();
                RefreshTransfersList();

                if (success)
                {
                    AppendLog($"[SECURITY] קישור השיתוף עבור '{name}' הושמד בהצלחה. הקובץ המקורי במחשב נשמר ללא שינוי.");
                    MessageBox.Show(LocalizationService.Instance["ShareRevokedSuccess"], LocalizationService.Instance["MsgSuccessTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
    }

    private void BtnRevokeAllMonitorShares_Click(object sender, RoutedEventArgs e)
    {
        string confirmMsg = LocalizationService.Instance["ConfirmRevokeAllShares"];
        var res = MessageBox.Show(confirmMsg, LocalizationService.Instance["ConfirmRevokeAllSharesTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res == MessageBoxResult.Yes)
        {
            // משמיד את כל הקישורים הפעילים - הקבצים המקוריים במחשב נשמרים בבטחה מלאה!
            int count = SecureTransferService.RevokeAllTransfers(removeFromList: true);
            RefreshMonitorSharedLinks();
            RefreshTransfersList();

            AppendLog($"[SECURITY] כל קישורי השיתוף ({count}) הושמדו בהצלחה. הקבצים המקוריים במחשב לא נפגעו.");
            MessageBox.Show(LocalizationService.Instance["AllSharesRevokedSuccess"], LocalizationService.Instance["MsgSuccessTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    #endregion

    private void RootWindow_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            if (GridDropOverlay != null) GridDropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void RootWindow_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            if (GridDropOverlay != null && GridDropOverlay.Visibility != Visibility.Visible)
            {
                GridDropOverlay.Visibility = Visibility.Visible;
            }
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void RootWindow_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (GridDropOverlay != null) GridDropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void RootWindow_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (GridDropOverlay != null) GridDropOverlay.Visibility = Visibility.Collapsed;
        try
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                var files = (string[]?)e.Data.GetData(System.Windows.DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    string target = files[0];
                    TxtSecureTarget.Text = target;
                    MainTabControl.SelectedIndex = 1; // שיתוף מאובטח
                    BtnCreateSecureShare_Click(this, new RoutedEventArgs());

                    string name = Path.GetFileName(target);
                    ShowNotification("EasyShare PRO", $"נוצר קישור שיתוף עבור: {name}");
                    AppendLog($"[DRAG-DROP] שותף קובץ בהצלחה: {target}");
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[DRAG-DROP ERROR] {ex.Message}");
        }
    }

    private static void CopyDirectoryRecursively(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectoryRecursively(subDir, dest);
        }
    }

    private void BtnSendClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _server.AddClipboardItem(text.Trim());
                    _server.AddMessage("מחשב", text.Trim());
                    if (TxtClipboardPreview != null)
                    {
                        TxtClipboardPreview.Text = text.Length > 25 ? text[..25] + "..." : text;
                    }
                    ShowNotification("EasyShare PRO", "לוח המחשב שותף בהצלחה למכשירים!");
                    AppendLog($"[CLIPBOARD] נשלח טקסט מהלוח: {(text.Length > 25 ? text[..25] + "..." : text)}");
                }
            }
            else
            {
                MessageBox.Show("אין טקסט בלוח המחשב להעתקה.", "סנכרון לוח", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[CLIPBOARD ERROR] {ex.Message}");
        }
    }

    private void BtnGetClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var item = _server.GetLatestClipboardItem();
            if (item != null && !string.IsNullOrWhiteSpace(item.Content))
            {
                Clipboard.SetText(item.Content);
                if (TxtClipboardPreview != null)
                {
                    TxtClipboardPreview.Text = item.Content.Length > 25 ? item.Content[..25] + "..." : item.Content;
                }
                ShowNotification("EasyShare PRO", "הטקסט מהמכשיר הועתק ללוח המחשב!");
                AppendLog($"[CLIPBOARD] הועתק ללוח המחשב: {(item.Content.Length > 25 ? item.Content[..25] + "..." : item.Content)}");
            }
            else
            {
                var msgs = _server.GetRecentMessages();
                var last = msgs.LastOrDefault();
                if (last != null && !string.IsNullOrWhiteSpace(last.Text))
                {
                    Clipboard.SetText(last.Text);
                    if (TxtClipboardPreview != null)
                    {
                        TxtClipboardPreview.Text = last.Text.Length > 25 ? last.Text[..25] + "..." : last.Text;
                    }
                    ShowNotification("EasyShare PRO", "הטקסט מהמכשיר הועתק ללוח המחשב!");
                    AppendLog($"[CLIPBOARD] הועתק ללוח המחשב: {(last.Text.Length > 25 ? last.Text[..25] + "..." : last.Text)}");
                }
                else
                {
                    MessageBox.Show("אין פריטי לוח שהתקבלו מהמכשירים עדיין.", "סנכרון לוח", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[CLIPBOARD ERROR] {ex.Message}");
        }
    }

    private void BtnToggleDropZone_Click(object sender, RoutedEventArgs e)
    {
        ToggleDropZone();
    }

    private void ToggleDropZone()
    {
        if (_dropZoneWindow == null)
        {
            _dropZoneWindow = new Views.FloatingDropZoneWindow(_server);
            _dropZoneWindow.Closed += (s, ev) => _dropZoneWindow = null;
        }

        if (_dropZoneWindow.IsVisible)
        {
            _dropZoneWindow.Hide();
            ChkSettingsDropZone.IsChecked = false;
        }
        else
        {
            _dropZoneWindow.Show();
            ChkSettingsDropZone.IsChecked = true;
        }
    }

    private void BtnToggleContextMenu_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        if (ShellContextMenuService.IsContextMenuRegistered())
        {
            ShellContextMenuService.UnregisterContextMenu();
            ChkSettingsContextMenu.IsChecked = false;
            MessageBox.Show(loc["ContextMenuRemoved"], loc["ExplorerMenuTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            ShellContextMenuService.RegisterContextMenu();
            ChkSettingsContextMenu.IsChecked = true;
            MessageBox.Show(loc["ContextMenuAdded"], loc["ExplorerMenuTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private bool HandleExternalShareRequest(string channel, string path)
    {
        try
        {
            if (channel == "ACTIVATE")
            {
                RestoreFromTray();
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                bool validHwnd = helper.Handle != IntPtr.Zero && IsWindow(helper.Handle);
                bool isWinVis = validHwnd && IsWindowVisible(helper.Handle);
                bool hasWindow = validHwnd && isWinVis && IsVisible;
                try
                {
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup_trace.log"),
                        $"[{DateTime.Now:HH:mm:ss.fff}] [IPC] ACTIVATE evaluated: hasWindow={hasWindow}, HWND={helper.Handle}, validHwnd={validHwnd}, isWinVis={isWinVis}, IsVisible={IsVisible}, WindowState={WindowState}\r\n");
                }
                catch { }

                // אם אין חלון פיזי תקין שמוצג למשתמש, אנחנו לא מונעים מופע חדש אלא יוצאים
                if (!hasWindow)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(300);
                        Environment.Exit(0);
                    });
                }

                return hasWindow;
            }

            AppendLog($"[EXTERNAL SHARE] Received share request for {Path.GetFileName(path)} (Channel: {channel})");
            var loc = LocalizationService.Instance;
            if (channel == "DirectCloud")
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string? url = await SecureTransferService.UploadToDirectPublicCloudAsync(path);
                        if (!string.IsNullOrEmpty(url))
                        {
                            Dispatcher.Invoke(() =>
                            {
                                Clipboard.SetText(url);
                                ShowNotification(loc["HeaderTitle"], string.Format(loc["BalloonCloudUploaded"], Path.GetFileName(path), url));
                                RefreshTransfersList();
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => AppendLog($"[DIRECT CLOUD ERROR] {ex.Message}"));
                    }
                });
                return true;
            }
            else if (channel == "LocalCloud")
            {
                _ = ExecuteCloudShareAsync("GoogleDrive", path);
                return true;
            }

            TxtSecureTarget.Text = path;
            MainTabControl.SelectedIndex = 1;
            RestoreFromTray();
            BtnCreateSecureShare_Click(this, new RoutedEventArgs());
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"[EXTERNAL SHARE ERROR] {ex.Message}");
            return false;
        }
    }

    #endregion

    #region עמיתים ודרופ ישיר (Peer Discovery & Direct Drop)

    private void BtnCopyPeerId_Click(object sender, RoutedEventArgs e)
    {
        string peerId = _server.PeerDiscovery.LocalPeerId;
        if (!string.IsNullOrEmpty(peerId))
        {
            Clipboard.SetText(peerId);
            TxtLastDropStatus.Text = $"מזהה {peerId} הועתק ללוח!";
            ShowNotification("EasyShare PRO", $"המזהה {peerId} הועתק ללוח בהצלחה.");
        }
    }

    private void BtnSaveDeviceName_Click(object sender, RoutedEventArgs e)
    {
        string newName = TxtLocalDeviceName.Text.Trim();
        if (!string.IsNullOrWhiteSpace(newName))
        {
            _server.Settings.DeviceName = newName;
            _server.SettingsManager.Save(_server.Settings);
            TxtLastDropStatus.Text = $"שם המכשיר עודכן ל-{newName}";
            AppendLog($"[PEER] Device name updated to: {newName}");
            MessageBox.Show($"שם המכשיר עודכן בהצלחה ל-{newName}", "EasyShare PRO", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ChkPeerDiscoveryActive_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _server?.PeerDiscovery == null || ChkPeerDiscoveryActive == null) return;
        bool active = ChkPeerDiscoveryActive.IsChecked == true;
        _server.Settings.EnablePeerDiscovery = active;
        _server.SettingsManager.Save(_server.Settings);

        if (active)
        {
            _server.PeerDiscovery.Start();
            if (TxtLastDropStatus != null) TxtLastDropStatus.Text = "שירות גילוי משתמשים הופעל.";
        }
        else
        {
            _server.PeerDiscovery.Stop();
            _discoveredPeers.Clear();
            if (TxtLastDropStatus != null) TxtLastDropStatus.Text = "שירות גילוי משתמשים הופסק.";
        }
    }

    private void ChkAutoAcceptDrops_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _server?.Settings == null || ChkAutoAcceptDrops == null) return;
        _server.Settings.AutoAcceptPeerDrops = ChkAutoAcceptDrops.IsChecked == true;
        _server.SettingsManager.Save(_server.Settings);
    }

    private async void RbVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _server?.PeerDiscovery?.InternetDiscovery == null || RbVisibilityVisible == null || RbVisibilityHidden == null) return;
        string mode = RbVisibilityVisible.IsChecked == true ? "Visible" : "Hidden";
        await _server.PeerDiscovery.InternetDiscovery.SetVisibilityModeAsync(mode);
        if (TxtLastDropStatus != null)
        {
            TxtLastDropStatus.Text = $"מצב נראות באינטרנט עודכן ל: {(mode == "Visible" ? "גלוי 🌐" : "מוסתר 🔒")}";
        }
    }

    private async void BtnSearchInternetPeers_Click(object sender, RoutedEventArgs e)
    {
        if (_server?.PeerDiscovery == null) return;
        if (TxtLastDropStatus != null) TxtLastDropStatus.Text = "סורק משתמשים גלויים באינטרנט...";
        var found = await _server.PeerDiscovery.SearchInternetPeersAsync();
        if (TxtLastDropStatus != null) TxtLastDropStatus.Text = $"נמצאו {found.Count} משתמשים גלויים באינטרנט.";
    }

    private async void BtnQueryPeer_Click(object sender, RoutedEventArgs e)
    {
        string target = TxtTargetPeerId.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show("נא להזין מזהה משתמש (למשל: SB-482-910)", "איתור משתמש", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TxtLastDropStatus.Text = $"מחפש משתמש {target} ברשת המקומית ובאינטרנט...";
        var found = await _server.PeerDiscovery.QueryPeerAsync(target);
        if (found != null)
        {
            TxtLastDropStatus.Text = $"נמצא משתמש: {found.DisplayText} ({found.SourceBadge})";
            AddOrUpdatePeerInUi(found);
            SelectPeerForChat(found);
        }
        else
        {
            TxtLastDropStatus.Text = $"משתמש {target} לא נמצא ברשת.";
            MessageBox.Show($"המשתמש {target} לא נמצא ברשת המקומית או באינטרנט.\nודא שהמזהה מדויק ושהמכשיר פועל ומחובר.", "משתמש לא נמצא", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SelectPeerForChat(PeerDevice peer)
    {
        _activeChatPeerId = peer.PeerId;
        TxtTargetPeerId.Text = peer.PeerId;
        TxtActiveChatName.Text = peer.DeviceName;
        TxtActiveChatId.Text = peer.PeerId;
        BadgeActiveChatId.Visibility = Visibility.Visible;
        TxtActiveChatStatus.Text = $"{peer.SourceBadge} • {peer.AddressText}";

        // טעינת היסטוריית שיחה עבור עמית זה
        _activeChatMessages.Clear();
        var history = _server.PeerDiscovery.GetChatHistory(peer.PeerId);
        foreach (var msg in history)
        {
            _activeChatMessages.Add(msg);
        }

        if (_activeChatMessages.Count > 0)
        {
            LstChatMessages.ScrollIntoView(_activeChatMessages[^1]);
        }
    }

    private void HandleChatMessageAdded(PeerChatMessage msg)
    {
        if (string.Equals(msg.SenderPeerId, _activeChatPeerId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(msg.RecipientPeerId, _activeChatPeerId, StringComparison.OrdinalIgnoreCase))
        {
            if (!_activeChatMessages.Any(m => m.Id == msg.Id))
            {
                _activeChatMessages.Add(msg);
                LstChatMessages.ScrollIntoView(msg);
            }
        }
    }

    private void TxtChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendMessageFromChat();
        }
    }

    private void BtnSendChatMsg_Click(object sender, RoutedEventArgs e)
    {
        SendMessageFromChat();
    }

    private void BtnSendMessageToTarget_Click(object sender, RoutedEventArgs e)
    {
        SendMessageFromChat();
    }

    private async void SendMessageFromChat()
    {
        string text = TxtChatInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = TxtTargetPeerMsg.Text.Trim();
        }

        if (string.IsNullOrWhiteSpace(text))
            return;

        string targetId = _activeChatPeerId ?? TxtTargetPeerId.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetId))
        {
            MessageBox.Show("נא לבחור משתמש מהרשימה או להזין מזהה יעד.", "צ'אט משתמשים", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var peer = _server.PeerDiscovery.FindPeer(targetId);
        if (peer == null)
        {
            TxtLastDropStatus.Text = $"מאתר משתמש {targetId}...";
            peer = await _server.PeerDiscovery.QueryPeerAsync(targetId);
        }

        if (peer == null)
        {
            MessageBox.Show($"המשתמש {targetId} אינו מקוון או טרם זוהה ברשת.", "צ'אט משתמשים", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TxtChatInput.Text = "";
        TxtTargetPeerMsg.Text = "";
        TxtLastDropStatus.Text = $"שולח הודעה אל {peer.DeviceName}...";
        var (ok, msgResult) = await _server.PeerDiscovery.SendDirectMessageAsync(peer, text);
        TxtLastDropStatus.Text = msgResult;
        if (ok)
        {
            AppendLog($"[PEER CHAT OUT] אל {peer.DeviceName} ({peer.PeerId}): {text}");
        }
        else
        {
            MessageBox.Show(msgResult, "שגיאה במסירת הודעה", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnChatAttachFile_Click(object sender, RoutedEventArgs e)
    {
        string targetId = _activeChatPeerId ?? TxtTargetPeerId.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetId))
        {
            MessageBox.Show("נא לבחור משתמש מהרשימה תחילה לשליחת קבצים.", "דרופ קבצים", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var peer = _server.PeerDiscovery.FindPeer(targetId);
        if (peer == null)
        {
            TxtLastDropStatus.Text = $"מאתר משתמש {targetId}...";
            peer = await _server.PeerDiscovery.QueryPeerAsync(targetId);
        }

        if (peer == null)
        {
            MessageBox.Show($"המשתמש {targetId} אינו מקוון או טרם זוהה ברשת.", "דרופ קבצים", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"בחר קבצים לשליחה ישירה אל {peer.DisplayText}",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
        {
            TxtLastDropStatus.Text = $"שולח {dlg.FileNames.Length} קבצים אל {peer.DeviceName}...";
            var (ok, msg) = await _server.PeerDiscovery.DropFilesToPeerAsync(peer, dlg.FileNames);
            TxtLastDropStatus.Text = msg;
            if (ok)
            {
                ShowNotification("דרופ קבצים", $"הקבצים נמסרו בהצלחה אל {peer.DeviceName}");
            }
            else
            {
                MessageBox.Show(msg, "שגיאה בדרופ קבצים", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void BtnDropFilesToTarget_Click(object sender, RoutedEventArgs e)
    {
        string targetId = _activeChatPeerId ?? TxtTargetPeerId.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetId))
        {
            MessageBox.Show("נא להזין או לבחור מזהה משתמש תחילה.", "דרופ קבצים", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var peer = _server.PeerDiscovery.FindPeer(targetId);
        if (peer == null)
        {
            TxtLastDropStatus.Text = $"מאתר משתמש {targetId}...";
            peer = await _server.PeerDiscovery.QueryPeerAsync(targetId);
        }

        if (peer == null)
        {
            MessageBox.Show($"המשתמש {targetId} אינו מקוון או טרם זוהה ברשת.", "דרופ קבצים", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"בחר קבצים לשליחה ישירה אל {peer.DisplayText}",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
        {
            TxtLastDropStatus.Text = $"שולח {dlg.FileNames.Length} קבצים אל {peer.DeviceName}...";
            var (ok, msg) = await _server.PeerDiscovery.DropFilesToPeerAsync(peer, dlg.FileNames);
            TxtLastDropStatus.Text = msg;
            if (ok)
            {
                MessageBox.Show(msg, "דרופ קבצים הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(msg, "שגיאה בדרופ קבצים", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnClearChat_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_activeChatPeerId))
            return;

        if (MessageBox.Show("האם אתה בטוח שברצונך לנקות את היסטוריית השיחה הנוכחית?", "ניקוי שיחה", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _server.PeerDiscovery.ClearChatHistory(_activeChatPeerId);
            _activeChatMessages.Clear();
            TxtLastDropStatus.Text = "היסטוריית השיחה נוקתה.";
        }
    }

    private void BtnOpenChatAttachment_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.Tag is string filePath && !string.IsNullOrEmpty(filePath))
            {
                if (File.Exists(filePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    });
                    return;
                }

                string dropsDir = Path.Combine(_server.RootDirectory, "Received_Drops");
                string candidate = Path.Combine(dropsDir, Path.GetFileName(filePath));
                if (File.Exists(candidate))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = candidate,
                        UseShellExecute = true
                    });
                    return;
                }

                if (Directory.Exists(dropsDir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dropsDir,
                        UseShellExecute = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Open attachment failed: {ex.Message}");
        }
    }

    private void BtnOpenDropsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dropsDir = Path.Combine(_server.RootDirectory, "Received_Drops");
            Directory.CreateDirectory(dropsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dropsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Failed to open drops folder: {ex.Message}");
        }
    }

    private async void BtnRefreshPeers_Click(object sender, RoutedEventArgs e)
    {
        TxtLastDropStatus.Text = "מרענן רשימת משתמשים וסורק ברשת...";
        _discoveredPeers.Clear();
        foreach (var p in _server.PeerDiscovery.GetDiscoveredPeers())
        {
            _discoveredPeers.Add(p);
        }
        await _server.PeerDiscovery.QueryPeerAsync("");
    }

    private void LstDiscoveredPeers_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstDiscoveredPeers.SelectedItem is PeerDevice peer)
        {
            SelectPeerForChat(peer);
        }
    }

    private async void BtnPeerItemDrop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PeerDevice peer)
        {
            SelectPeerForChat(peer);
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"בחר קבצים לדרופ ישיר אל {peer.DisplayText}",
                Multiselect = true
            };

            if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
            {
                TxtLastDropStatus.Text = $"שולח {dlg.FileNames.Length} קבצים אל {peer.DeviceName}...";
                var (ok, msg) = await _server.PeerDiscovery.DropFilesToPeerAsync(peer, dlg.FileNames);
                TxtLastDropStatus.Text = msg;
                if (ok)
                {
                    MessageBox.Show(msg, "דרופ קבצים הושלם", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(msg, "שגיאה בדרופ קבצים", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private void BtnPeerItemSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PeerDevice peer)
        {
            SelectPeerForChat(peer);
            TxtChatInput.Focus();
        }
    }

    private void AddOrUpdatePeerInUi(PeerDevice peer)
    {
        var existing = _discoveredPeers.FirstOrDefault(x => string.Equals(x.PeerId, peer.PeerId, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            int idx = _discoveredPeers.IndexOf(existing);
            _discoveredPeers[idx] = peer;
        }
        else
        {
            _discoveredPeers.Add(peer);
        }
    }

    private void RemovePeerFromUi(string peerId)
    {
        var existing = _discoveredPeers.FirstOrDefault(x => string.Equals(x.PeerId, peerId, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            _discoveredPeers.Remove(existing);
        }
    }

    private void HandleIncomingFileDrop(string senderId, string senderName, string fileName, string savedPath)
    {
        TxtLastDropStatus.Text = $"התקבל קובץ: {fileName} ממשתמש {senderName} ({senderId})";
        AppendLog($"[PEER DROP RECEIVED] File '{fileName}' from {senderName} ({senderId}) saved to: {savedPath}");
        ShowNotification("דרופ קבצים התקבל!", $"התקבל קובץ חדש '{fileName}' ממשתמש {senderName}.\nנשמר בתיקיית Received_Drops.");
    }

    private void HandleIncomingPeerMessage(string senderId, string senderName, string message)
    {
        TxtLastDropStatus.Text = $"הודעה ממשתמש {senderName}: {message}";
        AppendLog($"[PEER CHAT IN] From {senderName} ({senderId}): {message}");
        ShowNotification($"הודעה מ-{senderName}", message);
    }

    #endregion

    #region 12. בדיקת עדכונים מ-GitHub (Update Checker)

    private string? _latestReleaseUrl;

    private async Task CheckForUpdatesInternalAsync(bool isManualCheck)
    {
        var loc = LocalizationService.Instance;

        if (isManualCheck)
        {
            AppendLog($"[UPDATE] {loc["UpdateChecking"]}");
        }

        try
        {
            var res = await UpdateCheckerService.CheckForUpdatesAsync();

            Dispatcher.Invoke(() =>
            {
                if (res.HasUpdate)
                {
                    _latestReleaseUrl = !string.IsNullOrEmpty(res.DownloadUrl) ? res.DownloadUrl : res.ReleaseUrl;

                    // 1. הצגת באנר פנימי בראש חלון האפליקציה
                    if (BorderUpdateBanner != null)
                    {
                        BorderUpdateBanner.Visibility = Visibility.Visible;
                    }
                    if (TxtUpdateBannerMessage != null)
                    {
                        TxtUpdateBannerMessage.Text = $"🚀 קיים עדכון חדש: גרסה {res.LatestVersion} (הגרסה הנוכחית שלך: {res.CurrentVersion})";
                    }

                    // 2. שליחת הודעת Toast מערכתית
                    ShowNotification(
                        "EasyShare PRO | עדכון גרסה חדש",
                        $"גרסה {res.LatestVersion} זמינה כעת להורדה (הנוכחית: {res.CurrentVersion})!"
                    );

                    // 3. פתיחת דיאלוג מודרני מותאם אישית
                    string notesSummary = !string.IsNullOrWhiteSpace(res.ReleaseNotes)
                        ? (res.ReleaseNotes.Length > 200 ? res.ReleaseNotes[..200] + "..." : res.ReleaseNotes)
                        : string.Empty;

                    string prompt = string.Format(loc["UpdateAvailableMsg"], res.LatestVersion, res.CurrentVersion, string.IsNullOrEmpty(notesSummary) ? "" : notesSummary + "\n\n");
                    var ans = ModernDialog.Show(prompt, loc["UpdateAvailableTitle"], MessageBoxButton.YesNo, MessageBoxImage.Information, this);
                    if (ans == MessageBoxResult.Yes)
                    {
                        UpdateCheckerService.OpenReleaseInBrowser(_latestReleaseUrl ?? res.ReleaseUrl);
                    }
                }
                else if (isManualCheck)
                {
                    if (!string.IsNullOrEmpty(res.ErrorMessage))
                    {
                        ModernDialog.Show(
                            string.Format(loc["UpdateCheckFailedMsg"], res.ErrorMessage),
                            loc["UpdateCheckFailedTitle"],
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning,
                            this
                        );
                    }
                    else
                    {
                        ModernDialog.Show(
                            string.Format(loc["UpdateUpToDateMsg"], res.CurrentVersion),
                            loc["UpdateUpToDateTitle"],
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            this
                        );
                    }
                }
            });
        }
        catch (Exception ex)
        {
            if (isManualCheck)
            {
                Dispatcher.Invoke(() =>
                {
                    ModernDialog.Show(
                        string.Format(loc["UpdateCheckFailedMsg"], ex.Message),
                        loc["UpdateCheckFailedTitle"],
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        this
                    );
                });
            }
        }
    }

    private void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        _ = CheckForUpdatesInternalAsync(isManualCheck: true);
    }

    private void BtnDownloadUpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        UpdateCheckerService.OpenReleaseInBrowser(_latestReleaseUrl ?? $"https://github.com/{UpdateCheckerService.GitHubOwner}/{UpdateCheckerService.GitHubRepo}/releases/latest");
    }

    private void BtnDismissUpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        if (BorderUpdateBanner != null)
        {
            BorderUpdateBanner.Visibility = Visibility.Collapsed;
        }
    }

    #endregion
}



