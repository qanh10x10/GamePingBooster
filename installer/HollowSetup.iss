; Build: python installer/build.py --iscc C:\path\ISCC.exe
#ifndef Payload
  #error Build the self-contained payload using installer/build.py first.
#endif
#ifndef AppVersion
  #define AppVersion "0.0.2"
#endif
#define ServiceName "HollowPingBooster"

[Setup]
AppId={{D7E84B1A-56E2-4C3D-8A7B-9E10F23C5E47}
AppName=Hollow Ping Booster
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
AppPublisher=Hollow
DefaultDirName={autopf}\Hollow Ping Booster
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=Hollow Ping Booster
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UsePreviousAppDir=no
CloseApplications=no
RestartApplications=no
AppMutex=Local\HollowPingBooster.SingleInstance
UninstallDisplayIcon={app}\GamePingBooster.exe
OutputDir=dist
OutputBaseFilename=HollowPingBooster-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
InfoBeforeFile=SETUP-NOTES.txt
SetupLogging=yes

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Payload}\setup-service.ps1"; Flags: dontcopy
Source: "{#Payload}\SHA256SUMS.json"; Flags: dontcopy

[Icons]
Name: "{group}\Hollow Ping Booster"; Filename: "{app}\GamePingBooster.exe"
Name: "{autodesktop}\Hollow Ping Booster"; Filename: "{app}\GamePingBooster.exe"

[Run]
Filename: "{app}\GamePingBooster.exe"; Description: "Open Hollow Ping Booster"; Flags: nowait postinstall skipifsilent unchecked runasoriginaluser

[Code]
var
  LastHelperError: String;

procedure OnHelperLog(const S: String; const Error, FirstLine: Boolean);
begin
  Log(S);
  if Pos('ERROR [', S) = 1 then LastHelperError := S;
  if Error then LastHelperError := 'Could not read service setup output: ' + S;
end;

function RunServiceHelper(const HelperPath, Options: String): Boolean;
var
  ExitCode: Integer;
begin
  LastHelperError := '';
  Result := ExecAndLogOutput(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + HelperPath +
    '" -InstallDir "' + ExpandConstant('{app}') + '" ' + Options,
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode, @OnHelperLog);
  if not Result then LastHelperError := 'Could not launch service setup: ' + SysErrorMessage(ExitCode)
  else if (ExitCode <> 0) and (LastHelperError = '') then
    LastHelperError := 'Service helper exit code: ' + IntToStr(ExitCode);
  Result := Result and (ExitCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  ExtractTemporaryFile('setup-service.ps1');
  ExtractTemporaryFile('SHA256SUMS.json');
  if not RunServiceHelper(ExpandConstant('{tmp}\setup-service.ps1'), '-CheckOnly -PayloadManifest "' + ExpandConstant('{tmp}\SHA256SUMS.json') + '"') then
    Result := LastHelperError + #13#10 + 'Existing services were not stopped or replaced.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if not RunServiceHelper(ExpandConstant('{app}\setup-service.ps1'), '') then
      RaiseException(LastHelperError + #13#10 + 'Hollow service setup failed. The installation is not ready.' + #13#10 +
        'Log: ' + ExpandConstant('{log}') + #13#10 + 'Vendor Game Ping Booster was not modified.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    if not RunServiceHelper(ExpandConstant('{app}\setup-service.ps1'), '-Uninstall') then
      RaiseException(LastHelperError + #13#10 + 'Service removal failed. Its files will not be deleted.');
end;
