@echo off
setlocal
cd /d "%~dp0.."
dotnet publish RmsCollector.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
exit /b %ERRORLEVEL%
