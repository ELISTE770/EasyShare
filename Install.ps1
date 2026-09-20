<#
.SYNOPSIS
    סקריפט התקנה אוטומטי עבור EasyShare PRO (שיתוף קל)
    נבנה על ידי בינארי חכם (Smart Binary) - https://ivrit.smartbinary.org
#>

param(
    [string]$Launch = "true",
    [string]$Quiet = "false"
)

$LaunchAfterInstall = ($Launch -eq "true" -or $Launch -eq "1" -or $Launch -eq "$true")
$IsQuiet = ($Quiet -eq "true" -or $Quiet -eq "1" -or $Quiet -eq "$true")

$ErrorActionPreference = "Stop"

if (-not $IsQuiet) {
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host "       התקנת EasyShare PRO - שיתוף קל למחשב              " -ForegroundColor White -BackgroundColor DarkBlue
    Write-Host "       נבנה על ידי בינארי חכם (Smart Binary)            " -ForegroundColor Gray
    Write-Host "==========================================================" -ForegroundColor Cyan
}

# 1. סגירת מופעים פועלים
if (-not $IsQuiet) { Write-Host "[1/7] בודק מופעים פועלים..." -ForegroundColor Yellow }
Get-Process -Name "EasyShare" -ErrorAction SilentlyContinue | ForEach-Object {
    if (-not $IsQuiet) { Write-Host "   סוגר מופע קיים (PID: $($_.Id))..." -ForegroundColor DarkYellow }
    Stop-Process -Id $_.Id -Force
}
Start-Sleep -Milliseconds 500

# 2. נתיבים
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ScriptDir) { $ScriptDir = (Get-Location).Path }

$ProjectDir = $ScriptDir
$InstallDir = [System.IO.Path]::Combine($env:LOCALAPPDATA, "Programs", "EasyShare")
$AssetsSourceDir = [System.IO.Path]::Combine($ProjectDir, "Assets")
$TargetExe = [System.IO.Path]::Combine($InstallDir, "EasyShare.exe")
$TargetIcon = [System.IO.Path]::Combine($InstallDir, "Assets", "app.ico")

if (-not $IsQuiet) { Write-Host "[2/7] נתיב התקנה: $InstallDir" -ForegroundColor Yellow }

# 3. הידור והפצה
if (-not $IsQuiet) { Write-Host "[3/7] מהדר ומפיץ קבצי Release..." -ForegroundColor Yellow }
& dotnet publish "$ProjectDir\EasyShare.csproj" -c Release -r win-x64 --self-contained false -o "$InstallDir"
if ($LASTEXITCODE -ne 0) {
    Write-Error "שגיאה בהידור והפצת הפרויקט. קוד יציאה: $LASTEXITCODE"
    exit 1
}

# 4. העתקת קבצי משאבים ועזר
if (-not $IsQuiet) { Write-Host "[4/7] מעתיק משאבי מערכת ואייקונים..." -ForegroundColor Yellow }
$installAssetsDir = [System.IO.Path]::Combine($InstallDir, "Assets")
if (-not (Test-Path $installAssetsDir)) {
    New-Item -ItemType Directory -Path $installAssetsDir -Force | Out-Null
}

$icoSource = [System.IO.Path]::Combine($AssetsSourceDir, "app.ico")
if (Test-Path $icoSource) {
    Copy-Item -Path $icoSource -Destination $TargetIcon -Force
}

$pngSource = [System.IO.Path]::Combine($AssetsSourceDir, "app_icon.png")
if (Test-Path $pngSource) {
    Copy-Item -Path $pngSource -Destination (Join-Path $installAssetsDir "app_icon.png") -Force
}

$cloudflaredSource = [System.IO.Path]::Combine($ProjectDir, "cloudflared.exe")
if (Test-Path $cloudflaredSource) {
    $cloudflaredTarget = [System.IO.Path]::Combine($InstallDir, "cloudflared.exe")
    if (-not (Test-Path $cloudflaredTarget)) {
        Copy-Item -Path $cloudflaredSource -Destination $cloudflaredTarget -Force
    }
}

