using System;
using System.Collections.Generic;
using System.Windows;

namespace EasyShare.Core;

/// <summary>
/// שירות לוקליזציה וניהול שפות מרכזי (עברית ואנגלית).
/// מנהל את תרגום כל הטקסטים באפליקציית שולחן העבודה, כיווניות ה-UI (RTL/LTR),
/// ומשדר אירוע עדכון חי לכל החלונות הפעילים.
/// </summary>
public sealed class LocalizationService
{
    private static readonly Lazy<LocalizationService> _instance = new(() => new LocalizationService());
    public static LocalizationService Instance => _instance.Value;

    public const string LanguageHebrew = "he";
    public const string LanguageEnglish = "en";

    public string CurrentLanguage { get; private set; } = LanguageHebrew;

    public System.Windows.FlowDirection CurrentFlowDirection => CurrentLanguage == LanguageHebrew ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;

    public event Action<string>? OnLanguageChanged;

    private readonly Dictionary<string, Dictionary<string, string>> _translations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["he"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // כותרות ומצב ראשי
            ["AppTitle"] = "EasyShare PRO | מרכז שיתוף קבצים, סטרימינג וענן",
            ["HeaderTitle"] = "EasyShare PRO",
            ["HeaderSubtitle"] = "Enterprise File Sharing & Cloud Streaming Suite",
            ["ServerRunning"] = "פעיל ומאזין",
            ["ServerStopped"] = "שרת מושבת",
            ["StopServer"] = "עצור שרת",
            ["StartServer"] = "הפעל שרת",
            ["LangHebrew"] = "עברית",
            ["LangEnglish"] = "English",

            // טאבים
            ["TabWebServer"] = "רשת ומדיה",
            ["TabSecureShare"] = "שיתוף מאובטח",
            ["TabSettings"] = "הגדרות",
            ["TabMonitor"] = "ניטור ובקרה",

            // טאב 1: שרת רשת ומדיה
            ["LocalNetworkTitle"] = "כתובת ברשת המקומית (Wi-Fi / LAN)",
            ["LocalNetworkSubtitle"] = "התחבר מכל מכשיר המחובר לאותה הרשת המקומית",
            ["OpenInBrowser"] = "פתח בדפדפן",
            ["CopyAddress"] = "העתק כתובת",
            ["TunnelTitle"] = "מנהור גלובלי מאובטח (Cloudflare Tunnel)",
            ["TunnelSubtitle"] = "שיתוף קבצים מאובטח ב-HTTPS מכל מקום בעולם ללא פתיחת פורטים",
            ["EnableTunnel"] = "הפעל מנהור",
            ["TunnelStarting"] = "יוצר חיבור מנהור מאובטח דרך Cloudflare...",
            ["TunnelDisabled"] = "המנהור כבוי (סמן 'הפעל מנהור' לפתיחת כתובת ציבורית)\u200F",
            ["OpenPublicUrl"] = "פתח כתובת ציבורית",
            ["CopyLink"] = "העתק קישור",
            ["QrScanTitle"] = "חיבור מהיר באמצעות QR",
            ["QrScanSubtitle"] = "סרוק במצלמת הנייד לחיבור ישיר",
            ["ConsoleLogTitle"] = "יומן פעילות ושרת (Activity Stream)",
            ["ClearLog"] = "נקה יומן",

            // טאב 2: שיתוף מאובטח וענן
            ["CreateShareTitle"] = "יצירת קישור שיתוף מאובטח עם PIN והגבלת זמן/הורדות",
            ["SelectFileOrFolder"] = "קובץ או תיקייה לשיתוף:",
            ["ChoosePlaceholder"] = "בחר קובץ או תיקייה לשיתוף...",
            ["BtnChooseFile"] = "בחר קובץ",
            ["BtnChooseFolder"] = "בחר תיקייה",
            ["ChannelTitle"] = "ערוץ שיתוף ואבטחה:",
            ["ChannelLan"] = "רשת מקומית LAN (Wi-Fi בלבד)",
            ["ChannelTunnel"] = "מנהור גלובלי (Cloudflare Tunnel)",
            ["ChannelGoogleDrive"] = "העברה ישירה ל-Google Drive",
            ["ChannelOneDrive"] = "העברה ישירה ל-Microsoft OneDrive",
            ["PinCodeLabel"] = "קוד PIN לאבטחה:",
            ["RandomizePin"] = "הגרל קוד",
            ["ExpirationLabel"] = "תוקף הקישור:",
            ["Exp1Hour"] = "שעה אחת",
            ["Exp6Hours"] = "6 שעות",
            ["Exp24Hours"] = "24 שעות",
            ["Exp3Days"] = "3 ימים",
            ["ExpUnlimited"] = "ללא הגבלת זמן",
            ["MaxDownloadsLabel"] = "הגבלת הורדות:",
            ["DownloadsUnlimited"] = "ללא הגבלה",
            ["Download1Time"] = "הורדה אחת (חד-פעמי)",
            ["Downloads3Times"] = "עד 3 הורדות",
            ["Downloads10Times"] = "עד 10 הורדות",
            ["BtnShareGoogleDrive"] = "שיתוף ב-Google Drive",
            ["BtnShareOneDrive"] = "שיתוף ב-OneDrive",
            ["BtnCreateShare"] = "צור קישור שיתוף",
            ["BtnCreateShareDirectCloudLoading"] = "מעלה לענן ישיר...",
            ["BtnCreateShareLoading"] = "יוצר ומצפין קישור שיתוף...",
            ["BtnCreateShareCloudProcessing"] = "מעבד ומעביר לענן...",
            ["DirectCloudSuccessHeader"] = "הקובץ הועלה בהצלחה לענן. הקישור הישיר מוכן לשיתוף.",
            ["SecureShareSuccessHeader"] = "קישור השיתוף המאובטח מוכן לפעולה.",
            ["DirectCloudPinNote"] = "קישור ציבורי ישיר (ללא צורך בקוד PIN)",
            ["BtnCopyShareUrl"] = "העתק קישור",
            ["BtnCopyFullMessage"] = "העתק הודעת שיתוף מלאה",
            ["BtnOpenInBrowser"] = "פתח בדפדפן",
            ["BtnShowInFolder"] = "הצג בסייר",
            ["ActiveSharesTitle"] = "קישורי שיתוף פעילים",
            ["BtnCancelLink"] = "בטל קישור",

            // טאב 3: הגדרות מתקדמות
            ["SettingsTitle"] = "הגדרות שרת ואבטחה מתקדמות",
            ["ServerPortLabel"] = "יציאת שרת (Server Port):",
            ["AccessModeLabel"] = "מצב גישה למחשב:",
            ["AccessModeFull"] = "גישה מלאה לכל הכוננים והמחשב (Full Computer)",
            ["AccessModeSingle"] = "הגבלה לתיקייה ייעודית בלבד (Single Folder)",
            ["SharedFolderLabel"] = "תיקיית שיתוף ייעודית (במצב Single Folder):",
            ["BtnBrowseFolder"] = "עיון תיקייה",
            ["ChkRecycleBin"] = "השתמש בסל המחזור של Windows (Recycle Bin) בעת מחיקת קבצים",
            ["ChkReadOnly"] = "מצב קריאה בלבד (Read-Only Mode) - חוסם מחיקה והעלאה",
            ["ChkHiddenFiles"] = "הצג קבצים ותיקיות מוסתרים",
            ["ChkHotkey"] = "קיצור מקשים גלובלי (Alt+S) - שיתוף מהיר מהלוח או צילום מסך",
            ["ChkContextMenu"] = "תפריט קליק ימני בסייר הקבצים של Windows (שתף באמצעות EasyShare)",
            ["ChkDropZone"] = "ווידג'ט צף שולחני (Floating Drop-Zone) לגרירה מהירה של קבצים",
            ["LanguageSettingLabel"] = "שפת ממשק (Interface Language):",
            ["ThemeSettingLabel"] = "ערכת נושא (Theme):",
            ["ThemeDark"] = "עיצוב כהה (Nordic Cyber-Teal)",
            ["ThemeLight"] = "עיצוב בהיר (Nordic Frost)",
            ["ThemeAuto"] = "אוטומטי (לפי מערכת ההפעלה)",
            ["ToggleTheme"] = "החלף ערכת נושא",
            ["ThemeTooltip"] = "החלף בין מצב כהה למצב בהיר",
            ["BandwidthLimitLabel"] = "הגבלת קצב תעבורה (Bandwidth Limit):",
            ["BandwidthUnlimited"] = "ללא הגבלה (מהירות מקסימלית)",
            ["Bandwidth5MB"] = "5 MB/s (מתאים ל-Wi-Fi ביתי)",
            ["Bandwidth10MB"] = "10 MB/s (מהיר ומאוזן)",
            ["Bandwidth25MB"] = "25 MB/s (מהירות גבוהה)",
            ["Bandwidth50MB"] = "50 MB/s (רשת עסקית)",
            ["CloseActionLabel"] = "פעולת סגירת חלון (X):",
            ["CloseActionTray"] = "מזעור למגש המערכת (השרת ממשיך לפעול ברקע)",
            ["CloseActionExit"] = "יציאה מלאה וסגירת התוכנה",
            ["BtnSaveSettings"] = "שמור הגדרות",

            // טאב 4: ניטור ובקרה חי
            ["BandwidthTitle"] = "תעבורת רשת בזמן אמת (Upload Bandwidth):",
            ["BandwidthPortDetails"] = "• פורט 2121 • mDNS פעיל (easyshare.local)",
            ["LiveConnectionsTitle"] = "חיבורים והורדות אקטיביים בזמן אמת",
            ["ColSource"] = "מקור",
            ["ColClientIp"] = "כתובת IP",
            ["ColTargetFile"] = "קובץ במשיכה",
            ["ColProgress"] = "התקדמות",
            ["ColActions"] = "פעולות",
            ["BtnDisconnect"] = "נתק",
            ["BtnBlockIp"] = "חסום IP",
            ["BtnUnblockIp"] = "הסר חסימה",
            ["BlockedIpsTitle"] = "רשימת כתובות IP חסומות (Blacklist)",
            ["ColBlockReason"] = "סיבת חסימה",
            ["DragDropOverlayText"] = "שחרר קבצים או תיקיות כאן לשיתוף מיידי",
            ["ClipboardSyncTitle"] = "לוח שיתוף מהיר (Universal Clipboard)",
            ["BtnSendClipboard"] = "שלח לוח למכשירים",
            ["BtnReceiveClipboard"] = "קבל לוח מהנייד",

            // סרגל תחתון
            ["FooterUserMode"] = "EasyShare PRO | פועל במצב משתמש (No Admin / UAC Required)",
            ["BtnBottomDropZone"] = "ווידג'ט צף",
            ["BtnBottomContextMenu"] = "תפריט סייר",
            ["BtnBottomMinimize"] = "מזער למגש",

            // ווידג'ט צף
            ["DropZoneText"] = "גרור לכאן",

            // הודעות מערכת, מגש והודעות קופצות
            ["TrayOpen"] = "פתח את EasyShare PRO",
            ["TrayDropZone"] = "ווידג'ט שולחני צף",
            ["TrayCopyLocal"] = "העתק קישור מקומי",
            ["TrayCopyTunnel"] = "העתק קישור מנהור",
            ["TrayExit"] = "יציאה מלאה",
            ["MsgCopied"] = "הועתק",
            ["MsgReadyMessageCopied"] = "הודעת השיתוף הועתקה ללוח בהצלחה!\nניתן להדביק אותה בהודעות, דוא\"ל או צ'אט.",
            ["MsgInvalidPort"] = "פורט שרת אינו חוקי (חייב להיות מספר בין 1 ל-65535).",
            ["MsgSettingsSaved"] = "ההגדרות נשמרו בהצלחה!\u200F",
            ["MsgPortChangedRestart"] = "פורט השרת שונה. האם ברצונך להפעיל מחדש את השרת כעת כדי להחיל את הפורט החדש?",
            ["MsgApplySettingsTitle"] = "החלת הגדרות",
            ["MsgErrorTitle"] = "שגיאה",
            ["MsgSuccessTitle"] = "הצלחה",
            ["MsgCloudShareComplete"] = "שיתוף ענן הושלם",
            ["MsgTunnelInactive"] = "לא ניתן ליצור שיתוף בערוץ Tunnel ללא מנהור פעיל. אנא המתן לחיבור המנהור או בחר ערוץ LAN / Cloud.",
            ["MsgTunnelInactiveTitle"] = "מנהור לא פעיל",
            ["ShareMessageTemplate"] = "שותף איתך קובץ באמצעות EasyShare PRO\nשם הקובץ: {0}\nקישור גישה: {1}\nאימות ואבטחה: {2}\n\n(נבנה על ידי בינארי חכם: https://ivrit.smartbinary.org)",
            ["ContextMenuTitle"] = "שתף באמצעות EasyShare PRO",

            // פריטי ערוצי שיתוף
            ["ChanLan"] = "רשת מקומית (LAN Wi-Fi)",
            ["ChanTunnel"] = "אינטרנט עולמי (Cloudflare HTTPS)",
            ["ChanDirectCloud"] = "ענן ישיר (קישור מיידי)",
            ["ChanCloud"] = "ענן מוצפן ב-PIN (Cloud Relay)",
            ["ChanGoogleDrive"] = "Google Drive (גוגל דרייב)",
            ["ChanOneDrive"] = "Microsoft OneDrive (וואן דרייב)",

            // פריטי זמן תפוגה
            ["ExpUnlimitedItem"] = "ללא הגבלת זמן",
            ["Exp15MinItem"] = "15 דקות",
            ["Exp1HourItem"] = "שעה אחת",
            ["Exp6HoursItem"] = "6 שעות",
            ["Exp24HoursItem"] = "24 שעות",

            // פריטי הגבלת הורדות
            ["MaxDl1Item"] = "הורדה אחת (חד-פעמי)",
            ["MaxDl3Item"] = "עד 3 הורדות",
            ["MaxDl10Item"] = "עד 10 הורדות",
            ["MaxDlUnlimitedItem"] = "ללא הגבלה",

            // תוויות והגדרות דומיין אישי
            ["LblCustomDomainUrl"] = "כתובת דומיין אישי:",
            ["LblTunnelToken"] = "טוקן (Tunnel Token):",
            ["TipCustomDomainUrl"] = "למשל: https://share.smartbinary.org או share.mydomain.com",
            ["TipTunnelToken"] = "הדבק כאן את מפתח ה-Token של ה-Tunnel מ-Cloudflare Zero Trust",
            ["TipGenPin"] = "הגרל קוד PIN חדש",
            ["TipShareGoogleDrive"] = "מעתיק את הקובץ ללוח (Ctrl+V) ופותח את Google Drive",
            ["TipShareOneDrive"] = "סנכרון מקומי אוטומטי ל-OneDrive",

            // כותרות דיאלוגים ובחירת קבצים
            ["SelectValidFileOrFolder"] = "אנא בחר קובץ או תיקייה חוקיים לשיתוף.",
            ["ChooseFileShareTitle"] = "בחר קובץ לשיתוף מאובטח",
            ["ChooseFolderShareTitle"] = "בחר תיקייה לשיתוף מאובטח",
            ["ChooseFileGdriveTitle"] = "בחר קובץ לשיתוף ב-Google Drive",
            ["ChooseFileOneDriveTitle"] = "בחר קובץ לשיתוף ב-OneDrive",
            ["ChooseDedicatedFolderTitle"] = "בחר תיקיית שיתוף ייעודית",
            ["FilterAllFiles"] = "כל הקבצים (*.*)|*.*",
            ["PromptStartTunnel"] = "מנהור Cloudflare אינו פועל כעת.\nהאם ברצונך להפעיל את המנהור כעת כדי לאפשר גישה ציבורית באינטרנט?",
            ["PromptStartTunnelTitle"] = "הפעלת מנהור",
            ["MissingTunnelTokenMsg"] = "אנא הזן טוקן של Cloudflare Tunnel (Tunnel Token) מלוח הניהול של Cloudflare Zero Trust כדי להשתמש בדומיין אישי.",
            ["MissingTunnelTokenTitle"] = "טוקן חסר",
            ["ErrorCreatingShare"] = "שגיאה ביצירת השיתוף המאובטח:\n{0}",
            ["ErrorCloudShare"] = "שגיאה בשיתוף דרך {0}:\n{1}",
            ["CloudShareErrorTitle"] = "שגיאת ענן",
            ["SecuredInAccount"] = "מאובטח בחשבון {0}",
            ["ContextMenuRemoved"] = "תפריט ההקשר בסייר הקבצים הוסר בהצלחה.",
            ["ContextMenuAdded"] = "תפריט ההקשר בסייר הקבצים נרשם בהצלחה!\nכעת ניתן ללחוץ מקש ימני על כל קובץ או תיקייה ולבחור ב-'שתף באמצעות EasyShare PRO'.",
            ["ExplorerMenuTitle"] = "סייר הקבצים",
            ["ActiveConnectionsCount"] = "({0} חיבורים)",
            ["BalloonTrayMinimized"] = "התוכנה ממוזערת למגש המערכת.",
            ["BalloonServerRunningBg"] = "השרת ממשיך לפעול ברקע במגש המערכת.",
            ["BalloonQuickShareCreated"] = "נוצר קישור שיתוף והועתק ללוח!\n{0}",
            ["BalloonAutoSync"] = "{0} הועלה והועתק ללוח!",
            ["BalloonAutoSyncTitle"] = "סנכרון תיקייה אוטומטי",
            ["BrowserOpenError"] = "שגיאה בפתיחת הדפדפן: {0}",
            ["MonitorSharedTitle"] = "קישורים וקבצים משותפים פעילים",
            ["MonitorSharedSafeNote"] = "🛡️ השמדת קישור מבטלת את הגישה מרחוק בלבד — הקובץ המקורי במחשב נשמר בבטחה ולא נמחק!",
            ["MonitorSharedCount"] = "({0} קישורים פעילים)",
            ["BtnRevokeShare"] = "השמד קישור 🗑️",
            ["BtnRevokeAllShares"] = "השמד את כל הקישורים",
            ["BtnCopyShare"] = "העתק 📋",
            ["BtnRefreshShares"] = "רענן 🔄",
            ["SearchSharesPlaceholder"] = "חפש לפי שם קובץ / מזהה...",
            ["ConfirmRevokeShare"] = "האם אתה בטוח שברצונך להשמיד את קישור השיתוף עבור '{0}'?\n\n🛡️ הקישור וטוקן הגישה יבוטלו מיידית. הקובץ המקורי במחשב יישאר שלם לחלוטין וללא שינוי.",
            ["ConfirmRevokeShareTitle"] = "השמדת קישור שיתוף",
            ["ConfirmRevokeAllShares"] = "האם אתה בטוח שברצונך להשמיד את כל קישורי השיתוף הפעילים?\n\n🛡️ כל הקישורים יבוטלו מיידית. כל הקבצים והתיקיות במחשב יישארו שלמים ובטוחים!",
            ["ConfirmRevokeAllSharesTitle"] = "השמדת כל קישורי השיתוף",
            ["ShareRevokedSuccess"] = "קישור השיתוף הושמד בהצלחה. הקובץ במחשב לא נפגע.",
            ["AllSharesRevokedSuccess"] = "כל קישורי השיתוף הושמדו בהצלחה. הקבצים במחשב נשמרו ללא שינוי.",
            ["ShareCopied"] = "קישור השיתוף הועתק ללוח!",
            ["BtnCheckUpdates"] = "בדוק עדכונים 🔄",
            ["UpdateChecking"] = "בודק האם קיימים עדכונים...",
            ["UpdateAvailableTitle"] = "נמצא עדכון חדש ל-EasyShare PRO!",
            ["UpdateAvailableMsg"] = "גרסה {0} זמינה להורדה (הגרסה הנוכחית שלך: {1}).\n\n{2}\nהאם ברצונך לפתוח את דף ההורדה כעת?",
            ["UpdateUpToDateTitle"] = "התוכנה מעודכנת",
            ["UpdateUpToDateMsg"] = "אתה משתמש בגרסה העדכנית ביותר של EasyShare PRO ({0}).",
            ["UpdateCheckFailedTitle"] = "שגיאה בבדיקת עדכונים",
            ["UpdateCheckFailedMsg"] = "לא ניתן היה להשלים את בדיקת העדכונים:\n{0}"
        },
        ["en"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // Titles and Main Status
            ["AppTitle"] = "EasyShare PRO | File Sharing, Cloud Drops & Streaming Suite",
            ["HeaderTitle"] = "EasyShare PRO",
            ["HeaderSubtitle"] = "Enterprise File Sharing & Cloud Streaming Suite",
            ["ServerRunning"] = "Active & Listening",
            ["ServerStopped"] = "Server Inactive",
            ["StopServer"] = "Stop Server",
            ["StartServer"] = "Start Server",
            ["LangHebrew"] = "עברית",
            ["LangEnglish"] = "English",

            // Tabs
            ["TabWebServer"] = "Network & Media",
            ["TabSecureShare"] = "Secure Share",
            ["TabSettings"] = "Settings",
            ["TabMonitor"] = "Live Telemetry",

            // Tab 1: Web & Media Server
            ["LocalNetworkTitle"] = "Local Network Address (Wi-Fi / LAN)",
            ["LocalNetworkSubtitle"] = "Connect from any phone or workstation on the local network",
            ["OpenInBrowser"] = "Open in Browser",
            ["CopyAddress"] = "Copy Address",
            ["TunnelTitle"] = "Global Cloudflare Tunnel (HTTPS)",
            ["TunnelSubtitle"] = "End-to-end encrypted sharing worldwide without port forwarding",
            ["EnableTunnel"] = "Enable Tunnel",
            ["TunnelStarting"] = "Establishing secure Cloudflare Tunnel connection...",
            ["TunnelDisabled"] = "Tunnel is disabled (check 'Enable Tunnel' to open a public URL)",
            ["OpenPublicUrl"] = "Open Public URL",
            ["CopyLink"] = "Copy Link",
            ["QrScanTitle"] = "Direct QR Connection",
            ["QrScanSubtitle"] = "Scan with your mobile camera to open instantly",
            ["ConsoleLogTitle"] = "Activity Stream & Server Logs",
            ["ClearLog"] = "Clear Log",

            // Tab 2: Secure & Cloud Share
            ["CreateShareTitle"] = "Create Secure Share Link with PIN, Expiration & Download Limits",
            ["SelectFileOrFolder"] = "File or Folder to Share:",
            ["ChoosePlaceholder"] = "Select file or folder to share...",
            ["BtnChooseFile"] = "Select File",
            ["BtnChooseFolder"] = "Select Folder",
            ["ChannelTitle"] = "Delivery Channel & Security:",
            ["ChannelLan"] = "Local LAN (Wi-Fi Only)",
            ["ChannelTunnel"] = "Global Tunnel (Cloudflare)",
            ["ChannelGoogleDrive"] = "Direct Transfer to Google Drive",
            ["ChannelOneDrive"] = "Direct Transfer to Microsoft OneDrive",
            ["PinCodeLabel"] = "Security PIN Code:",
            ["RandomizePin"] = "Generate PIN",
            ["ExpirationLabel"] = "Link Expiration:",
            ["Exp1Hour"] = "1 Hour",
            ["Exp6Hours"] = "6 Hours",
            ["Exp24Hours"] = "24 Hours",
            ["Exp3Days"] = "3 Days",
            ["ExpUnlimited"] = "No Expiration",
            ["MaxDownloadsLabel"] = "Max Downloads:",
            ["DownloadsUnlimited"] = "Unlimited",
            ["Download1Time"] = "1 Download (Single-use)",
            ["Downloads3Times"] = "Up to 3 downloads",
            ["Downloads10Times"] = "Up to 10 downloads",
            ["BtnShareGoogleDrive"] = "Share to Google Drive",
            ["BtnShareOneDrive"] = "Share to OneDrive",
            ["BtnCreateShare"] = "Create Share Link",
            ["BtnCreateShareDirectCloudLoading"] = "Uploading to Cloud...",
            ["BtnCreateShareLoading"] = "Creating & encrypting share...",
            ["BtnCreateShareCloudProcessing"] = "Transferring to Cloud...",
            ["DirectCloudSuccessHeader"] = "File successfully uploaded to Cloud. Direct link ready.",
            ["SecureShareSuccessHeader"] = "Secure share link is ready.",
            ["DirectCloudPinNote"] = "Direct public link (No PIN required)",
            ["BtnCopyShareUrl"] = "Copy Link",
            ["BtnCopyFullMessage"] = "Copy Full Share Message",
            ["BtnOpenInBrowser"] = "Open in Browser",
            ["BtnShowInFolder"] = "Show in Folder",
            ["ActiveSharesTitle"] = "Active Secure Shares",
            ["BtnCancelLink"] = "Cancel Link",

            // Tab 3: Advanced Settings
            ["SettingsTitle"] = "Advanced Server & Security Settings",
            ["ServerPortLabel"] = "Server Port:",
            ["AccessModeLabel"] = "Computer Access Mode:",
            ["AccessModeFull"] = "Full Computer Access (All Drives & Fast Folders)",
            ["AccessModeSingle"] = "Restricted to Dedicated Folder Only",
            ["SharedFolderLabel"] = "Dedicated Shared Folder (Single Folder Mode):",
            ["BtnBrowseFolder"] = "Browse Folder",
            ["ChkRecycleBin"] = "Use Windows Recycle Bin when deleting files",
            ["ChkReadOnly"] = "Read-Only Mode - Blocks file upload and deletion",
            ["ChkHiddenFiles"] = "Show hidden files and folders",
            ["ChkHotkey"] = "Global Hotkey (Alt+S) - Quick share file/image from clipboard or screenshot",
            ["ChkContextMenu"] = "Windows Explorer Right-Click Menu (Share with EasyShare)",
            ["ChkDropZone"] = "Floating Desktop Drop-Zone widget for instant file drag & drop",
            ["LanguageSettingLabel"] = "Interface Language:",
            ["ThemeSettingLabel"] = "Theme Mode:",
            ["ThemeDark"] = "Dark Theme (Nordic Cyber-Teal)",
            ["ThemeLight"] = "Light Theme (Nordic Frost)",
            ["ThemeAuto"] = "Automatic (Follow Windows)",
            ["ToggleTheme"] = "Toggle Theme",
            ["ThemeTooltip"] = "Switch between Dark and Light theme",
            ["BandwidthLimitLabel"] = "Bandwidth Limit:",
            ["BandwidthUnlimited"] = "Unlimited (Maximum Speed)",
            ["Bandwidth5MB"] = "5 MB/s (Home Wi-Fi)",
            ["Bandwidth10MB"] = "10 MB/s (Balanced)",
            ["Bandwidth25MB"] = "25 MB/s (High Speed)",
            ["Bandwidth50MB"] = "50 MB/s (Enterprise)",
            ["CloseActionLabel"] = "Window Close Action (X):",
            ["CloseActionTray"] = "Minimize to System Tray (Keep running)",
            ["CloseActionExit"] = "Exit Application Completely",
            ["BtnSaveSettings"] = "Save Settings",

            // Tab 4: Live Monitor & Control
            ["BandwidthTitle"] = "Real-Time Upload Bandwidth:",
            ["BandwidthPortDetails"] = "• Port 2121 • mDNS Active (easyshare.local)",
            ["LiveConnectionsTitle"] = "Live Active Connections & Transfers",
            ["ColSource"] = "Source",
            ["ColClientIp"] = "Client IP",
            ["ColTargetFile"] = "Downloading File",
            ["ColProgress"] = "Progress",
            ["ColActions"] = "Actions",
            ["BtnDisconnect"] = "Disconnect",
            ["BtnBlockIp"] = "Block IP",
            ["BtnUnblockIp"] = "Unblock",
            ["BlockedIpsTitle"] = "Blocked IP Addresses (Blacklist)",
            ["ColBlockReason"] = "Block Reason",
            ["DragDropOverlayText"] = "Drop files or folders here for instant sharing",
            ["ClipboardSyncTitle"] = "Universal Clipboard Sync",
            ["BtnSendClipboard"] = "Send Clipboard to Devices",
            ["BtnReceiveClipboard"] = "Get Clipboard from Mobile",

            // Bottom Bar
            ["FooterUserMode"] = "EasyShare PRO | Running in User Mode (No Admin / UAC Required)",
            ["BtnBottomDropZone"] = "Floating Widget",
            ["BtnBottomContextMenu"] = "Explorer Menu",
            ["BtnBottomMinimize"] = "Minimize to Tray",

            // Floating Drop Zone
            ["DropZoneText"] = "Drop Here",

            // System Messages, Tray & Dialogs
            ["TrayOpen"] = "Open EasyShare PRO",
            ["TrayDropZone"] = "Floating Drop-Zone",
            ["TrayCopyLocal"] = "Copy Local Link",
            ["TrayCopyTunnel"] = "Copy Tunnel Link",
            ["TrayExit"] = "Exit",
            ["MsgCopied"] = "Copied",
            ["MsgReadyMessageCopied"] = "Share message copied to clipboard successfully!\nYou can paste it in messages, email, or chat.",
            ["MsgInvalidPort"] = "Invalid server port (must be between 1 and 65535).",
            ["MsgSettingsSaved"] = "Settings saved successfully!",
            ["MsgPortChangedRestart"] = "Server port changed. Would you like to restart the server now to apply the new port?",
            ["MsgApplySettingsTitle"] = "Apply Settings",
            ["MsgErrorTitle"] = "Error",
            ["MsgSuccessTitle"] = "Success",
            ["MsgCloudShareComplete"] = "Cloud Share Completed",
            ["MsgTunnelInactive"] = "Cannot create a Tunnel share without an active tunnel. Please wait for tunnel or select LAN / Cloud.",
            ["MsgTunnelInactiveTitle"] = "Tunnel Inactive",
            ["ShareMessageTemplate"] = "A file was shared with you via EasyShare PRO\nFile name: {0}\nAccess URL: {1}\nSecurity Verification: {2}\n\n(Built by Smart Binary: https://smartbinary.org)",
            ["ContextMenuTitle"] = "Share with EasyShare PRO",

            // Share Channels
            ["ChanLan"] = "Local Network (LAN Wi-Fi)",
            ["ChanTunnel"] = "Global Internet (Cloudflare HTTPS)",
            ["ChanDirectCloud"] = "Instant Direct Cloud (Quick Link)",
            ["ChanCloud"] = "PIN Encrypted Cloud (Cloud Relay)",
            ["ChanGoogleDrive"] = "Google Drive",
            ["ChanOneDrive"] = "Microsoft OneDrive",

            // Expiration
            ["ExpUnlimitedItem"] = "No Expiration",
            ["Exp15MinItem"] = "15 Minutes",
            ["Exp1HourItem"] = "1 Hour",
            ["Exp6HoursItem"] = "6 Hours",
            ["Exp24HoursItem"] = "24 Hours",

            // Max Downloads
            ["MaxDl1Item"] = "1 Download (Single-use)",
            ["MaxDl3Item"] = "Up to 3 downloads",
            ["MaxDl10Item"] = "Up to 10 downloads",
            ["MaxDlUnlimitedItem"] = "Unlimited",

            // Custom Domain & Tunnel labels & tooltips
            ["LblCustomDomainUrl"] = "Custom Domain URL:",
            ["LblTunnelToken"] = "Tunnel Token:",
            ["TipCustomDomainUrl"] = "e.g.: https://share.smartbinary.org or share.mydomain.com",
            ["TipTunnelToken"] = "Paste your Cloudflare Zero Trust Tunnel token here",
            ["TipGenPin"] = "Generate new random PIN",
            ["TipShareGoogleDrive"] = "Copies file to clipboard (Ctrl+V) and opens Google Drive",
            ["TipShareOneDrive"] = "Automatic local sync to OneDrive",

            // Dialogs and file pickers
            ["SelectValidFileOrFolder"] = "Please select a valid file or folder to share.",
            ["ChooseFileShareTitle"] = "Select File for Secure Share",
            ["ChooseFolderShareTitle"] = "Select Folder for Secure Share",
            ["ChooseFileGdriveTitle"] = "Select File to Share in Google Drive",
            ["ChooseFileOneDriveTitle"] = "Select File to Share in OneDrive",
            ["ChooseDedicatedFolderTitle"] = "Select Dedicated Shared Folder",
            ["FilterAllFiles"] = "All Files (*.*)|*.*",
            ["PromptStartTunnel"] = "Cloudflare Tunnel is currently inactive.\nWould you like to start the tunnel now to enable global public access?",
            ["PromptStartTunnelTitle"] = "Start Tunnel",
            ["MissingTunnelTokenMsg"] = "Please enter a Cloudflare Tunnel Token from Cloudflare Zero Trust dashboard to use a custom domain.",
            ["MissingTunnelTokenTitle"] = "Missing Token",
            ["ErrorCreatingShare"] = "Error creating secure share:\n{0}",
            ["ErrorCloudShare"] = "Error sharing via {0}:\n{1}",
            ["CloudShareErrorTitle"] = "Cloud Error",
            ["SecuredInAccount"] = "Secured in {0} account",
            ["ContextMenuRemoved"] = "Explorer context menu removed successfully.",
            ["ContextMenuAdded"] = "Explorer context menu registered successfully!\nYou can now right-click any file or folder and choose 'Share with EasyShare PRO'.",
            ["ExplorerMenuTitle"] = "File Explorer",
            ["ActiveConnectionsCount"] = "({0} connections)",
            ["BalloonTrayMinimized"] = "App minimized to system tray.",
            ["BalloonServerRunningBg"] = "Server continues running in background.",
            ["BalloonQuickShareCreated"] = "Share link created and copied to clipboard!\n{0}",
            ["BalloonAutoSync"] = "{0} was uploaded and copied to clipboard!",
            ["BalloonAutoSyncTitle"] = "Automatic Folder Sync",
            ["BalloonCloudUploaded"] = "File {0} uploaded to cloud and link copied to clipboard!\n{1}",
            ["BrowserOpenError"] = "Error opening browser: {0}",
            ["MonitorSharedTitle"] = "Active Shared Links & Files",
            ["MonitorSharedSafeNote"] = "🛡️ Revoking a link destroys remote access only — the original file on your computer is safely preserved and never deleted!",
            ["MonitorSharedCount"] = "({0} active shares)",
            ["BtnRevokeShare"] = "Destroy Link 🗑️",
            ["BtnRevokeAllShares"] = "Destroy All Links",
            ["BtnCopyShare"] = "Copy 📋",
            ["BtnRefreshShares"] = "Refresh 🔄",
            ["SearchSharesPlaceholder"] = "Search by file name / token...",
            ["ConfirmRevokeShare"] = "Are you sure you want to destroy the share link for '{0}'?\n\n🛡️ The link and token will be revoked immediately. The original file on the computer remains 100% untouched.",
            ["ConfirmRevokeShareTitle"] = "Destroy Share Link",
            ["ConfirmRevokeAllShares"] = "Are you sure you want to destroy all active share links?\n\n🛡️ All links will be revoked immediately. All original files on the computer remain completely safe!",
            ["ConfirmRevokeAllSharesTitle"] = "Destroy All Share Links",
            ["ShareRevokedSuccess"] = "Share link destroyed successfully. The original file was not touched.",
            ["AllSharesRevokedSuccess"] = "All share links were destroyed successfully. Local files preserved.",
            ["ShareCopied"] = "Share link copied to clipboard!",
            ["BtnCheckUpdates"] = "Check Updates 🔄",
            ["UpdateChecking"] = "Checking for updates...",
            ["UpdateAvailableTitle"] = "New Update Available for EasyShare PRO!",
            ["UpdateAvailableMsg"] = "Version {0} is available for download (Current: {1}).\n\n{2}\nWould you like to open the download page now?",
            ["UpdateUpToDateTitle"] = "Up to Date",
            ["UpdateUpToDateMsg"] = "You are running the latest version of EasyShare PRO ({0}).",
            ["UpdateCheckFailedTitle"] = "Update Check Failed",
            ["UpdateCheckFailedMsg"] = "Unable to check for updates:\n{0}"
        }
    };

    private LocalizationService()
    {
    }

    /// <summary>
    /// מקבל תרגום לפי מפתח. אם המפתח לא קיים, מחזיר את המפתח עצמו.
    /// </summary>
    public string Get(string key)
    {
        if (_translations.TryGetValue(CurrentLanguage, out var dict) && dict.TryGetValue(key, out var val))
        {
            return val;
        }

        // ניסיון במילון ברירת מחדל
        if (_translations[LanguageHebrew].TryGetValue(key, out var defaultVal))
        {
            return defaultVal;
        }

        return key;
    }

    /// <summary>
    /// אינדקסר מקוצר לגישה נוחה: LocalizationService.Instance["Key"]
    /// </summary>
    public string this[string key] => Get(key);

    /// <summary>
    /// משנה את השפה הנוכחית ומפעיל את אירוע העדכון
    /// </summary>
    public void SetLanguage(string languageCode)
    {
        string normalized = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase) ? LanguageEnglish : LanguageHebrew;
        if (CurrentLanguage != normalized)
        {
            CurrentLanguage = normalized;
            OnLanguageChanged?.Invoke(CurrentLanguage);
        }
    }
}
