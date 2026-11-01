#ifndef DllSource
  #define DllSource "FPSGridGenerator.dll"
#endif
#ifndef ManifestSource
  #define ManifestSource "PackageContents.xml"
#endif

[Setup]
AppName=AutoCAD FPS Grid Generator
AppVersion=1.0.0
DefaultDirName={commonappdata}\Autodesk\ApplicationPlugins\FPSGrid.bundle
DisableDirPage=yes
OutputBaseFilename=FPS_Grid_Generator_Setup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin

[Files]
Source: "{#ManifestSource}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DllSource}"; DestDir: "{app}\Contents"; Flags: ignoreversion

[Messages]
FinishedLabel=Installation Complete! The FPS Grid Generator will automatically appear in your AutoCAD Ribbon tab and commands (GENGRID, EDITGRID) will be active.
