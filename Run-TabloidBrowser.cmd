@echo off
setlocal
cd /d "%~dp0"
"%USERPROFILE%\.dotnet\dotnet.exe" build
if errorlevel 1 pause
"bin\Debug\net8.0-windows\TabloidBrowser.exe"
if errorlevel 1 pause
