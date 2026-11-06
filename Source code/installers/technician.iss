; Inno Setup script for a technician workstation.
; Compile after running build.ps1 (payload lives in ..\dist\technician).

#define AppName "Remote Support Technician"

; The publisher - see the same note in client.iss and in Directory.Build.props. Fixed, ours,
; and the name that goes on the code-signing certificate.
#define CompanyName "The Remote Support Program"
#define ShortName "TheRemoteSupportProgram"

; build.ps1 passes /DAppVersion=<the build's version> (2026-10-16); this is only for compiling by hand.
#ifndef AppVersion
#define AppVersion "1.0.0"
#endif
#define TechExe "RemoteSupport.Technician.exe"

[Setup]
AppId={{B7F3A1C2-9E4D-4A11-8C55-RS-TECH-0002}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#CompanyName}
DefaultDirName={autopf}\{#CompanyName}\{#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist\installers
OutputBaseFilename={#ShortName}-Technician-Setup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
WizardStyle=modern

[Files]
Source: "..\dist\technician\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#TechExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#TechExe}"; Tasks: desktopicon
Name: "{group}\Uninstall"; Filename: "{uninstallexe}"

[Tasks]
Name: desktopicon; Description: "Create a desktop icon"; GroupDescription: "Shortcuts"

[Run]
Filename: "{app}\{#TechExe}"; Description: "Launch the Technician Console"; Flags: nowait postinstall skipifsilent
