using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Application = System.Windows.Application;

namespace EasyShare.Views;

/// <summary>
/// חלון דיאלוג והודעות מודרני, מעוצב ומותאם לערכת הנושא של EasyShare PRO.
/// מחליף את תיבות ה-MessageBox הרגילות של Windows בעיצוב Obsidian יוקרתי.
/// </summary>
public partial class ModernDialog : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;
    private readonly MessageBoxButton _buttons;

    public ModernDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();
        _buttons = buttons;
        TxtTitle.Text = title;
        TxtMessage.Text = message;

        ConfigureIcon(icon);
        ConfigureButtons(buttons);
    }

    private void ConfigureIcon(MessageBoxImage icon)
    {
        switch (icon)
        {
            case MessageBoxImage.Information:
                TxtIcon.Text = "ℹ";
                TxtIcon.Foreground = (Brush)FindResource("BrushAccentCyan");
                IconBadge.BorderBrush = (Brush)FindResource("BrushAccentCyan");
                break;
            case MessageBoxImage.Warning:
                TxtIcon.Text = "⚠";
                TxtIcon.Foreground = (Brush)FindResource("BrushWarning");
                IconBadge.BorderBrush = (Brush)FindResource("BrushWarning");
                break;
            case MessageBoxImage.Error:
                TxtIcon.Text = "✖";
                TxtIcon.Foreground = (Brush)FindResource("BrushDanger");
                IconBadge.BorderBrush = (Brush)FindResource("BrushDanger");
                break;
            case MessageBoxImage.Question:
                TxtIcon.Text = "❔";
                TxtIcon.Foreground = (Brush)FindResource("BrushAccentIndigo");
                IconBadge.BorderBrush = (Brush)FindResource("BrushAccentIndigo");
                break;
            default: // Success / None
                TxtIcon.Text = "✓";
                TxtIcon.Foreground = (Brush)FindResource("BrushSuccess");
                IconBadge.BorderBrush = (Brush)FindResource("BrushSuccess");
                break;
        }
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                BtnPrimary.Content = "אישור";
                BtnSecondary.Visibility = Visibility.Collapsed;
                break;
            case MessageBoxButton.OKCancel:
                BtnPrimary.Content = "אישור";
                BtnSecondary.Content = "ביטול";
                BtnSecondary.Visibility = Visibility.Visible;
                break;
            case MessageBoxButton.YesNo:
                BtnPrimary.Content = "כן";
                BtnSecondary.Content = "לא";
                BtnSecondary.Visibility = Visibility.Visible;
                break;
            case MessageBoxButton.YesNoCancel:
                BtnPrimary.Content = "כן";
                BtnSecondary.Content = "לא";
                BtnSecondary.Visibility = Visibility.Visible;
                break;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Result = _buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK
        };
        Close();
    }

    private void BtnPrimary_Click(object sender, RoutedEventArgs e)
    {
        Result = _buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.Yes,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Yes,
            _ => MessageBoxResult.OK
        };
        Close();
    }

    private void BtnSecondary_Click(object sender, RoutedEventArgs e)
    {
        Result = _buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel
        };
        Close();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            BtnPrimary_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            BtnClose_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    /// <summary>
    /// מציג הודעה מעוצבת למשתמש כתחליף ל-MessageBox.Show.
    /// </summary>
    public static MessageBoxResult Show(string message, string title = "EasyShare PRO", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information, Window? owner = null)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            return Application.Current.Dispatcher.Invoke(() => Show(message, title, buttons, icon, owner));
        }

        var dlg = new ModernDialog(message, title, buttons, icon);
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible)
        {
            dlg.Owner = Application.Current.MainWindow;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dlg.ShowDialog();
        return dlg.Result;
    }
}
