@echo off
chcp 65001 >nul
rem Первый запуск: программа сама спросит, что это за компьютер (администратора или сотрудника),
rem и всё настроит: адрес сервера, автозапуск, доступ в брандмауэре, запуск сайта.
cd /d "%~dp0"
if not exist OfficeTimeClient.exe (
  echo Не найден файл OfficeTimeClient.exe. Сначала запустите build.bat.
  pause
  exit /b 1
)
start "" "%~dp0OfficeTimeClient.exe"
