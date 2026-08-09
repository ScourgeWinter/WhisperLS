#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef Variant
  #define Variant "Full"
#endif
#ifndef AppSource
  #error AppSource must be provided
#endif
#ifndef OutputDirectory
  #define OutputDirectory "."
#endif

#define DisplayVariant Variant

[Setup]
AppId={{D870B13A-9620-4A41-AC49-FE16B7C7D243}
AppName=WhisperLS
AppVersion={#AppVersion}
AppVerName=WhisperLS {#AppVersion} {#DisplayVariant}
AppPublisher=ScourgeWinter
AppPublisherURL=https://github.com/ScourgeWinter/WhisperLS
AppSupportURL=https://github.com/ScourgeWinter/WhisperLS/issues
DefaultDirName={localappdata}\Programs\WhisperLS
DefaultGroupName=WhisperLS
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDirectory}
OutputBaseFilename=WhisperLS-Setup-{#AppVersion}-{#DisplayVariant}-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\FasterWhisperGui\Assets\WhisperLS.ico
UninstallDisplayIcon={app}\WhisperLS.exe
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}.0

[Tasks]
Name: "desktopicon"; Description: "Crea un collegamento sul Desktop"; GroupDescription: "Collegamenti aggiuntivi:"; Flags: unchecked

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\WhisperLS"; Filename: "{app}\WhisperLS.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\WhisperLS"; Filename: "{app}\WhisperLS.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\WhisperLS.exe"; Description: "Avvia WhisperLS"; Flags: nowait postinstall skipifsilent

[Code]
var
  RemoveUserDataCheckBox: TNewCheckBox;

procedure InitializeUninstallProgressForm();
begin
  RemoveUserDataCheckBox := TNewCheckBox.Create(UninstallProgressForm);
  RemoveUserDataCheckBox.Parent := UninstallProgressForm;
  RemoveUserDataCheckBox.Left := UninstallProgressForm.StatusLabel.Left;
  RemoveUserDataCheckBox.Top := UninstallProgressForm.ProgressBar.Top + UninstallProgressForm.ProgressBar.Height + ScaleY(16);
  RemoveUserDataCheckBox.Width := UninstallProgressForm.StatusLabel.Width;
  RemoveUserDataCheckBox.Caption := 'Elimina anche impostazioni, checkpoint e dati locali di WhisperLS';
  RemoveUserDataCheckBox.Checked := False;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and Assigned(RemoveUserDataCheckBox) and RemoveUserDataCheckBox.Checked then
    DelTree(ExpandConstant('{localappdata}\WhisperLS'), True, True, True);
end;
