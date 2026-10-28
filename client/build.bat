@echo off
rem Сборка программы для рабочих ПК встроенным в Windows компилятором C# (ничего ставить не нужно).
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /codepage:65001 /out:OfficeTimeClient.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Security.dll OfficeTimeClient.cs
if errorlevel 1 (echo Ошибка сборки & pause & exit /b 1)
echo Готово: OfficeTimeClient.exe
