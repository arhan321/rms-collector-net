@echo off
setlocal
cd /d "%~dp0.."
dotnet run --project tests\RmsCollector.Tests.csproj -c Release
exit /b %ERRORLEVEL%
