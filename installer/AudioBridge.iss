; AudioBridge Inno Setup Script
#define MyAppName "AudioBridge"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "AudioBridge"
#define MyAppExeName "AudioBridge.exe"
#define SourceDir "C:\Users\Mount Pearl\.gemini\antigravity\scratch\AudioBridge"

[Setup]
AppId={{D37E6B20-A227-4180-87D9-B1A0F57189E1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir={#SourceDir}\release
OutputBaseFilename=AudioBridge-Setup
SetupIconFile={#SourceDir}\windows\AudioBridge.Tray\Resources\AudioBridge.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "installvbcable"; Description: "Install VB-CABLE Virtual Audio Driver (Recommended for silent PC speakers)"; GroupDescription: "Drivers:"

[Files]
; Windows Application binaries
Source: "{#SourceDir}\windows\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Bundled Drivers
Source: "{#SourceDir}\drivers\vbcable\*"; DestDir: "{app}\drivers\vbcable"; Flags: ignoreversion recursesubdirs
; Android APK
Source: "{#SourceDir}\android\app\build\outputs\apk\debug\app-debug.apk"; DestDir: "{app}\android"; DestName: "AudioBridge.apk"; Flags: ignoreversion
; Phone install helper
Source: "{#SourceDir}\installer\install-to-phone.bat"; DestDir: "{app}\android"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Install AudioBridge to Android Phone"; Filename: "{app}\android\install-to-phone.bat"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Install VB-CABLE Driver"; Filename: "{app}\drivers\vbcable\VBCABLE_Setup_x64.exe"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; IconFilename: "{app}\{#MyAppExeName}"

[Run]
; Option to run VB-CABLE driver installer during setup
Filename: "{app}\drivers\vbcable\VBCABLE_Setup_x64.exe"; Parameters: "-i"; StatusMsg: "Installing VB-CABLE virtual audio driver..."; Flags: runascurrentuser waituntilterminated; Tasks: installvbcable
; Launch AudioBridge after setup
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
