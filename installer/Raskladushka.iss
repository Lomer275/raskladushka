#ifndef AppVersion
  #error AppVersion is required. Pass /DAppVersion=<version>.
#endif
#ifndef PublishDir
  #error PublishDir is required. Pass /DPublishDir=<self-contained publish directory>.
#endif
#ifndef RuntimeDir
  #error RuntimeDir is required. Pass /DRuntimeDir=<Visual C++ runtime directory>.
#endif

[Setup]
; AppId is permanent: never change it, or Windows will treat a new version as a different program.
AppId={{B9B7AC94-39DA-4F67-9064-BDB7410D05E9}
AppName=Раскладушка
AppVersion={#AppVersion}
AppPublisher=Lomer275
AppPublisherURL=https://github.com/Lomer275/raskladushka
DefaultDirName={localappdata}\Programs\Raskladushka
PrivilegesRequired=lowest
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\installer
OutputBaseFilename=Raskladushka-{#AppVersion}-setup
SetupIconFile=..\assets\icon\raskladushka.ico
UninstallDisplayIcon={app}\Raskladushka.exe
CloseApplications=no
UsePreviousTasks=no
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "autostart"; Description: "Запускать вместе с Windows"
Name: "desktopicon"; Description: "Значок на рабочем столе"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#RuntimeDir}\*"; DestDir: "{app}\vcrt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Раскладушка"; Filename: "{app}\Raskladushka.exe"
Name: "{autodesktop}\Раскладушка"; Filename: "{app}\Raskladushka.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Raskladushka.exe"; Description: "Запустить Раскладушку"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  CloseError = 'Не удалось закрыть Раскладушку. Закройте её через значок в трее и нажмите «Повторить».';

var
  IsUpdate: Boolean;
  AutostartWas: Boolean;
  TasksPageShown: Boolean;

function InitializeSetup(): Boolean;
begin
  IsUpdate := FileExists(ExpandConstant('{localappdata}\Programs\Raskladushka\Raskladushka.exe'));
  AutostartWas := RegValueExists(HKCU, RunKey, 'Raskladushka');
  Result := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectTasks) and not TasksPageShown then
  begin
    TasksPageShown := True;
    if IsUpdate then
    begin
      if AutostartWas then
        WizardSelectTasks('autostart')
      else
        WizardSelectTasks('!autostart');
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardSilent and IsUpdate then
      Exit;
    if WizardIsTaskSelected('autostart') then
      RegWriteStringValue(HKCU, RunKey, 'Raskladushka',
        '"' + ExpandConstant('{app}\Raskladushka.exe') + '"')
    else
      RegDeleteValue(HKCU, RunKey, 'Raskladushka');
  end;
end;

function CloseRunningApp(Silent: Boolean): Boolean;
var
  ExePath: String;
  ExitCode: Integer;
begin
  Result := True;
  ExePath := ExpandConstant('{app}\Raskladushka.exe');
  if not FileExists(ExePath) then
    Exit;
  while True do
  begin
    if not Exec(ExePath, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      Exit;
    if ExitCode = 0 then
      Exit;
    if Silent then
    begin
      Result := False;
      Exit;
    end;
    if SuppressibleMsgBox(CloseError, mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not CloseRunningApp(False) then
    Result := CloseError;
end;

function InitializeUninstall(): Boolean;
begin
  Result := CloseRunningApp(UninstallSilent);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, RunKey, 'Raskladushka');
    if not UninstallSilent then
    begin
      if MsgBox('Удалить также настройки, журнал и скачанный голосовой ввод?',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(ExpandConstant('{userappdata}\Raskladushka'), True, True, True);
        DelTree(ExpandConstant('{localappdata}\Raskladushka'), True, True, True);
      end;
    end;
  end;
end;
