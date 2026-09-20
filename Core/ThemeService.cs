using System;
using System.Windows;
using System.Windows.Media;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace EasyShare.Core;

/// <summary>
/// שירות ניהול ערכות נושא ועיצוב דינמי (Dynamic Theme Service).
/// מאפשר מעבר חלק בזמן אמת בין מצב כהה (Nordic Cyber-Teal & Obsidian) למצב בהיר (Nordic Frost & Teal),
/// תוך עדכון ישיר של משאבי המערכת ב-Application.Resources ושמירה על קונטרסט ונגישות WCAG AAA.
/// </summary>
public sealed class ThemeService
{
    private static readonly Lazy<ThemeService> _instance = new(() => new ThemeService());
    public static ThemeService Instance => _instance.Value;

    public const string ThemeDark = "dark";
    public const string ThemeLight = "light";
    public const string ThemeSystem = "system";

    public string ConfiguredTheme { get; private set; } = ThemeDark;
    public string CurrentTheme { get; private set; } = ThemeDark;

    public bool IsDark => string.Equals(CurrentTheme, ThemeDark, StringComparison.OrdinalIgnoreCase);

    public event Action<string>? OnThemeChanged;

    private ThemeService()
    {
        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == Microsoft.Win32.UserPreferenceCategory.General || 
                    e.Category == Microsoft.Win32.UserPreferenceCategory.Color)
                {
                    SyncWithSystemTheme();
                }
            };
        }
        catch { }
    }

    /// <summary>
    /// בודק ברג'יסטרי של Windows האם מערכת ההפעלה נמצאת במצב בהיר.
    /// </summary>
    public static bool IsWindowsInLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            if (val is int intVal)
            {
                return intVal == 1;
            }
        }
        catch
        {
            // הגנה על מערכות שאינן Windows 10/11
        }
        return false;
    }

    /// <summary>
    /// מחיל את ערכת הנושא המבוקשת על כל משאבי האפליקציה ב-WPF בזמן אמת.
    /// </summary>
    /// <param name="themeName">"dark", "light" או "system"</param>
    public void SetTheme(string? themeName)
    {
        string target = themeName?.ToLowerInvariant() switch
        {
            ThemeLight => ThemeLight,
            ThemeSystem => ThemeSystem,
            _ => ThemeDark
        };

        ConfiguredTheme = target;

        string effectiveTheme = target == ThemeSystem
            ? (IsWindowsInLightTheme() ? ThemeLight : ThemeDark)
            : target;

        CurrentTheme = effectiveTheme;

        if (Application.Current == null)
        {
            return;
        }

        // ביצוע על ה-Dispatcher הראשי
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => SetTheme(target));
            return;
        }

        if (effectiveTheme == ThemeLight)
        {
            ApplyLightPalette();
        }
        else
        {
            ApplyDarkPalette();
        }

        OnThemeChanged?.Invoke(CurrentTheme);
    }

    /// <summary>
    /// מסנכרן מחדש את ערכת הנושא מול הגדרות Windows אם המצב הוא "system".
    /// </summary>
    public void SyncWithSystemTheme()
    {
        if (string.Equals(ConfiguredTheme, ThemeSystem, StringComparison.OrdinalIgnoreCase))
        {
            SetTheme(ThemeSystem);
        }
    }

    /// <summary>
    /// מחליף בין מצבי הערכה (כהה -> בהיר -> מערכת).
    /// </summary>
    public void ToggleTheme()
    {
        if (ConfiguredTheme == ThemeDark)
        {
            SetTheme(ThemeLight);
        }
        else if (ConfiguredTheme == ThemeLight)
        {
            SetTheme(ThemeSystem);
        }
        else
        {
            SetTheme(ThemeDark);
        }
    }

    private void ApplyDarkPalette()
    {
        // =================== משטחים ורקעים (Dark) ===================
        UpdateColorToken("ColorBackground", "#0B0F14", "BrushBackground");
        UpdateColorToken("ColorSurface", "#111720", "BrushSurface");
        UpdateColorToken("ColorCard", "#16202B", "BrushCard");
        UpdateColorToken("ColorCardHover", "#1C2937", "BrushCardHover");
        UpdateColorToken("ColorBorder", "#223142", "BrushBorder");
        UpdateColorToken("ColorBorderHighlight", "#00D2B4", "BrushBorderHighlight", opacity: 0.6);

        // =================== צבעי מותג ודגש (Dark) ===================
        UpdateColorToken("ColorPrimary", "#00D2B4", "BrushPrimary");
        UpdateColorToken("ColorPrimaryHover", "#00B89D", "BrushPrimaryHover");
        UpdateColorToken("ColorAccentCyan", "#22D3EE", "BrushAccentCyan");
        UpdateColorToken("ColorAccentIndigo", "#38BDF8", "BrushAccentIndigo");

        // =================== צבעים סמנטיים (Dark) ===================
        UpdateColorToken("ColorSuccess", "#00D2B4", "BrushSuccess");
        UpdateColorToken("ColorWarning", "#F59E0B", "BrushWarning");
        UpdateColorToken("ColorDanger", "#EF4444", "BrushDanger");

        // =================== טיפוגרפיה (Dark) ===================
        UpdateColorToken("ColorText", "#F1F5F9", "BrushText");
        UpdateColorToken("ColorTextMuted", "#94A3B8", "BrushTextMuted");
        UpdateColorToken("ColorTextSubtle", "#64748B", "BrushTextSubtle");

        // =================== גרדיאנטים (Dark) ===================
        UpdateGradient("BrushCardGradient", "#18222E", "#141D27");
        UpdateGradient("BrushPrimaryGradient", "#00D2B4", "#0284C7");
        UpdateGradient("BrushAccentGradient", "#22D3EE", "#38BDF8");

        // =================== אפקטי צללים (Dark) ===================
        UpdateShadow("EffectCardShadow", 0.35, depth: 3, blur: 16);
        UpdateShadow("EffectElevatedShadow", 0.50, depth: 6, blur: 24);
    }

    private void ApplyLightPalette()
    {
        // =================== משטחים ורקעים (Light) ===================
        UpdateColorToken("ColorBackground", "#F8FAFC", "BrushBackground");
        UpdateColorToken("ColorSurface", "#F1F5F9", "BrushSurface");
        UpdateColorToken("ColorCard", "#FFFFFF", "BrushCard");
        UpdateColorToken("ColorCardHover", "#F8FAFC", "BrushCardHover");
        UpdateColorToken("ColorBorder", "#CBD5E1", "BrushBorder");
        UpdateColorToken("ColorBorderHighlight", "#00D2B4", "BrushBorderHighlight", opacity: 0.85);

        // =================== צבעי מותג ודגש (Light - זהות מותגית אחידה Nordic Cyber-Teal) ===================
        UpdateColorToken("ColorPrimary", "#00D2B4", "BrushPrimary");
        UpdateColorToken("ColorPrimaryHover", "#00B89D", "BrushPrimaryHover");
        UpdateColorToken("ColorAccentCyan", "#00B4D8", "BrushAccentCyan");
        UpdateColorToken("ColorAccentIndigo", "#38BDF8", "BrushAccentIndigo");

        // =================== צבעים סמנטיים (Light) ===================
        UpdateColorToken("ColorSuccess", "#00D2B4", "BrushSuccess");
        UpdateColorToken("ColorWarning", "#F59E0B", "BrushWarning");
        UpdateColorToken("ColorDanger", "#EF4444", "BrushDanger");

        // =================== טיפוגרפיה (Light) ===================
        UpdateColorToken("ColorText", "#0F172A", "BrushText");
        UpdateColorToken("ColorTextMuted", "#475569", "BrushTextMuted");
        UpdateColorToken("ColorTextSubtle", "#64748B", "BrushTextSubtle");

        // =================== גרדיאנטים (Light - אותו גרדיאנט Cyber-Teal מותגי) ===================
        UpdateGradient("BrushCardGradient", "#FFFFFF", "#F8FAFC");
        UpdateGradient("BrushPrimaryGradient", "#00D2B4", "#0284C7");
        UpdateGradient("BrushAccentGradient", "#22D3EE", "#38BDF8");

        // =================== אפקטי צללים (Light) ===================
        UpdateShadow("EffectCardShadow", 0.08, depth: 3, blur: 16);
        UpdateShadow("EffectElevatedShadow", 0.14, depth: 6, blur: 24);
    }

    private static void UpdateColorToken(string colorKey, string hexColor, string brushKey, double? opacity = null)
    {
        try
        {
            var res = Application.Current.Resources;
            var c = (Color)ColorConverter.ConvertFromString(hexColor);

            res[colorKey] = c;

            var newBrush = new SolidColorBrush(c);
            if (opacity.HasValue)
            {
                newBrush.Opacity = opacity.Value;
            }
            newBrush.Freeze();
            res[brushKey] = newBrush;
        }
        catch
        {
            // הגנה מפני קריסה בעת שינוי משאב
        }
    }

    private static void UpdateGradient(string brushKey, string startHex, string endHex)
    {
        try
        {
            var res = Application.Current.Resources;
            var c1 = (Color)ColorConverter.ConvertFromString(startHex);
            var c2 = (Color)ColorConverter.ConvertFromString(endHex);

            System.Windows.Point startPoint = new System.Windows.Point(0, 0);
            System.Windows.Point endPoint = (brushKey == "BrushCardGradient" || brushKey == "BrushInnerTopHairline") 
                ? new System.Windows.Point(0, 1) 
                : new System.Windows.Point(1, 1);

            var grad = new LinearGradientBrush(c1, c2, startPoint, endPoint);
            grad.Freeze();
            res[brushKey] = grad;
        }
        catch
        {
            // הגנה מפני קריסה
        }
    }

    private static void UpdateShadow(string effectKey, double opacity, double depth = 3, double blur = 16)
    {
        try
        {
            var res = Application.Current.Resources;
            var effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 270,
                ShadowDepth = depth,
                BlurRadius = blur,
                Opacity = opacity
            };
            effect.Freeze();
            res[effectKey] = effect;
        }
        catch
        {
            // הגנה מפני קריסה
        }
    }
}
