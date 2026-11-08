@echo off
setlocal
cd /d "%~dp0"
rms_collector.exe --check-sdk
set "RMS_RESULT=%ERRORLEVEL%"
pause
exit /b %RMS_RESULT%
