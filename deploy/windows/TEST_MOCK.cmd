@echo off
setlocal
cd /d "%~dp0"
rms_collector.exe --config config\collector.dojo.conf --mock --once --dry-run
set "RMS_RESULT=%ERRORLEVEL%"
pause
exit /b %RMS_RESULT%
