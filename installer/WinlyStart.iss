; Inno Setup script for the Winly Start installer.
; Built by CI (.github/workflows/build.yml) after the self-contained publish:
;   ISCC.exe /DSourceExe=..\out\sc\WinlyStart.exe installer\WinlyStart.iss
;
; Deliberately a PER-USER install (PrivilegesRequired=lowest): Winly Start runs as a normal user,
; writes only under HKCU and %LocalAppData%, and never needs administrator rights.

#define AppName        "Winly Start"
#define AppExeName     "WinlyStart.exe"
#define AppPublisher   "Winly Start contributors"
#define AppURL         "https://github.com/Mahi-BD/WinlyStart"

#ifndef AppVersion
  #define AppVersion "1.5.4"
#endif
#ifndef SourceExe
  #define SourceExe "..\out\sc\WinlyStart.exe"
#endif

[Setup]
AppId={{6C1B5A2E-6A3B-4C2B-9C4B-3E5A1D2F7B10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
; The setup .exe is itself a signed PE file, so it has to carry its own version resources -
; both the Microsoft Store and the SignPath Foundation require a product name and version.
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoCopyright=© 2026 {#AppPublisher}. MIT License.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=WinlyStart-Setup-{#AppVersion}
SetupIconFile=..\src\WinlyStart\Assets\WinlyStart.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; close a running copy (and restart nothing) so the exe can be replaced
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup";     Description: "Start {#AppName} when I sign in"; GroupDescription: "Startup:"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "{#AppExeName}"; Flags: ignoreversion
Source: "..\LICENSE";   DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.md";   Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; the same per-user Run value the app's own "Start with Windows" setting uses
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "WinlyStart"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; the app's own data is left alone on uninstall; only the install folder goes
Type: dirifempty; Name: "{app}"
