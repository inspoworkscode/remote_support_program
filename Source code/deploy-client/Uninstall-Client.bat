@echo off
setlocal
title Remote Support - Uninstall

REM ---------------------------------------------------------------------------------------------
REM  Double-click to remove the remote support client, the unattended agent and their
REM  settings from this computer. It asks for administrator rights itself.
REM ---------------------------------------------------------------------------------------------

cd /d "%~dp0"

net session >nul 2>&1
if not errorlevel 1 goto :run

echo Asking for administrator rights...
REM Elevate CMD running this file, rather than the SHELL running this file again.
REM
REM That distinction is the whole fix for the second "The publisher could not be verified"
REM warning the customer saw on 2026-09-20. A file downloaded from the internet carries a
REM mark-of-the-web, and it is ShellExecute - what Start-Process does by default, and what a
REM double-click does - that stops and asks about it. So the customer was asked twice: once for
REM their double-click, and once for our own re-launch of the very same file.
REM
REM cmd.exe is a Windows binary and carries no mark, and a batch file passed to "cmd /c" is READ
REM rather than shell-executed, so nothing asks about it. Everything below still runs exactly as
REM it did, in the elevated window, including the Done message and the pause.
REM
REM The FIRST warning is the shell's, on this file, before any of our code can run. Only a code
REM signature removes that (roadmap stage 5) - or unblocking the zip before extracting it, which
REM "INSTALL - read me.txt" now explains in the first thing it says.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath 'cmd.exe' -ArgumentList '/c','""""%~f0""""' -Verb RunAs"
if errorlevel 1 (
    echo.
    echo Could not get administrator rights. Right-click this file and choose
    echo "Run as administrator".
    echo.
    pause
)
exit /b

:run
if not exist "%~dp0Uninstall-Client.ps1" (
    echo Uninstall-Client.ps1 is missing. Keep it next to this file.
    echo.
    pause
    exit /b 1
)

echo This removes the remote support client, the unattended agent
echo and their settings from this computer.
echo.
choice /C YN /N /M "Remove it now? [Y,N] "
if errorlevel 2 (
    echo Cancelled - nothing was changed.
    echo.
    pause
    exit /b 0
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-Client.ps1"
set RS_EXIT=%errorlevel%

echo.
echo This window can be closed.
echo.
pause
exit /b %RS_EXIT%
