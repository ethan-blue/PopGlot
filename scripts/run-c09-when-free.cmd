@echo off
setlocal
echo ======================================================================
echo  PopGlot C09 Startup Performance Measurement ^& UI07 Screenshot Runner
echo ======================================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-c09-when-free.ps1" %*
exit /b %ERRORLEVEL%
