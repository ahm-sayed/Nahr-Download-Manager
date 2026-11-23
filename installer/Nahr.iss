; Nahr Download Manager — Windows Installer Script (Inno Setup 6)
; Builds a self-contained single-file Setup executable (dist\Nahr-Setup-1.3.4.exe)
; including the Nahr WPF desktop app (.NET 10 self-contained win-x64) and browser extension.

#define MyAppName "Nahr Download Manager"
#define MyAppShortName "Nahr"
#define MyAppVersion "1.3.4"
#define MyAppPublisher "Nahr"
#define MyAppURL "https://github.com/ahm-sayed/Nahr-Download-Manager"
#define MyAppExeName "Nahr.exe"

[Setup]
AppId={{8E4C7A2B-6C51-4F90-9A1D-2B4E8F3C109A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppShortName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=Nahr-Setup-{#MyAppVersion}
SetupIconFile=..\src\DownloadManager.App\Assets\nahr.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
RestartApplications=no
AppMutex=Local\NahrDownloadManager_SingleInstance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "Start Nahr silently in background with Windows (recommended for browser extension)"; GroupDescription: "Integration:"

[Files]
; Self-contained Nahr desktop app
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Official Browser Extension (Chrome / Edge / Brave / Firefox)
Source: "..\browser-extension\*"; DestDir: "{app}\browser-extension"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Browser Extension Folder"; Filename: "{app}\browser-extension"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppShortName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Optional silent tray startup on Windows login
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Nahr"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue
; Clean up Native Messaging Host registry entries on uninstall
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.nahr.downloader"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.nahr.downloader"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.nahr.downloader"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Chromium\NativeMessagingHosts\com.nahr.downloader"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Mozilla\NativeMessagingHosts\com.nahr.downloader"; Flags: uninsdeletekey

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Nahr\native-host"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
