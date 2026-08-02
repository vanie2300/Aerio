@echo off
setlocal
cd /d "%~dp0"
"%USERPROFILE%\.dotnet\dotnet.exe" build
if errorlevel 1 pause
"bin\Debug\net8.0-windows10.0.19041.0\TabloidBrowser.exe"
if errorlevel 1 pause
