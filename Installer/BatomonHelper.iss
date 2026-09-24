#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExeName "BatomonHelper.Overlay.exe"
#define AppId "{{83E75DD6-FD4A-45C8-8C82-B793864385EE}"
#define RuntimeVersion "10.0.10"
#define RuntimeFile "windowsdesktop-runtime-10.0.10-win-x64.exe"

[Setup]
AppId={#AppId}
AppName=Batomon Helper
AppVersion={#AppVersion}
AppVerName=Batomon Helper {#AppVersion}
AppPublisher=Batomon Helper
DefaultDirName={autopf}\Batomon Helper
DefaultGroupName=Batomon Helper
UsePreviousAppDir=yes
UsePreviousGroup=yes
DisableReadyPage=yes
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=BatomonHelper-Setup-{#AppVersion}
SetupIconFile=..\src\BatomonHelper.Overlay\Assets\batomon-helper.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExeName}
RestartIfNeededByRun=no
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}

[Files]
Source: "dependencies\{#RuntimeFile}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWindowsDesktopRuntime
Source: "staging\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autodesktop}\Batomon Helper"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\Batomon Helper"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"

[Run]
Filename: "{tmp}\{#RuntimeFile}"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing .NET Desktop Runtime..."; Flags: waituntilterminated runhidden; Check: NeedsWindowsDesktopRuntime
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,Batomon Helper}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RuntimeName = 'Microsoft.WindowsDesktop.App';
  MinimumRuntimeVersion = '{#RuntimeVersion}';
  UninstallRegKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1';

function GetVersionPart(var Version: string): Integer;
var
  Part: string;
  DotPosition: Integer;
begin
  DotPosition := Pos('.', Version);
  if DotPosition = 0 then
  begin
    Part := Version;
    Version := '';
  end
  else
  begin
    Part := Copy(Version, 1, DotPosition - 1);
    Delete(Version, 1, DotPosition);
  end;
  Result := StrToIntDef(Part, 0);
end;

function CompareVersions(const First, Second: string): Integer;
var
  FirstValue: string;
  SecondValue: string;
  FirstPart: Integer;
  SecondPart: Integer;
begin
  FirstValue := First;
  SecondValue := Second;
  while (FirstValue <> '') or (SecondValue <> '') do
  begin
    FirstPart := GetVersionPart(FirstValue);
    SecondPart := GetVersionPart(SecondValue);
    if FirstPart < SecondPart then begin Result := -1; Exit; end;
    if FirstPart > SecondPart then begin Result := 1; Exit; end;
  end;
  Result := 0;
end;

function NeedsWindowsDesktopRuntime: Boolean;
var
  RuntimeRoot: string;
  Versions: TArrayOfString;
  Index: Integer;
begin
  Result := True;
  RuntimeRoot := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\' + RuntimeName;
  if not RegGetSubkeyNames(HKLM, RuntimeRoot, Versions) then Exit;
  for Index := 0 to GetArrayLength(Versions) - 1 do
    if CompareVersions(Versions[Index], MinimumRuntimeVersion) >= 0 then
    begin
      Result := False;
      Exit;
    end;
end;

function GetPreviousUninstaller(): string;
var
  UninstallCommand: string;
begin
  Result := '';
  if RegQueryStringValue(HKLM64, UninstallRegKey, 'UninstallString', UninstallCommand) then
    Result := UninstallCommand
  else if RegQueryStringValue(HKLM, UninstallRegKey, 'UninstallString', UninstallCommand) then
    Result := UninstallCommand;
end;

procedure StopPreviousApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/im "{#AppExeName}" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  PreviousUninstaller: string;
  ResultCode: Integer;
begin
  Result := '';
  StopPreviousApp();
  PreviousUninstaller := GetPreviousUninstaller();
  if PreviousUninstaller = '' then Exit;

  PreviousUninstaller := RemoveQuotes(PreviousUninstaller);
  if not Exec(PreviousUninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := 'The previous Batomon Helper version could not be removed.';
end;
