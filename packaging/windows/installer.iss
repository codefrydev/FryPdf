; Inno Setup 6 Script for FryPDF by Code Fry Dev
#ifndef MyAppVersion
#define MyAppVersion "0.2.0"
#endif

; Four-part numeric version for the setup.exe version resource. VersionInfoVersion
; rejects anything that is not up to four dotted numbers, so a prerelease tag like
; "0.0.7-beta" cannot be reused here. release.yml passes its four_part_version.
#ifndef MyAppVersionNumeric
#define MyAppVersionNumeric "0.0.0.0"
#endif

#ifndef MyPublishDir
#define MyPublishDir "..\..\publish\win-x64"
#endif

#define MyAppName "FryPDF"
#define MyAppPublisher "Code Fry Dev"
#define MyAppCopyright "Copyright (C) 2026 Code Fry Dev"
#define MyAppURL "https://codefrydev.in"
#define MyAppSupportURL "mailto:codefrydev@gmail.com"
#define MyAppExeName "FryPDF.exe"
; Superseded by FryPDF.exe in 0.0.6+; removed on upgrade via [InstallDelete].
#define MyLegacyAppExeName "PdfEditorApp.exe"

[Setup]
; Basic Application Info
AppId={{D37E88A1-1B2F-4A92-875D-876E842109AB}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppSupportURL}
AppUpdatesURL={#MyAppURL}
AppCopyright={#MyAppCopyright}

; Add/Remove Programs. Without UninstallDisplayName the entry inherits AppVerName
; and reads "FryPDF 1.2.3" instead of the product name on its own.
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName},0

; Destination Directories
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Output Configuration
OutputDir=.
OutputBaseFilename=FryPDF-Setup-{#MyAppVersion}
SetupIconFile=..\..\src\PdfEditorApp\Assets\app-logo.ico
LicenseFile=..\..\LICENSE

; Wizard Branding. Inno picks whichever supplied file is closest to the size it
; needs for the current DPI, so these are globs rather than single files.
WizardStyle=modern
WizardImageFile=branding\wizard-large-*.png
WizardSmallImageFile=branding\wizard-small-*.png

; setup.exe's own file properties (Explorer > Properties, and the UAC prompt)
VersionInfoVersion={#MyAppVersionNumeric}
VersionInfoProductVersion={#MyAppVersionNumeric}
VersionInfoTextVersion={#MyAppVersion}
VersionInfoProductTextVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright={#MyAppCopyright}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoOriginalFileName=FryPDF-Setup.exe

; Compression & Behaviour
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
SetupMutex=FryPDFSetupMutex
CloseApplications=yes
RestartApplications=no

; File Associations
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; SetupAppTitle drives the installer's taskbar button, which otherwise reads "Setup".
[Messages]
SetupAppTitle={#MyAppName} Setup
SetupWindowTitle={#MyAppName} Setup
UninstallAppTitle={#MyAppName} Uninstall
UninstallAppFullTitle={#MyAppName} Uninstall

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "fileassoc_proj"; Description: "Associate with FryPDF project files (.frypdf)"; GroupDescription: "File Associations:"
Name: "fileassoc_pdf"; Description: "Associate with Adobe / standard PDF documents (.pdf)"; GroupDescription: "File Associations:"; Flags: unchecked

; Inno never removes files that are absent from [Files], so an upgrade from a
; pre-rename install would otherwise leave both launchers side by side. The
; desktop shortcut goes too: it points at the launcher being deleted, and it is
; only recreated when the (unchecked) desktopicon task is selected, so leaving
; it in place would leave a dead link. The Start menu entry needs no entry here
; because [Icons] always rewrites it.
[InstallDelete]
Type: files; Name: "{app}\{#MyLegacyAppExeName}"
Type: files; Name: "{autodesktop}\{#MyAppName}.lnk"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Open-with list. Without FriendlyAppName the shell falls back to the exe's
; FileDescription, which works but is not guaranteed.
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey

; .frypdf Association
Root: HKA; Subkey: "Software\Classes\.frypdf"; ValueType: string; ValueName: ""; ValueData: "FryPDF.Project"; Flags: uninsdeletevalue; Tasks: fileassoc_proj
; Legacy .pdfproj Association
Root: HKA; Subkey: "Software\Classes\.pdfproj"; ValueType: string; ValueName: ""; ValueData: "FryPDF.Project"; Flags: uninsdeletevalue; Tasks: fileassoc_proj
Root: HKA; Subkey: "Software\Classes\FryPDF.Project"; ValueType: string; ValueName: ""; ValueData: "FryPDF Project File"; Flags: uninsdeletekey; Tasks: fileassoc_proj
Root: HKA; Subkey: "Software\Classes\FryPDF.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Flags: uninsdeletekey; Tasks: fileassoc_proj
Root: HKA; Subkey: "Software\Classes\FryPDF.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: fileassoc_proj

; .pdf Association (optional)
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "FryPDF.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc_pdf
Root: HKA; Subkey: "Software\Classes\FryPDF.Document"; ValueType: string; ValueName: ""; ValueData: "PDF Document"; Flags: uninsdeletekey; Tasks: fileassoc_pdf
Root: HKA; Subkey: "Software\Classes\FryPDF.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Flags: uninsdeletekey; Tasks: fileassoc_pdf
Root: HKA; Subkey: "Software\Classes\FryPDF.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: fileassoc_pdf

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
