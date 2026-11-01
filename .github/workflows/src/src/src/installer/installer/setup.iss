[Setup]
AppName=AutoCAD FPS Grid Generator
AppVersion=1.0.0
DefaultDirName={commonappdata}\Autodesk\ApplicationPlugins\FPSGrid.bundle
DisableDirPage=yes
OutputBaseFilename=FPS_Grid_Generator_Setup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin
OutputDir=..\bin

[Files]
Source: "PackageContents.xml"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\bin\Release\FPSGridGenerator.dll"; DestDir: "{app}\Contents"; Flags: ignoreversion

[Messages]
FinishedLabel=Installation Complete! The FPS Grid Generator will automatically appear in your AutoCAD Ribbon tab and commands (GENGRID, EDITGRID) will be active.
