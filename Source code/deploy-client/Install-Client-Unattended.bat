@echo off
setlocal
title Remote Support - Install (unattended access)

REM ---------------------------------------------------------------------------------------------
REM  Double-click to install the client with unattended access ALREADY SWITCHED ON, so technicians
REM  can connect with nobody at the keyboard without anybody having to tick a box first.
REM
REM  Install-Client.bat installs exactly the same software, agent service included. The only
REM  difference is this one also ticks "Allow unattended access" on the customer's behalf - it
REM  leaves a request in HKLM which the tray app turns into one permission change the first time
REM  it connects. The permission itself lives on the SERVER either way (HANDOVER 31).
REM
REM  This is a separate file rather than a flag, for two reasons: enabling unattended access is a
REM  decision the customer should make on purpose, and it means nothing has to be forwarded
REM  through the elevation step (see the note in Install-Client.bat).
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
    echo "Run as administrator", or see "INSTALL - read me.txt".
    echo.
    pause
)
exit /b

:run
if not exist "%~dp0Install-Client.ps1" (
    echo Install-Client.ps1 is missing. Copy the whole unzipped folder across, not just this file.
    echo.
    pause
    exit /b 1
)

echo Installing with unattended access enabled.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Client.ps1" -Unattended
set RS_EXIT=%errorlevel%

echo.
if "%RS_EXIT%"=="0" (
    echo Done. This window can be closed.
) else (
    echo The installer reported a problem ^(exit code %RS_EXIT%^).
    echo See "INSTALL - read me.txt" for the usual causes.
)
echo.
pause
exit /b %RS_EXIT%
