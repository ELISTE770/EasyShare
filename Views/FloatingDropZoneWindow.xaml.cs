using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EasyShare.Core;
using EasyShare.Services;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;
using DragEventArgs = System.Windows.DragEventArgs;
using DataFormats = System.Windows.DataFormats;
using Color = System.Windows.Media.Color;

namespace EasyShare.Views;

public partial class FloatingDropZoneWindow : Window
{
    private readonly LocalWebServerService _server;
    private Storyboard? _pulseAnim;

    public FloatingDropZoneWindow(LocalWebServerService server)
    {
        InitializeComponent();
        _server = server;
        _pulseAnim = (Storyboard)FindResource("PulseGlow");

        // מיקום התחלתי: פינה ימנית תחתונה מעל שורת המשימות
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(20, workArea.Right - Width - 30);
        Top = Math.Max(20, workArea.Bottom - Height - 50);

        ApplyLanguage();
        LocalizationService.Instance.OnLanguageChanged += lang => Dispatcher.Invoke(ApplyLanguage);
    }

    private void ApplyLanguage()
    {
        TxtDropLabel.Text = LocalizationService.Instance["DropZoneText"];
        FlowDirection = LocalizationService.Instance.CurrentFlowDirection;
        SetupContextMenu();
    }

    private void SetupContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        bool isHe = LocalizationService.Instance.CurrentLanguage == LocalizationService.LanguageHebrew;
        var mnuOpen = new System.Windows.Controls.MenuItem { Header = isHe ? "פתח את החלון הראשי" : "Open Main Window" };
        mnuOpen.Click += (s, e) =>
        {
            var win = System.Windows.Application.Current.MainWindow;
            if (win != null)
            {
                win.Show();
                win.WindowState = WindowState.Normal;
                win.Activate();
            }
        };

        var mnuSnap = new System.Windows.Controls.MenuItem { Header = isHe ? "הצמד לפינת המסך" : "Snap to Corner" };
        mnuSnap.Click += (s, e) => SnapToCorner();

        var mnuHide = new System.Windows.Controls.MenuItem { Header = isHe ? "הסתר ווידג'ט" : "Hide Widget" };
        mnuHide.Click += (s, e) => Hide();

        menu.Items.Add(mnuOpen);
        menu.Items.Add(mnuSnap);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(mnuHide);

        MainCircle.ContextMenu = menu;
    }

    private void DropZone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            SnapIfCloseToEdge();
        }
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            _pulseAnim?.Begin();
            OuterGlow.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
            TxtIcon.Text = "↓";
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        ResetVisuals();
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        ResetVisuals();

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;

        string target = files[0];

        try
        {
            PnlNormal.Visibility = Visibility.Collapsed;
            PrgBusy.Visibility = Visibility.Visible;

            string? url = await Task.Run(async () =>
            {
                return await SecureTransferService.UploadToDirectPublicCloudAsync(target);
            });

            if (!string.IsNullOrEmpty(url))
            {
                Clipboard.SetText(url);
                TxtIcon.Text = "✓";
                PnlNormal.Visibility = Visibility.Visible;
                PrgBusy.Visibility = Visibility.Collapsed;

                OuterGlow.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                await Task.Delay(1800);
                TxtIcon.Text = "↑";
            }
        }
        catch (Exception ex)
        {
            bool isHe = LocalizationService.Instance.CurrentLanguage == LocalizationService.LanguageHebrew;
            string errTitle = isHe ? "שגיאת ווידג'ט שיתוף" : "EasyShare Drop-Zone Error";
            string errMsg = isHe ? $"שגיאה בשיתוף: {ex.Message}" : $"Sharing error: {ex.Message}";
            MessageBox.Show(errMsg, errTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ResetVisuals();
        }
    }

    private void ResetVisuals()
    {
        _pulseAnim?.Stop();
        OuterGlow.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        OuterGlow.Opacity = 0.25;
        ScaleTransform.ScaleX = 1.0;
        ScaleTransform.ScaleY = 1.0;
        PnlNormal.Visibility = Visibility.Visible;
        PrgBusy.Visibility = Visibility.Collapsed;
    }

    private void SnapIfCloseToEdge()
    {
        var area = SystemParameters.WorkArea;
        const double threshold = 40;

        if (Math.Abs(Left - area.Left) < threshold) Left = area.Left + 10;
        else if (Math.Abs(Left + Width - area.Right) < threshold) Left = area.Right - Width - 10;

        if (Math.Abs(Top - area.Top) < threshold) Top = area.Top + 10;
        else if (Math.Abs(Top + Height - area.Bottom) < threshold) Top = area.Bottom - Height - 10;
    }

    private void SnapToCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 20;
        Top = area.Bottom - Height - 30;
    }
}
