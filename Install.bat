@echo off
chcp 65001 >nul
title התקנת EasyShare PRO - שיתוף קל
echo.
echo ==========================================================
echo        התקנת EasyShare PRO - שיתוף קל
echo        נבנה על ידי בינארי חכם (Smart Binary)
echo ==========================================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] אירעה שגיאה במהלך ההתקנה.
    pause
)