$appDataCloudflared = [System.IO.Path]::Combine($env:LOCALAPPDATA, "EasyShare")
if (-not (Test-Path $appDataCloudflared)) {
    New-Item -ItemType Directory -Path $appDataCloudflared -Force | Out-Null
}
if (Test-Path $cloudflaredSource) {
    Copy-Item -Path $cloudflaredSource -Destination (Join-Path $appDataCloudflared "cloudflared.exe") -Force
}

# העתקת סקריפט הסרה
$uninstallSource = [System.IO.Path]::Combine($ProjectDir, "Uninstall.ps1")
$uninstallPs1Path = [System.IO.Path]::Combine($InstallDir, "Uninstall.ps1")
if (Test-Path $uninstallSource) {
    Copy-Item -Path $uninstallSource -Destination $uninstallPs1Path -Force
}

# 5. יצירת קיצורי דרך
if (-not $IsQuiet) { Write-Host "[5/7] יוצר קיצורי דרך..." -ForegroundColor Yellow }
$wsh = New-Object -ComObject WScript.Shell

$desktopPath = [Environment]::GetFolderPath('Desktop')
$desktopShortcutPath = [System.IO.Path]::Combine($desktopPath, "EasyShare PRO.lnk")
$shortcut = $wsh.CreateShortcut($desktopShortcutPath)
$shortcut.TargetPath = $TargetExe
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = "EasyShare PRO - שיתוף קל"
if (Test-Path $TargetIcon) {
    $shortcut.IconLocation = "$TargetIcon,0"
} else {
    $shortcut.IconLocation = "$TargetExe,0"
}
$shortcut.Save()

$programsPath = [Environment]::GetFolderPath('Programs')
$startShortcutPath = [System.IO.Path]::Combine($programsPath, "EasyShare PRO.lnk")
$shortcutStart = $wsh.CreateShortcut($startShortcutPath)
$shortcutStart.TargetPath = $TargetExe
$shortcutStart.WorkingDirectory = $InstallDir
$shortcutStart.Description = "EasyShare PRO - שיתוף קל"
if (Test-Path $TargetIcon) {
    $shortcutStart.IconLocation = "$TargetIcon,0"
} else {
    $shortcutStart.IconLocation = "$TargetExe,0"
}
$shortcutStart.Save()

$appStartFolder = [System.IO.Path]::Combine($programsPath, "EasyShare PRO")
if (-not (Test-Path $appStartFolder)) {
    New-Item -ItemType Directory -Path $appStartFolder -Force | Out-Null
}
$folderShortcutPath = [System.IO.Path]::Combine($appStartFolder, "EasyShare PRO.lnk")
$shortcutFolder = $wsh.CreateShortcut($folderShortcutPath)
$shortcutFolder.TargetPath = $TargetExe
$shortcutFolder.WorkingDirectory = $InstallDir
if (Test-Path $TargetIcon) {
    $shortcutFolder.IconLocation = "$TargetIcon,0"
} else {
    $shortcutFolder.IconLocation = "$TargetExe,0"
}
$shortcutFolder.Save()

