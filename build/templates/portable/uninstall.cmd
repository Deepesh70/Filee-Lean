@echo off
title Filee - Remove Shell Integration
cd /d "%~dp0"
echo Unregistering Filee context menu and user PATH...
if exist filee-cli.exe (
    filee-cli.exe uninstall
) else (
    echo Error: filee-cli.exe not found in current folder.
)
echo.
pause
