using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyShare.Core;
using EasyShare.Models;
using EasyShare.Services;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
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
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LocalizationService.Instance.SetLanguage(_server.Settings.Language ?? "he");
        LocalizationService.Instance.OnLanguageChanged += lang => Dispatcher.Invoke(() => ApplyLanguage(lang));
        ApplyLanguage(LocalizationService.Instance.CurrentLanguage);

        InitializeTrayIcon();
        LoadSettingsToUi();
        StartServer();
        TxtSecurePin.Text = SecureTransferService.GenerateRandomPin(8);
        RefreshTransfersList();
        UpdateCloudServicesStatus();

        // 1. הפעלת שרת IPC לקבלת שיתופים מסייר הקבצים
        SingleInstanceIpcService.StartIpcServer();
        SingleInstanceIpcService.OnShareRequested += (channel, path) => Dispatcher.Invoke(() => HandleExternalShareRequest(channel, path));

        // 2. הפעלת מנהל קיצור מקשים גלובלי Alt+S
        if (_server.Settings.EnableGlobalHotkey)
        {
            _hotkeyManager = new Core.GlobalHotkeyManager(this);
            _hotkeyManager.OnQuickShareCompleted += url => Dispatcher.Invoke(() =>
            {
                var loc = LocalizationService.Instance;
                _notifyIcon?.ShowBalloonTip(3000, loc["HeaderTitle"], string.Format(loc["BalloonQuickShareCreated"], url), System.Windows.Forms.ToolTipIcon.Info);
                AppendLog($"[HOTKEY] Created quick share: {url}");
            });
        }

        // 3. הפעלת מעקב תיקיית סנכרון (Watch Folder)
        if (_server.Settings.EnableWatchFolder && !string.IsNullOrEmpty(_server.Settings.WatchFolderPath))
        {
            _watchFolderService = new WatchFolderService(_server.Settings.WatchFolderPath);
            _watchFolderService.OnFileSynced += (path, url) => Dispatcher.Invoke(() =>
            {
                var loc = LocalizationService.Instance;
                _notifyIcon?.ShowBalloonTip(3000, loc["BalloonAutoSyncTitle"], string.Format(loc["BalloonAutoSync"], Path.GetFileName(path)), System.Windows.Forms.ToolTipIcon.Info);
                AppendLog($"[WATCH FOLDER] Auto-synced {Path.GetFileName(path)}: {url}");
            });
            _watchFolderService.Start();
        }

        // 4. חיבור ניטור רשת חי
        LiveNetworkMonitorService.Instance.OnSpeedUpdated += speedMbps => Dispatcher.Invoke(() => UpdateSpeedGraph(speedMbps));
        LiveNetworkMonitorService.Instance.OnConnectionsChanged += () => Dispatcher.Invoke(RefreshLiveConnections);

        // 5. בדיקת ווידג'ט צף
        if (_server.Settings.EnableFloatingDropZone)
        {
            ToggleDropZone();
        }
    }

    private void UpdateCloudServicesStatus()
    {
        try
        {
            var oneDrive = CloudDriveService.GetOneDriveInfo();
            var gdrive = CloudDriveService.GetGoogleDriveInfo();
            bool isHe = LocalizationService.Instance.CurrentLanguage == LocalizationService.LanguageHebrew;

            string od = oneDrive.IsAvailableLocally 
                ? (isHe ? "OneDrive: מסונכרן מקומית" : "OneDrive: Locally synced")
                : (isHe ? "OneDrive: זמין ב-Web" : "OneDrive: Available on Web");
            string gd = gdrive.IsAvailableLocally 
                ? (isHe ? "Google Drive: כונן מקומי פעיל" : "Google Drive: Local drive active")
                : (isHe ? "Google Drive: זמין ב-Web" : "Google Drive: Available on Web");

            TxtCloudServicesStatus.Text = $"{od}  |  {gd}";
        }
        catch { }
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

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _server.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isExplicitExit)
        {
            _server.Dispose();
            _notifyIcon?.Dispose();
            base.OnClosing(e);
            return;
        }

        bool minimizeToTray = !string.Equals(_server.Settings.CloseAction, "ExitApplication", StringComparison.OrdinalIgnoreCase);
        if (minimizeToTray)
        {
            // מזעור למגש המערכת במקום סגירה מלאה שמפילה את השרת
            e.Cancel = true;
            Hide();
            var loc = LocalizationService.Instance;
            _notifyIcon?.ShowBalloonTip(2500, loc["HeaderTitle"], loc["BalloonServerRunningBg"], System.Windows.Forms.ToolTipIcon.Info);
        }
        else
        {
            // סגירה מלאה ויציאה מהתוכנה (מכבה את השרת לחלוטין)
            ExitApplication();
        }
    }

    private void BtnMinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        var loc = LocalizationService.Instance;
        _notifyIcon?.ShowBalloonTip(2500, loc["HeaderTitle"], loc["BalloonTrayMinimized"], System.Windows.Forms.ToolTipIcon.Info);
    }

    #endregion

    #region ניהול שרת רשת ומדיה

    private void StartServer()
    {
        try
        {
            _server.Start();

            TxtServerStatus.Text = LocalizationService.Instance["ServerRunning"];
            TxtServerStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Emerald Green
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            BtnToggleServerTop.Content = LocalizationService.Instance["StopServer"];
            BtnToggleServerTop.Background = new SolidColorBrush(Color.FromArgb(40, 239, 68, 68));
            BtnToggleServerTop.BorderBrush = new SolidColorBrush(Color.FromArgb(180, 239, 68, 68));
            BtnToggleServerTop.BorderThickness = new Thickness(1);
            BtnToggleServerTop.Foreground = new SolidColorBrush(Color.FromRgb(254, 202, 202));

            TxtLocalUrl.Text = _server.LocalUrl;
            UpdateQrCodeImage(_server.LocalUrl);
            TxtFooterInfo.Text = $"{LocalizationService.Instance["HeaderTitle"]} | Port {_server.Port} | Mode: {_server.Settings.AccessMode}";
        }
        catch (Exception ex)
        {
            TxtServerStatus.Text = LocalizationService.Instance["MsgErrorTitle"];
            TxtServerStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            BtnToggleServerTop.Content = LocalizationService.Instance["StartServer"];
            BtnToggleServerTop.Background = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129));
            BtnToggleServerTop.BorderBrush = new SolidColorBrush(Color.FromArgb(180, 16, 185, 129));
            BtnToggleServerTop.BorderThickness = new Thickness(1);
            BtnToggleServerTop.Foreground = new SolidColorBrush(Color.FromRgb(167, 243, 208));

            AppendLog($"[SERVER ERROR] {ex.Message}");
            MessageBox.Show($"Error starting server on port {_server.Port}:\n{ex.Message}", LocalizationService.Instance["MsgErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopServer()
    {
        _server.Stop();

        TxtServerStatus.Text = LocalizationService.Instance["ServerStopped"];
        TxtServerStatus.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)); // Gray Muted
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        BtnToggleServerTop.Content = LocalizationService.Instance["StartServer"];
        BtnToggleServerTop.Background = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129));
        BtnToggleServerTop.BorderBrush = new SolidColorBrush(Color.FromArgb(180, 16, 185, 129));
        BtnToggleServerTop.BorderThickness = new Thickness(1);
        BtnToggleServerTop.Foreground = new SolidColorBrush(Color.FromRgb(167, 243, 208));

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

    private void HandleTunnelUrlChanged(string url)
    {
        TxtTunnelUrl.Text = url;
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
            TxtTunnelUrl.Text = displayUrl;
            BtnOpenTunnelBrowser.IsEnabled = true;
            BtnCopyTunnelUrl.IsEnabled = true;
            UpdateQrCodeImage(displayUrl);
        }
        else
        {
            TxtTunnelUrl.Text = "יוצר חיבור מנהור מהיר דרך Cloudflare...";
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
                        TxtTunnelUrl.Text = "שגיאה בהפעלת מנהור Cloudflare (בדוק חיבור אינטרנט או תקינות ה-Token).";
                        ChkEnableTunnel.IsChecked = false;
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtTunnelUrl.Text = $"שגיאה בהפעלת מנהור: {ex.Message}";
                    ChkEnableTunnel.IsChecked = false;
                    AppendLog($"[TUNNEL ERROR] {ex.Message}");
                });
            }
        });
    }

    private void ChkEnableTunnel_Unchecked(object sender, RoutedEventArgs e)
    {
        _server.Settings.EnableCloudflareTunnel = false;
        _server.SettingsManager.Save();

        _server.TunnelService.Stop();
        TxtTunnelUrl.Text = "המנהור כבוי (סמן 'הפעל מנהור' לפתיחת כתובת ציבורית)";
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
                if (string.Equals(item.Tag?.ToString(), settings.CloseAction, StringComparison.OrdinalIgnoreCase))
                {
                    CmbSettingsCloseAction.SelectedItem = item;
                    break;
                }
            }
        }

        // טעינת שפה ולוקליזציה
        LocalizationService.Instance.SetLanguage(settings.Language ?? LocalizationService.LanguageHebrew);
        ApplyLocalization();
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
        if (RbTunnelRandom != null) RbTunnelRandom.Content = isHe ? "מנהור מהיר (Cloudflare Quick Tunnel)" : "Quick Tunnel (Cloudflare)";
        if (RbTunnelCustom != null) RbTunnelCustom.Content = isHe ? "דומיין אישי (Cloudflare Token)" : "Custom Domain (with Cloudflare Token)";
        if (LblCustomDomainUrl != null) LblCustomDomainUrl.Text = loc["LblCustomDomainUrl"];
        if (LblTunnelToken != null) LblTunnelToken.Text = loc["LblTunnelToken"];
        if (TxtCustomDomainUrl != null) TxtCustomDomainUrl.ToolTip = loc["TipCustomDomainUrl"];
        if (TxtTunnelToken != null) TxtTunnelToken.ToolTip = loc["TipTunnelToken"];
        if (TxtTunnelUrl != null && !_server.TunnelService.IsRunning)
        {
            TxtTunnelUrl.Text = loc["TunnelDisabled"];
        }
        if (BtnOpenTunnelBrowser != null) BtnOpenTunnelBrowser.Content = loc["OpenPublicUrl"];
        if (BtnCopyTunnelUrl != null) BtnCopyTunnelUrl.Content = loc["CopyLink"];

        if (LblEventLogTitle != null) LblEventLogTitle.Text = loc["ConsoleLogTitle"];
        if (LblQrTitle != null) LblQrTitle.Text = loc["QrScanTitle"];
        if (LblQrSubtitle != null) LblQrSubtitle.Text = loc["QrScanSubtitle"];
        if (LblQrFooter != null) LblQrFooter.Text = isHe ? "חיבור רשת ישיר | נגני מדיה | העלאה והורדה" : "Direct Network Link | Media Players | Upload & Download";

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
        if (LblLiveConnectionsTitle != null) LblLiveConnectionsTitle.Text = loc["LiveConnectionsTitle"];
        if (ColSource != null) ColSource.Header = loc["ColSource"];
        if (ColClientIp != null) ColClientIp.Header = loc["ColClientIp"];
        if (ColTargetFile != null) ColTargetFile.Header = loc["ColTargetFile"];
        if (ColProgress != null) ColProgress.Header = loc["ColProgress"];
        if (ColActions != null) ColActions.Header = loc["ColActions"];

        // Bottom Bar
        if (TxtFooterInfo != null) TxtFooterInfo.Text = $"{loc["HeaderTitle"]} | Port {_server.Port} | Mode: {_server.Settings.AccessMode}";
        if (BtnBottomDropZone != null) BtnBottomDropZone.Content = loc["BtnBottomDropZone"];
        if (BtnBottomContextMenu != null) BtnBottomContextMenu.Content = loc["BtnBottomContextMenu"];
        if (BtnBottomMinimize != null) BtnBottomMinimize.Content = loc["BtnBottomMinimize"];

        _server.Settings.Language = loc.CurrentLanguage;
        UpdateTrayMenu();
        UpdateCloudServicesStatus();
        RefreshTransfersList();
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

    private async void HandleExternalShareRequest(string channel, string path)
    {
        if (channel == "ACTIVATE")
        {
            RestoreFromTray();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            return;
        }

        AppendLog($"[EXTERNAL SHARE] Received share request for {Path.GetFileName(path)} (Channel: {channel})");
        try
        {
            var loc = LocalizationService.Instance;
            if (channel == "DirectCloud")
            {
                string? url = await SecureTransferService.UploadToDirectPublicCloudAsync(path);
                if (!string.IsNullOrEmpty(url))
                {
                    Clipboard.SetText(url);
                    _notifyIcon?.ShowBalloonTip(3500, loc["HeaderTitle"], string.Format(loc["BalloonCloudUploaded"], Path.GetFileName(path), url), System.Windows.Forms.ToolTipIcon.Info);
                    RefreshTransfersList();
                    return;
                }
            }
            else if (channel == "LocalCloud")
            {
                await ExecuteCloudShareAsync("GoogleDrive", path);
                return;
            }

            TxtSecureTarget.Text = path;
            MainTabControl.SelectedIndex = 1;
            RestoreFromTray();
            BtnCreateSecureShare_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            AppendLog($"[EXTERNAL SHARE ERROR] {ex.Message}");
        }
    }

    #endregion
}
