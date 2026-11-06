; Inno Setup script for the end-user (client) machine.
; Installs the attended tray app + the unattended agent service + the host helper.
; Compile after running build.ps1 (payload lives in ..\dist\client).

#define AppName "Remote Support"

; The publisher: us, identical in every build, and the name that goes on the code-signing
; certificate (docs\CODE-SIGNING.md). This is NOT the tenant - a reseller's own name reaches
; their customers through branding\brand.json and the PowerShell installer, never from here.
; See Directory.Build.props for the same distinction applied to the binaries.
#define CompanyName "The Remote Support Program"

; For the places a name must carry no spaces: the setup filename, and the Run value, which has
; to match the one Install-Client.ps1 writes and Uninstall-Client.ps1 removes.
#define ShortName "TheRemoteSupportProgram"

; build.ps1 passes /DAppVersion=<the build's version> (2026-10-16); this is only for compiling by hand.
#ifndef AppVersion
#define AppVersion "1.0.0"
#endif
#define ClientExe "RemoteSupport.Client.exe"
#define AgentExe "RemoteSupport.Agent.exe"

[Setup]
AppId={{B7F3A1C2-9E4D-4A11-8C55-RS-CLIENT-0001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#CompanyName}
DefaultDirName={autopf}\{#CompanyName}\{#AppName}
DefaultGroupName={#CompanyName}
DisableProgramGroupPage=yes
OutputDir=..\dist\installers
OutputBaseFilename={#ShortName}-Client-Setup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayName={#AppName}

[Files]
Source: "..\dist\client\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#ClientExe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#ClientExe}"; Tasks: desktopicon
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Tasks]
Name: desktopicon; Description: "Create a desktop icon"; GroupDescription: "Shortcuts"
Name: unattended; Description: "Enable unattended access (install background service)"; GroupDescription: "Support options"; Flags: checkedonce

[Registry]
; start the tray app for every user at logon
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#ShortName}"; ValueData: """{app}\{#ClientExe}"" --startup"; Flags: uninsdeletevalue

[Run]
; install + start the unattended agent service (optional task)
Filename: "sc.exe"; Parameters: "create RemoteSupportAgent binPath= ""{app}\{#AgentExe}"" start= auto DisplayName= ""{#AppName} Agent"""; Flags: runhidden; Tasks: unattended
Filename: "sc.exe"; Parameters: "description RemoteSupportAgent ""Provides unattended remote support for {#CompanyName}."""; Flags: runhidden; Tasks: unattended
Filename: "sc.exe"; Parameters: "failure RemoteSupportAgent reset= 60 actions= restart/5000/restart/5000/restart/5000"; Flags: runhidden; Tasks: unattended
Filename: "sc.exe"; Parameters: "start RemoteSupportAgent"; Flags: runhidden; Tasks: unattended
; launch the tray app now
Filename: "{app}\{#ClientExe}"; Parameters: "--startup"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop RemoteSupportAgent"; Flags: runhidden; RunOnceId: "StopAgent"
Filename: "sc.exe"; Parameters: "delete RemoteSupportAgent"; Flags: runhidden; RunOnceId: "DelAgent"
Filename: "taskkill.exe"; Parameters: "/f /im {#ClientExe}"; Flags: runhidden; RunOnceId: "KillClient"
Filename: "taskkill.exe"; Parameters: "/f /im RemoteSupport.Host.exe"; Flags: runhidden; RunOnceId: "KillHost"
