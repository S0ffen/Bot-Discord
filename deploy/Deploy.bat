@echo off
setlocal EnableExtensions

cd /d "%~dp0.."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0internal\Deploy.ps1"
set "DEPLOY_EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%DEPLOY_EXIT_CODE%"=="0" (
    echo Deploy zakonczyl sie bledem. Kod: %DEPLOY_EXIT_CODE%
) else (
    echo Deploy zakonczony poprawnie.
)
pause
exit /b %DEPLOY_EXIT_CODE%
