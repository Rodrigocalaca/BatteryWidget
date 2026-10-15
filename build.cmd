@echo off
rem Compila o monitor. Usa o csc do .NET Framework, que ja vem no Windows -
rem nao precisa de SDK, Visual Studio nem restore de pacotes.
setlocal

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Nao achei o compilador em %CSC%
  exit /b 1
)

if not exist "%~dp0bin" mkdir "%~dp0bin"

"%CSC%" -nologo -target:winexe -optimize+ -out:"%~dp0bin\DareuBattery.exe" ^
  -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Core.dll ^
  "%~dp0src\DareuHid.cs" "%~dp0src\GamakayHid.cs" "%~dp0src\Notifier.cs" ^
  "%~dp0src\Widget.cs" "%~dp0src\Program.cs"

if errorlevel 1 (
  echo.
  echo Falha na compilacao.
  exit /b 1
)

echo.
echo OK: %~dp0bin\DareuBattery.exe
endlocal
