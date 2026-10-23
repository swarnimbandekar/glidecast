; Glidecast installer (Inno Setup 6). Built by build.ps1:
;   ISCC.exe /DAppVersion=1.0.0 /DPublishDir=..\artifacts\publish installer\glidecast.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "Glidecast"
#define AppExe "Glidecast.exe"
#define AppUrl "https://github.com/swarnimbandekar/glidecast"

[Setup]
AppId={{C4F27816-DEEA-4A94-9143-656EAC11A686}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Glidecast contributors
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (c) 2026 Swarnim Bandekar and Glidecast contributors
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no admin prompt); users can choose all-users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=..\LICENSE
SetupIconFile=..\assets\glidecast.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard-side.bmp,wizard-side@2x.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small@2x.bmp
OutputDir=..\artifacts
OutputBaseFilename=Glidecast-Setup-{#AppVersion}-x64
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
#ifndef BundledFFmpeg
Name: "ffmpeg"; Description: "Install FFmpeg with winget (required to record and export; skipped if already installed)"; GroupDescription: "Components:"; Check: not FFmpegInstalled
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
#ifndef BundledFFmpeg
Filename: "{localappdata}\Microsoft\WindowsApps\winget.exe"; \
  Parameters: "install --id Gyan.FFmpeg.Essentials -e --accept-source-agreements --accept-package-agreements --disable-interactivity"; \
  StatusMsg: "Installing FFmpeg (this can take a minute)..."; Tasks: ffmpeg; Flags: runhidden waituntilterminated skipifdoesntexist
#endif
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function FFmpegInstalled: Boolean;
begin
  Result := (FileSearch('ffmpeg.exe', GetEnv('PATH')) <> '') or
            FileExists(ExpandConstant('{localappdata}\Microsoft\WinGet\Links\ffmpeg.exe'));
end;
