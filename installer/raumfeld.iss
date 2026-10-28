#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{B6A3E62A-A86A-452E-9A76-68EAC2533388}
AppName=Raumfeld Desktop Stream
AppVersion={#AppVersion}
AppPublisher=Raumfeld
DefaultDirName={localappdata}\Programs\Raumfeld Desktop Stream
DefaultGroupName=Raumfeld
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\installer
OutputBaseFilename=Raumfeld-Setup
SetupIconFile=..\src\Raumfeld.Tray\Assets\raumfeld.ico
UninstallDisplayIcon={app}\Raumfeld.Tray.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Tasks]
Name: startup; Description: "Open Raumfeld in the tray when I sign in to Windows"; Flags: checkedonce

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Raumfeld"; Filename: "{app}\Raumfeld.Tray.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "RaumklangDesktopStream"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RaumfeldDesktopStream"; ValueData: """{app}\Raumfeld.Tray.exe"" --hidden"; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "RaumfeldDesktopStream"; Tasks: not startup; Flags: deletevalue

[InstallDelete]
Type: files; Name: "{userprograms}\Raumklang.lnk"

[Run]
Filename: "{app}\Raumfeld.Tray.exe"; Description: "Open Raumfeld"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\Raumfeld.DesktopStream.exe"; Parameters: "stop"; Flags: runhidden waituntilterminated; RunOnceId: "StopStream"

; Configuration and logs in LocalAppData are deliberately retained.
