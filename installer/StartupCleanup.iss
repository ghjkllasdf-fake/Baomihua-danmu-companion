procedure RemoveStartupShortcut(ApplicationRoot: String);
var Name, Target: String; Shell, Shortcut: Variant;
begin
  Name := ExpandConstant('{userstartup}\爆米花弹幕伴侣.lnk');
  if not FileExists(Name) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(Name);
    Target := ExpandFileName(Shortcut.TargetPath);
    if CompareText(Target, AddBackslash(ExpandFileName(ApplicationRoot)) + 'DanmuCompanion.exe') = 0 then
      if not DeleteFile(Name) then Log('Startup shortcut could not be removed; close the process using it and remove it manually.');
  except
    Log('Startup shortcut could not be read; the application uninstall continues.');
  end;
end;