$uninstallShortcutPath = [System.IO.Path]::Combine($appStartFolder, "Uninstall EasyShare.lnk")
$shortcutUninst = $wsh.CreateShortcut($uninstallShortcutPath)
$shortcutUninst.TargetPath = "powershell.exe"
$shortcutUninst.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$uninstallPs1Path`""
$shortcutUninst.WorkingDirectory = $InstallDir
$shortcutUninst.Save()

# 6. רישום תפריט קליק-ימני בסייר הקבצים
if (-not $IsQuiet) { Write-Host "[6/7] רושם תפריטי מערכת..." -ForegroundColor Yellow }
$menuRootName = "EasySharePRO"
$menuTitle = "שתף באמצעות EasyShare PRO"

function Register-ExplorerMenu([string]$classPath) {
    $baseKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Software\Classes\$classPath\shell\$menuRootName")
    if ($baseKey) {
        $baseKey.SetValue("", $menuTitle)
        $baseKey.SetValue("Icon", "`"$TargetExe`",0")
        $baseKey.SetValue("SubCommands", "")

        $shellKey = $baseKey.CreateSubKey("shell")
        if ($shellKey) {
            $cmd1 = $shellKey.CreateSubKey("01_DirectCloud")
            $cmd1.SetValue("", "העלאה מהירה לענן (קישור ישיר)")
            $cmd1Exec = $cmd1.CreateSubKey("command")
            $cmd1Exec.SetValue("", "`"$TargetExe`" --quick-share `"DirectCloud`" `"%1`"")
            $cmd1Exec.Close()
            $cmd1.Close()

            $cmd2 = $shellKey.CreateSubKey("02_SecurePin")
            $cmd2.SetValue("", "שיתוף מאובטח עם אימות PIN")
            $cmd2Exec = $cmd2.CreateSubKey("command")
            $cmd2Exec.SetValue("", "`"$TargetExe`" --quick-share `"Tunnel`" `"%1`"")
            $cmd2Exec.Close()
            $cmd2.Close()

            $cmd3 = $shellKey.CreateSubKey("03_CloudDrive")
            $cmd3.SetValue("", "סנכרון לתיקיית ענן (Google Drive / OneDrive)")
            $cmd3Exec = $cmd3.CreateSubKey("command")
            $cmd3Exec.SetValue("", "`"$TargetExe`" --quick-share `"LocalCloud`" `"%1`"")
            $cmd3Exec.Close()
            $cmd3.Close()

            $shellKey.Close()
        }
        $baseKey.Close()
    }
}

try {
    Register-ExplorerMenu "*"
    Register-ExplorerMenu "Directory"
} catch {
    Write-Warning "לא ניתן היה לרשום תפריט הקשר: $($_.Exception.Message)"
}

# 7. רישום תוכניות ותכונות
try {
    $uninstKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Software\Microsoft\Windows\CurrentVersion\Uninstall\EasyShare")
    if ($uninstKey) {
        $uninstKey.SetValue("DisplayName", "EasyShare PRO - שיתוף קל")
        $uninstKey.SetValue("DisplayVersion", "1.0.0")
        $uninstKey.SetValue("Publisher", "בינארי חכם (Smart Binary)")
        $uninstKey.SetValue("InstallLocation", $InstallDir)
        $uninstKey.SetValue("DisplayIcon", "`"$TargetIcon`",0")
        $uninstKey.SetValue("UninstallString", "powershell.exe -ExecutionPolicy Bypass -File `"$uninstallPs1Path`"")
        $uninstKey.SetValue("QuietUninstallString", "powershell.exe -ExecutionPolicy Bypass -File `"$uninstallPs1Path`" -Quiet")
        $uninstKey.SetValue("HelpLink", "https://ivrit.smartbinary.org")
        $uninstKey.SetValue("URLInfoAbout", "https://smartbinary.org")
        $uninstKey.SetValue("NoModify", 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $uninstKey.SetValue("NoRepair", 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $uninstKey.Close()
    }
} catch {
    Write-Warning "לא ניתן היה לרשום תוכנית מותקנת: $($_.Exception.Message)"
}

if (-not $IsQuiet) {
    Write-Host "[7/7] ההתקנה הושלמה בהצלחה!" -ForegroundColor Green
    Write-Host "----------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host " קבצי התוכנה הותקנו ב: $InstallDir" -ForegroundColor White
    Write-Host " נוצר קיצור דרך בשולחן העבודה: EasyShare - שיתוף קל" -ForegroundColor White
    Write-Host " נוצר קיצור דרך בתפריט התחלה" -ForegroundColor White
    Write-Host " נוספו תפריטי קליק-ימני בסייר הקבצים" -ForegroundColor White
    Write-Host " התוכנה רשומה בהגדרות Windows (תוכניות ותכונות)" -ForegroundColor White
    Write-Host "----------------------------------------------------------" -ForegroundColor DarkGray
}

if ($LaunchAfterInstall) {
    if (-not $IsQuiet) { Write-Host "מפעיל את EasyShare כעת..." -ForegroundColor Cyan }
    Start-Process -FilePath "explorer.exe" -ArgumentList "`"$TargetExe`""
}
