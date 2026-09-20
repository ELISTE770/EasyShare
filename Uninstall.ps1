<#
    סקריפט הסרת התקנה עבור EasyShare PRO
    נבנה על ידי בינארי חכם - https://ivrit.smartbinary.org
#>
param([switch]$Quiet = $false)

if (-not $Quiet) {
    Write-Host "מסיר את EasyShare PRO מהמחשב..." -ForegroundColor Yellow
}

# 1. סגירת תוכנה פועלת
Get-Process -Name "EasyShare" -ErrorAction SilentlyContinue | Stop-Process -Force

# 2. מחיקת קיצורי דרך
$desktopPath = [Environment]::GetFolderPath('Desktop')
foreach ($name in @("EasyShare PRO.lnk", "EasyShare.lnk", "שיתוף קל - EasyShare.lnk")) {
    $p = Join-Path $desktopPath $name
    if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
}

$programsPath = [Environment]::GetFolderPath('Programs')
foreach ($name in @("EasyShare PRO.lnk", "EasyShare.lnk", "שיתוף קל - EasyShare.lnk")) {
    $p = Join-Path $programsPath $name
    if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
}

foreach ($folderName in @("EasyShare PRO", "EasyShare", "שיתוף קל")) {
    $f = Join-Path $programsPath $folderName
    if (Test-Path $f) { Remove-Item $f -Recurse -Force -ErrorAction SilentlyContinue }
}

# 3. הסרת תפריטי לחיצה ימנית (Context Menu)
try {
    Remove-Item -Path "HKCU:\Software\Classes\*\shell\EasySharePRO" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "HKCU:\Software\Classes\Directory\shell\EasySharePRO" -Recurse -Force -ErrorAction SilentlyContinue
} catch {}

# 4. הסרה מהרשימה בלוח הבקרה / הגדרות Windows
try {
    Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\EasyShare" -Recurse -Force -ErrorAction SilentlyContinue
} catch {}

if (-not $Quiet) {
    Write-Host "EasyShare PRO הוסר בהצלחה מהמחשב." -ForegroundColor Green
}
