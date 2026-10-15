@echo off
rem Compila, para a instancia que estiver rodando, instala em %LOCALAPPDATA%\DareuBattery,
rem registra a tarefa de logon e sobe o monitor de novo.
setlocal

call "%~dp0build.cmd"
if errorlevel 1 exit /b 1

echo Parando instancia em execucao...
taskkill /f /im DareuBattery.exe >nul 2>&1
timeout /t 2 /nobreak >nul

"%~dp0bin\DareuBattery.exe" --install
if errorlevel 1 exit /b 1

start "" "%LOCALAPPDATA%\DareuBattery\DareuBattery.exe"
echo.
echo Monitor instalado e rodando.
endlocal
