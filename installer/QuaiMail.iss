; Installateur de Quai Mail (Inno Setup 6), repris de celui de Quai : un seul exécutable, installation par
; utilisateur (sans droits administrateur), désinstallation depuis « Applications installées » de Windows.
;
; Compilé par le CI (.github/workflows/build.yml) :
;   ISCC.exe /DAppVersion=0.1.0 /DAppNumericVersion=0.1.0 /DArch=x64 /DSourceDir=..\publish\x64 /DOutputDir=..\dist installer\QuaiMail.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppNumericVersion
  #define AppNumericVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\" + Arch
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "Quai Mail"
#define AppExe "QuaiMail.exe"
; Dossier des données (%LOCALAPPDATA%\QuaiMail : réglages, brouillons, journaux), voir AppPaths.cs.
#define DataDir "QuaiMail"
#define AppUrl "https://github.com/memton80/quai-mail"

[Setup]
; Identifiant stable : ne jamais le changer, il relie les mises à jour et la désinstallation.
AppId={{45AC4609-96F5-4C2C-A740-E243A390EA78}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppNumericVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Installation de {#AppName}

; Par utilisateur : %LOCALAPPDATA%\Programs\Quai Mail, aucune élévation.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=no
UsePreviousAppDir=yes
MinVersion=10.0.17763

#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

OutputDir={#OutputDir}
OutputBaseFilename=QuaiMail-Setup-{#Arch}
SetupIconFile=..\src\QuaiMail.App\Assets\QuaiMail.ico
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Mise à jour par-dessus une version ouverte : l'installateur propose de fermer Quai Mail.
CloseApplications=yes
RestartApplications=no

UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
french.RemoveUserData=Supprimer aussi vos réglages, brouillons gardés sur cet ordinateur et journaux de Quai Mail ?%n%nVos mails restent sur vos serveurs, ils ne sont jamais touchés.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Mise à jour lancée par Quai Mail lui-même (/SILENT /RELAUNCH=1) : la nouvelle version redémarre une fois installée.
Filename: "{app}\{#AppExe}"; Parameters: "--after-update"; WorkingDir: "{app}"; Flags: nowait; Check: RelaunchRequested

[UninstallDelete]
; Journaux : jamais conservés, ce ne sont pas des données de l'utilisateur.
Type: filesandordirs; Name: "{localappdata}\{#DataDir}\logs"
Type: filesandordirs; Name: "{localappdata}\{#DataDir}\updates"
Type: dirifempty; Name: "{localappdata}\{#DataDir}"

[Code]
var
  RemoveUserData: Boolean;

// Installation demandée par le bouton « Mettre à jour » de Quai Mail : relancer l'application à la fin.
function RelaunchRequested(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

// Désinstallation : on demande s'il faut aussi effacer les réglages (non par défaut).
// En mode silencieux, le paramètre /PURGE=1 répond « oui ».
function InitializeUninstall(): Boolean;
begin
  Result := True;
  if UninstallSilent() then
    RemoveUserData := ExpandConstant('{param:PURGE|0}') = '1'
  else
    RemoveUserData := MsgBox(CustomMessage('RemoveUserData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and RemoveUserData then
    DelTree(ExpandConstant('{localappdata}\{#DataDir}'), True, True, True);
end;
