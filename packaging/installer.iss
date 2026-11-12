; Inno Setup script for the Windows installer. The workspace build script supplies the packaged app path;
; it passes the version with /DAppVersion=...

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef AppSourceDir
  #define AppSourceDir "..\dist\EtherlightingEvolved"
#endif

#define AppName "Etherlighting Evolved"
#define AppExe "EtherlightingEvolved.exe"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
; The AppId identifies the app across upgrades. Never change it.
AppId={{B5958405-DA3C-4585-BFA8-5DBFF340C199}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Views2k
AppPublisherURL=https://github.com/Views2k/etherlighting-evolved
AppSupportURL=https://github.com/Views2k/etherlighting-evolved/issues
AppCopyright=Copyright (c) 2026 Views2k
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
SetupIconFile=..\src\etherlighting_evolved\assets\ee.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard.bmp,wizard-200.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small-200.bmp
CloseApplications=no
RestartApplications=no
OutputDir=..\dist
OutputBaseFilename=EtherlightingEvolved-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes

[Messages]
WelcomeLabel1=Etherlighting Evolved
WelcomeLabel2=Install lighting controls for your UniFi switch.%n%nUnofficial community project. Not affiliated with Ubiquiti.
FinishedHeadingLabel=Etherlighting Evolved
FinishedLabel=Installation complete. The app runs in the notification area to keep your lighting active when its window is closed.
SelectDirLabel3=Choose where to install the app.

[Tasks]
Name: "autostart"; Description: "Start the app when I sign in to Windows"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[InstallDelete]
; The Python runtime of the 0.4 desktop program. The native desktop program does not use it.
Type: filesandordirs; Name: "{app}\_internal"

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --background"; Tasks: autostart
; Removed only when someone chose so: on the tasks page or with /TASKS. A silent upgrade keeps the current setting,
; which the app's notification area menu can also change.
Root: HKCU; Subkey: "{#RunKey}"; ValueType: none; ValueName: "{#AppName}"; Flags: deletevalue; Tasks: not autostart; Check: TasksChosen

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open lighting controls"; Flags: nowait postinstall skipifsilent

[Code]
const
  AppMutex = 'EtherlightingEvolved';

var
  TasksPrepared: Boolean;

function TasksOnCommandLine(): Boolean;
var
  Tail: String;
begin
  Tail := Uppercase(GetCmdTail);
  Result := (Pos('/TASKS=', Tail) > 0) or (Pos('/MERGETASKS=', Tail) > 0);
end;

function TasksChosen(): Boolean;
begin
  Result := (not WizardSilent) or TasksOnCommandLine();
end;

{ The tasks page starts from the current Start with Windows setting. }
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectTasks) and not TasksPrepared then
  begin
    TasksPrepared := True;
    if not TasksOnCommandLine() and RegValueExists(HKCU, '{#RunKey}', '{#AppName}') then
      WizardSelectTasks('autostart');
  end;
end;

procedure InitializeWizard();
begin
  WizardForm.Font.Name := 'Segoe UI';
  WizardForm.WelcomeLabel1.Font.Name := 'Segoe UI';
  WizardForm.WelcomeLabel1.Font.Style := [fsBold];
  WizardForm.WelcomeLabel1.Font.Size := 20;
  WizardForm.FinishedHeadingLabel.Font.Name := 'Segoe UI';
  WizardForm.FinishedHeadingLabel.Font.Style := [fsBold];
  WizardForm.FinishedHeadingLabel.Font.Size := 20;
end;

{ Ask a running copy to quit before replacing its files. }
function QuitRunningCopy(): Boolean;
var
  Exe: String;
  ResultCode: Integer;
begin
  Result := True;
  if not (CheckForMutexes(AppMutex) or CheckForMutexes('EtherlightingEvolved.Desktop')) then
    Exit;
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
  begin
    if not Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      Result := False;
      Exit;
    end;
    if ResultCode <> 0 then
    begin
      Result := False;
      Exit;
    end;
  end;
  Result := not (CheckForMutexes(AppMutex) or CheckForMutexes('EtherlightingEvolved.Desktop'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not QuitRunningCopy() then
    Result := 'EE is still running. Quit the desktop app from its notification area menu, or choose Settings > Close web session in the web workspace. Then reopen setup.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := QuitRunningCopy();
  if not Result then
    MsgBox('EE is still running. Quit the desktop app from its notification area menu, or choose Settings > Close web session in the web workspace. Then retry uninstalling.', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { The app's own "Start with Windows" switch can add this value even if the installer didn't. }
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, '{#RunKey}', '{#AppName}');
end;
