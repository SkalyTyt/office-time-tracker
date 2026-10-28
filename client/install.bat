@echo off
chcp 65001 >nul
rem Установка программы на рабочий компьютер сотрудника: запускать один раз под его Windows-аккаунтом.
cd /d "%~dp0"
if not exist OfficeTimeClient.exe (
  echo Не найден файл OfficeTimeClient.exe рядом с этим скриптом. Запустите build.bat.
  pause
  exit /b 1
)
set /p SERVER=Адрес сервера (например http://192.168.1.10:3000):
> config.ini echo server=%SERVER%
>> config.ini echo # unlock_pin=1234
>> config.ini echo # block_keys=0
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v OfficeTimeClient /t REG_SZ /d "\"%~dp0OfficeTimeClient.exe\"" /f >nul
echo Готово. Программа будет запускаться при входе в Windows.
start "" "%~dp0OfficeTimeClient.exe"
pause
