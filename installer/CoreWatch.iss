; CoreWatch installer (Inno Setup 6.3 or newer).
; Build it with tools\build-installer.ps1, which publishes the app first and passes Source and OutDir.

#ifndef Source
  #define Source AddBackslash(SourcePath) + "..\obj\installer\app"
#endif
#ifndef OutDir
  #define OutDir AddBackslash(SourcePath) + "..\publish"
#endif

; The version comes from the published exe, so it cannot drift from the app.
#define Major
#define Minor
#define Rev
#define Build
#expr GetVersionComponents(AddBackslash(Source) + "CoreWatch.exe", Major, Minor, Rev, Build)
#if Major == 0 && Minor == 0 && Rev == 0
  #error CoreWatch.exe was not found in Source, or it has no version. Run tools\build-installer.ps1.
#endif
#define AppVersion Str(Major) + "." + Str(Minor) + "." + Str(Rev)

#define AppName "CoreWatch"
#define AppExe "CoreWatch.exe"
#define AppUrl "https://github.com/haych-dev/CoreWatch"

[Setup]
; Never change AppId: it is how a newer installer finds and upgrades an older install.
AppId={{144FD861-B5E5-4026-A5B9-06CE83D86C0F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=haych-dev
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

; Installs for the current user without an administrator prompt, or for all users if chosen on the first dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; The mutex CoreWatch holds while it runs (including in the notification area).
; Setup and the uninstaller ask for CoreWatch to be closed before they continue.
AppMutex=CoreWatch.SingleInstance

SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern dynamic
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#OutDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; The same per-user setting as "Start with Windows" in CoreWatch's Settings, so it is only offered for a per-user install.
Name: "autostart"; Description: "Start {#AppName} when I sign in, in the notification area"; GroupDescription: "Start-up:"; Flags: unchecked; Check: not IsAdminInstallMode

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Written exactly as CoreWatch writes it. Removal is handled in [Code], because the switch in Settings can also create it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --tray"; Tasks: autostart; Check: not IsAdminInstallMode

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

{ Removes "Start with Windows" only when it points at the copy being uninstalled. }
procedure RemoveAutoStart;
var
  Value: String;
begin
  if RegQueryStringValue(HKCU, RunKey, '{#AppName}', Value) and
     (Pos(Lowercase(ExpandConstant('{app}\{#AppExe}')), Lowercase(Value)) > 0) then
    RegDeleteValue(HKCU, RunKey, '{#AppName}');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usUninstall then
    RemoveAutoStart;

  if CurUninstallStep = usPostUninstall then
  begin
    { Settings, history and undo backups are kept unless the user asks otherwise. A silent uninstall always keeps them. }
    Data := ExpandConstant('{userappdata}\{#AppName}');
    if DirExists(Data) and not UninstallSilent then
      if MsgBox('Also delete your {#AppName} settings, history and undo backups?' + #13#10#13#10 +
                'The backups are what let {#AppName} undo its tweaks, service changes, DNS changes and registry fixes. ' +
                'Choose No to keep everything for a later install.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
