@echo off
setlocal

set "APP_EXE=%~dp0KongfzBookMonitor.Windows\bin\Release\net5.0-windows\KongfzBookMonitor.Windows.exe"

if not exist "%APP_EXE%" (
    echo Application file not found:
    echo %APP_EXE%
    echo Build the Release version before starting the application.
    pause
    exit /b 1
)

start "" "%APP_EXE%"
exit /b 0
