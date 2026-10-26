; Inno Setup 6 — instalación por usuario, sin privilegios administrativos (RF-01)
#define MyAppName "SpywareAgent"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "SpywareLab"
#define MyOutputExe "SpywareAgent.exe"

[Setup]
AppId={{8E3F0F3A-6C7B-4F2A-9F41-B6E7C1D5A210}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\SpywareLab\SpywareAgent\bin
DefaultGroupName=SpywareLab
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir=..\dist
OutputBaseFilename=SpywareAgent-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyOutputExe}
WizardStyle=modern

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "startupfolder"; Description: "Añadir también acceso directo en la carpeta de Inicio"; Flags: unchecked
Name: "retentionhelp"; Description: "Mostrar el estado del almacén local al terminar"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Run]
Filename: "{app}\{#MyOutputExe}"; Parameters: "--install{code:StartupFlag}"; Description: "Instalar persistencia e iniciar el agente"; Flags: postinstall nowait shellexec
Filename: "{app}\{#MyOutputExe}"; Parameters: "--status"; Check: TaskSelected

[Code]
function StartupFlag(Param: String): String;
begin
  if WizardIsTaskSelected('startupfolder') then
    Result := ' --startup-folder --start'
  else
    Result := ' --start';
end;

function TaskSelected(Param: String): Boolean;
begin
  Result := WizardIsTaskSelected('retentionhelp');
end;

[UninstallRun]
Filename: "{app}\{#MyOutputExe}"; Parameters: "--uninstall --remove-data"; Flags: runhidden; RunOnceId: "RemoveSpywareAgent"
