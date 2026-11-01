#ifndef SmartPrintPublicUrl
  #define SmartPrintPublicUrl "https://qr-test.paintpulp.com"
#endif
#ifndef SmartPrintVersion
  #define SmartPrintVersion "0.1.0"
#endif

[Setup]
SourceDir=..
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
UninstallDisplayName=SmartPrint Windows Agent
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "artifacts\agent\*"; DestDir: "{app}"; Excludes: "pair-agent.ps1,appsettings.example.json,README.md"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SmartPrint shop website"; Filename: "{#SmartPrintPublicUrl}"
Name: "{group}\Uninstall SmartPrint"; Filename: "{uninstallexe}"

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\uninstall-agent-service.ps1"""; Flags: runhidden waituntilterminated

[Code]
var
  PairingPage: TInputQueryWizardPage;

function SetEnvironmentVariableW(lpName, lpValue: string): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

procedure InitializeWizard;
begin
  PairingPage := CreateInputQueryPage(
    wpSelectDir,
    'Connect this computer to your shop',
    'Enter a code for first-time setup, or reuse an existing active pairing.',
    'In the shop workspace, open Print Agent → Add computer to generate a 10-minute code. Leave this blank when updating or repairing a computer that is already paired and active. To pair with a different shop, revoke the old computer in its previous shop workspace before entering a new code.');
  PairingPage.Add('One-time pairing code (first-time or replacement only)', True);
end;

function IsValidPairingCode(const Value: string): Boolean;
var
  I: Integer;
  C: Char;
  Code: string;
begin
  Code := Trim(Value);
  Result := Length(Code) = 32;
  if not Result then
    Exit;

  for I := 1 to Length(Code) do
  begin
    C := Code[I];
    if not (((C >= 'a') and (C <= 'z')) or
            ((C >= 'A') and (C <= 'Z')) or
            ((C >= '0') and (C <= '9')) or
            (C = '-') or (C = '_')) then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = PairingPage.ID then
  begin
    if (Trim(PairingPage.Values[0]) <> '') and
       (not IsValidPairingCode(PairingPage.Values[0])) then
    begin
      MsgBox('Enter the complete 32-character pairing code from your SmartPrint shop workspace, or leave it blank to reuse an existing active pairing.', mbInformation, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  AgentPath: string;
  PairExitCode: Integer;
  InstallExitCode: Integer;
  VerifyExitCode: Integer;
  PowerShellPath: string;
  InstallParameters: string;
  VerifyParameters: string;
begin
  if CurStep <> ssPostInstall then
    Exit;

  AgentPath := ExpandConstant('{app}\SmartPrint.Agent.exe');
  if Trim(PairingPage.Values[0]) = '' then
  begin
    if not Exec(AgentPath, '--check-pairing', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, PairExitCode) then
      RaiseException('SmartPrint could not check the saved pairing for this computer.');
    if PairExitCode <> 0 then
      RaiseException('No active pairing could be verified. Enter a new 10-minute code from Shop workspace → Print Agent → Add computer. If this computer was paired before, revoke the previous entry in that shop workspace before pairing it again.');
  end
  else
  begin
    if not SetEnvironmentVariableW('Agent__ApiBaseUrl', '{#SmartPrintPublicUrl}') then
      RaiseException('Could not prepare the SmartPrint service address.');

    if not SetEnvironmentVariableW('Agent__EnrollmentCode', Trim(PairingPage.Values[0])) then
      RaiseException('Could not prepare the one-time pairing code.');

    if not Exec(AgentPath, '--pair', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, PairExitCode) then
      RaiseException('SmartPrint could not start its secure pairing step. Restart setup and try again.');

    SetEnvironmentVariableW('Agent__EnrollmentCode', '');
    SetEnvironmentVariableW('Agent__ApiBaseUrl', '');
    if PairExitCode <> 0 then
      RaiseException('SmartPrint could not pair this computer. Check that the code is current and belongs to this shop. If this computer is already paired to this shop, revoke its previous entry in Print Agent before pairing it again.');
  end;

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  InstallParameters :=
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\install-agent-service.ps1') +
    '" -AgentExePath "' + AgentPath +
    '" -CustomerPortalOrigin "' + '{#SmartPrintPublicUrl}' + '"';

  if not Exec(PowerShellPath, InstallParameters, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, InstallExitCode) then
    RaiseException('SmartPrint paired successfully, but Windows could not start the background service installer.');

  SetEnvironmentVariableW('Agent__ApiBaseUrl', '');
  if InstallExitCode <> 0 then
    RaiseException('SmartPrint paired successfully, but Windows could not finish service setup. Run setup again as an administrator or contact support.');

  VerifyParameters :=
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\verify-agent-local.ps1') + '" -TimeoutSeconds 60';
  if not Exec(PowerShellPath, VerifyParameters, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, VerifyExitCode) then
    RaiseException('SmartPrint was installed, but its local readiness check could not start.');
  if VerifyExitCode <> 0 then
    RaiseException('SmartPrint was installed but did not become ready. Restart the computer and check the SmartPrint Windows Service.');
end;
