#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#define AppExeName "BatomonHelper.Overlay.exe"
#ifndef AppId
  #define AppId "{{83E75DD6-FD4A-45C8-8C82-B793864385EE}"
#endif

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
RestartApplications=no
AppPublisherURL=https://github.com/kamikadzexa/BatomonHelper
AppSupportURL=https://github.com/kamikadzexa/BatomonHelper/issues
MinVersion=10.0.22621
UninstallDisplayName=Batomon Helper
LicenseFile=..\LICENSE.txt
CloseApplicationsFilter={#AppExeName}

[Files]
Source: "staging\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Icons]
Name: "{autodesktop}\Batomon Helper"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{group}\Batomon Helper"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,Batomon Helper}"; Flags: nowait postinstall skipifsilent
