@echo off
setlocal
title Remote Support - Install

REM ---------------------------------------------------------------------------------------------
REM  Double-click to install the remote support client. THIS IS THE NORMAL INSTALL.
REM
REM  It installs the same software as Install-Client-Unattended.bat, agent service included -
REM  since 2026-09-20 every install does, because the tray app's "Allow unattended access" tick
REM  box has to have something to switch on. The only difference is that this one leaves the box
REM  UNTICKED, so somebody must accept each session until the person at the machine decides
REM  otherwise. The other file ticks it for them.
REM
REM  It is NOT "tray app only" and has not been since that date. That wording is worth keeping
REM  straight: it is what makes "I did the normal install so there is no agent" sound true.
REM
REM  It asks for administrator rights itself, so there is no need to open an admin terminal.
REM
REM  Each of these batch files re-launches ITSELF elevated and then runs the PowerShell script
REM  with its own fixed options. Nothing is forwarded through the elevation step on purpose:
REM  passing arguments through cmd -> PowerShell -> cmd is where this kind of script normally
REM  breaks on a path containing spaces. For anything unusual (-InstallDir, -NoAutoStart) call
REM  Install-Client.ps1 directly from an elevated PowerShell.
REM ---------------------------------------------------------------------------------------------

cd /d "%~dp0"

REM "net session" succeeds only for an administrator - the standard way to test elevation.
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

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Client.ps1"
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
