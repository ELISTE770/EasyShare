; Inno Setup Script for EasyShare PRO
; נבנה על ידי בינארי חכם (Smart Binary) - https://ivrit.smartbinary.org | https://smartbinary.org

#define MyAppName "EasyShare PRO"
#define MyAppVersion "2.5.0"
#define MyAppPublisher "בינארי חכם (Smart Binary)"
#define MyAppURL "https://ivrit.smartbinary.org"
#define MyAppExeName "EasyShare.exe"

[Setup]
AppId={{8B1B86CE-D28E-4545-B4C1-309191B98D55}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL=https://smartbinary.org
AppUpdatesURL=https://github.com/ELISTE770/EasyShare/releases
DefaultDirName={localappdata}\Programs\EasyShare
DisableProgramGroupPage=yes
; התקנה מקומית למשתמש (ללא צורך בהרשאות ניהול / UAC)
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=EasySharePRO_Setup_v{#MyAppVersion}
SetupIconFile=Assets\app.ico
UninstallDisplayIcon={app}\Assets\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "hebrew"; MessagesFile: "compiler:Languages\Hebrew.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "contextmenu"; Description: "שילוב בתפריט קליק-ימני בסייר הקבצים (שתף באמצעות EasyShare PRO)"; GroupDescription: "שילוב בסייר Windows:"

[Files]
; קובצי התוכנה העיקריים מתיקיית publish_inno
Source: "publish_inno\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; משאבים גרפיים ואייקונים
Source: "Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs
; cloudflared
Source: "cloudflared.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app.ico"; Comment: "EasyShare PRO - שיתוף קל"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app.ico"; Comment: "EasyShare PRO - שיתוף קל"; Tasks: desktopicon

[Registry]
; -------------------------------------------------------------
; תפריטי הקשר עבור כל הקבצים (*)
; -------------------------------------------------------------
Root: HKCU; Subkey: "Software\Classes\*\shell\EasySharePRO"; ValueType: string; ValueData: "שתף באמצעות EasyShare PRO"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\EasySharePRO"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\EasySharePRO"; ValueType: string; ValueName: "SubCommands"; ValueData: ""; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\*\shell\EasySharePRO\shell\01_DirectCloud"; ValueType: string; ValueData: "העלאה מהירה לענן (קישור ישיר)"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\EasySharePRO\shell\01_DirectCloud\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --quick-share ""DirectCloud"" ""%1"""; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\*\shell\EasySharePRO\shell\02_SecurePin"; ValueType: string; ValueData: "שיתוף מאובטח עם אימות PIN"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\EasySharePRO\shell\02_SecurePin\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --quick-share ""Tunnel"" ""%1"""; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\*\shell\EasySharePRO\shell\03_CloudDrive"; ValueType: string; ValueData: "סנכרון לתיקיית ענן (Google Drive / OneDrive)"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\EasySharePRO\shell\03_CloudDrive\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --quick-share ""LocalCloud"" ""%1"""; Tasks: contextmenu

; -------------------------------------------------------------
; תפריטי הקשר עבור תיקיות (Directory)
; -------------------------------------------------------------
Root: HKCU; Subkey: "Software\Classes\Directory\shell\EasySharePRO"; ValueType: string; ValueData: "שתף באמצעות EasyShare PRO"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\EasySharePRO"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\EasySharePRO"; ValueType: string; ValueName: "SubCommands"; ValueData: ""; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\Directory\shell\EasySharePRO\shell\01_DirectCloud"; ValueType: string; ValueData: "העלאה מהירה לענן (קישור ישיר)"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\EasySharePRO\shell\01_DirectCloud\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --quick-share ""DirectCloud"" ""%1"""; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\Directory\shell\EasySharePRO\shell\02_SecurePin"; ValueType: string; ValueData: "שיתוף מאובטח עם אימות PIN"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\EasySharePRO\shell\02_SecurePin\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --quick-share ""Tunnel"" ""%1"""; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\Directory\shell\EasySharePRO\shell\03_CloudDrive"; ValueType: string; ValueData: "סנכרון לתיקיית ענן (Google Drive / OneDrive)"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\EasySharePRO\shell\03_CloudDrive\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --quick-share ""LocalCloud"" ""%1"""; Tasks: contextmenu

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
