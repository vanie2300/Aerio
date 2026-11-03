@echo off
setlocal
cd /d "%~dp0"
set "PREVIEW_OUTPUT=bin\Preview\%RANDOM%_%RANDOM%\net10.0-windows10.0.19041.0"
"%USERPROFILE%\.dotnet\dotnet.exe" build -p:OutputPath="%PREVIEW_OUTPUT%\"
if errorlevel 1 (
    pause
    exit /b 1
)
"%PREVIEW_OUTPUT%\Aerio.exe"
if errorlevel 1 pause
