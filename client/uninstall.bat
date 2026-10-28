@echo off
chcp 65001 >nul
rem Убирает автозапуск программы (под тем же Windows-аккаунтом, где ставили).
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v OfficeTimeClient /f >nul 2>&1
taskkill /im OfficeTimeClient.exe /f >nul 2>&1
rmdir /s /q "%APPDATA%\OfficeTime" >nul 2>&1
echo Автозапуск убран, запомненный вход удалён.
pause
