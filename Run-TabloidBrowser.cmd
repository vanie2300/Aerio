@echo off
setlocal
cd /d "%~dp0"
"%USERPROFILE%\.dotnet\dotnet.exe" build
if errorlevel 1 (
    pause
    exit /b 1
)
"bin\Debug\net10.0-windows10.0.19041.0\Aerio.exe"
if errorlevel 1 pause
