; Wireless Bark Inno Setup Script
; Built for TAB inc. - Straightforward, Tracker-Free Software

#define MyAppName "Wireless Bark"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "TAB inc."
#define MyAppURL "https://github.com/Tr1pAc1d/Fetch"
#define MyAppExeName "WirelessBark.exe"
#define SourceDir "C:\Users\Mount Pearl\.gemini\antigravity\scratch\AudioBridge"

[Setup]
AppId={{D37E6B20-A227-4180-87D9-B1A0F57189E1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir={#SourceDir}\release
OutputBaseFilename=WirelessBark-Setup
SetupIconFile={#SourceDir}\windows\AudioBridge.Tray\Resources\AudioBridge.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

; Traditional Windows Setup Wizard Pages
DisableWelcomePage=no
LicenseFile={#SourceDir}\installer\TERMS.txt
DisableDirPage=no
DisableProgramGroupPage=no
DisableReadyPage=no
DisableFinishedPage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "installvbcable"; Description: "Install VB-CABLE Virtual Audio Driver (Recommended for silent PC speakers)"; GroupDescription: "Audio Drivers:"

[Files]
; Windows Application binaries
Source: "{#SourceDir}\windows\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Bundled Drivers
Source: "{#SourceDir}\drivers\vbcable\*"; DestDir: "{app}\drivers\vbcable"; Flags: ignoreversion recursesubdirs
; Android APK
Source: "{#SourceDir}\android\app\build\outputs\apk\debug\app-debug.apk"; DestDir: "{app}\android"; DestName: "WirelessBark.apk"; Flags: ignoreversion
; Phone install helper
Source: "{#SourceDir}\installer\install-to-phone.bat"; DestDir: "{app}\android"; Flags: ignoreversion
; Terms of Service document
Source: "{#SourceDir}\installer\TERMS.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Install to Android Phone"; Filename: "{app}\android\install-to-phone.bat"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Install VB-CABLE Driver"; Filename: "{app}\drivers\vbcable\VBCABLE_Setup_x64.exe"
Name: "{group}\Check out Fetch Remote (GitHub)"; Filename: "https://github.com/Tr1pAc1d/Fetch"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; IconFilename: "{app}\{#MyAppExeName}"

[Run]
; Install VB-CABLE driver during setup if selected
Filename: "{app}\drivers\vbcable\VBCABLE_Setup_x64.exe"; Parameters: "-i"; StatusMsg: "Installing VB-CABLE virtual audio driver..."; Flags: runascurrentuser waituntilterminated; Tasks: installvbcable
; Launch Wireless Bark
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Check out Fetch by TAB inc.
Filename: "https://github.com/Tr1pAc1d/Fetch"; Description: "Check out Fetch - wireless remote touchpad & keyboard for PC by TAB inc."; Flags: postinstall shellexec skipifsilent unchecked

[Messages]
FinishedHeadingLabel=Completing the Wireless Bark Setup Wizard
ClickFinish=Click Finish to exit Setup.%n%nStraightforward, tracker-free software by TAB inc.
