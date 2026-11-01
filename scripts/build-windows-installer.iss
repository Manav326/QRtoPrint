#ifndef SmartPrintPublicUrl
  #define SmartPrintPublicUrl "https://qr-test.paintpulp.com"
#endif
#ifndef SmartPrintVersion
  #define SmartPrintVersion "0.1.0"
#endif

[Setup]
SourceDir=..\
AppId={{A7F2E9CA-2C3A-4E58-AE63-26E6B7B0DDF1}
AppName=SmartPrint by QuantaEdge
AppVersion={#SmartPrintVersion}
AppPublisher=QuantaEdge
DefaultDirName={autopf}\SmartPrint
DefaultGroupName=SmartPrint
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=artifacts\agent-installer
OutputBaseFilename=SmartPrint-Windows-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=SmartPrint by QuantaEdge
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "scripts\agent-service-lifecycle.ps1"; Flags: dontcopy
Source: "artifacts\agent\*"; DestDir: "{app}\agent"; Excludes: "pair-agent.ps1,appsettings.example.json,README.md"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "artifacts\desktop\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\SmartPrint.Agent.exe"
Type: files; Name: "{app}\appsettings.json"
Type: files; Name: "{app}\install-agent-service.ps1"
Type: files; Name: "{app}\uninstall-agent-service.ps1"
Type: files; Name: "{app}\verify-agent-local.ps1"

[Icons]
Name: "{group}\SmartPrint"; Filename: "{app}\SmartPrint.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\SmartPrint"; Filename: "{app}\SmartPrint.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{group}\Uninstall SmartPrint"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\SmartPrint.Desktop.exe"; Description: "Launch SmartPrint"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\agent\uninstall-agent-service.ps1"""; Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
var
  ServiceWasRunning: Boolean;
  ServiceInstallSucceeded: Boolean;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  PowerShellPath: string;
  Parameters: string;
  ExitCode: Integer;
begin
  Result := '';
  NeedsRestart := False;
  ExtractTemporaryFile('agent-service-lifecycle.ps1');
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters :=
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\agent-service-lifecycle.ps1') + '"';

  if not Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'SmartPrint could not safely prepare its background print service. Restart Windows and run setup again.';
    Exit;
  end;

  if ExitCode = 10 then
    ServiceWasRunning := True
  else if ExitCode <> 0 then
    Result := 'SmartPrint could not stop its existing background print service safely. Restart Windows and try setup again.';
end;

procedure DeinitializeSetup();
var
  PowerShellPath: string;
  Parameters: string;
  ExitCode: Integer;
begin
  if not ServiceWasRunning or ServiceInstallSucceeded then
    Exit;

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters :=
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\agent-service-lifecycle.ps1') + '" -Start';
  Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  PowerShellPath: string;
  Parameters: string;
  ExitCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters :=
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\agent\install-agent-service.ps1') +
    '" -AgentExePath "' + ExpandConstant('{app}\agent\SmartPrint.Agent.exe') +
    '" -CustomerPortalOrigin "{#SmartPrintPublicUrl}" -ApiBaseUrl "{#SmartPrintPublicUrl}"';

  if not Exec(PowerShellPath, Parameters, ExpandConstant('{app}\agent'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    RaiseException('SmartPrint could not start its background service installer.');
  if ExitCode <> 0 then
    RaiseException('SmartPrint could not install or start the background print service. No printer connection has been changed. Restart Windows and run setup again.');

  ServiceInstallSucceeded := True;
end;
