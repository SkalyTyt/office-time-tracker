@echo off
chcp 65001 >nul
rem Установка программы «Учёт времени»: запускать один раз под Windows-аккаунтом пользователя этого компьютера.
cd /d "%~dp0"
if not exist OfficeTimeClient.exe (
  echo Не найден файл OfficeTimeClient.exe рядом с этим скриптом. Запустите build.bat.
  pause
  exit /b 1
)
set /p ISADMIN=Это компьютер администратора, где работает сайт? (y/n):
if /i "%ISADMIN%"=="y" goto admin

set /p SERVER=Адрес сервера (например http://192.168.1.10:3000):
> config.ini echo server=%SERVER%
>> config.ini echo # unlock_pin=1234
>> config.ini echo # block_keys=0
goto reg

:admin
> config.ini echo server=http://localhost:3000
>> config.ini echo start_server=1

:reg
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v OfficeTimeClient /t REG_SZ /d "\"%~dp0OfficeTimeClient.exe\"" /f >nul
echo Готово. Программа будет запускаться при входе в Windows.
start "" "%~dp0OfficeTimeClient.exe"
pause
