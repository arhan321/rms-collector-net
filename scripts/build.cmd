@echo off
setlocal
cd /d "%~dp0.."
dotnet build RmsCollector.csproj -c Release
exit /b %ERRORLEVEL%
