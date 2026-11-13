@echo off
title Filee - Register Shell Integration
cd /d "%~dp0"
echo Registering Filee context menu and user PATH...
if exist filee-cli.exe (
    filee-cli.exe install
) else (
    echo Error: filee-cli.exe not found in current folder.
)
echo.
pause
