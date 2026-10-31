var ResourceSnapshot: String;

function ResourceMetadataBounded(Name: String): Boolean;
var Handle: Integer; Size: Int64;
begin
  Result := False; if PathComponentProblem(Name) <> '' then Exit;
  Handle := UpdateCreateFile(UpdateNativePath(Name), $80000000, 1, 0, 3, $200000, 0);
  if Handle = -1 then Exit;
  try Result := UpdateGetSize(Handle, Size) and (Size >= 0) and (Size <= 4194304);
  finally UpdateCloseHandle(Handle); end;
end;

function IsRetiredResource(Relative: String): Boolean;
var Divider, I: Integer; Culture, Name: String;
begin
  Result := False; Divider := Pos('\', Relative);
  if Divider <= 1 then Exit;
  Culture := Copy(Relative, 1, Divider - 1);
  if (CompareText(Culture, 'en') = 0) or (CompareText(Culture, 'zh-Hans') = 0) or (CompareText(Culture, 'zh-Hant') = 0) then Exit;
  if not ((Length(Culture) = 2) or ((Length(Culture) > 3) and (Copy(Culture, 3, 1) = '-'))) then Exit;
  for I := 1 to Length(Culture) do if Pos(Copy(Culture, I, 1), 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-') = 0 then Exit;
  Name := Copy(Relative, Divider + 1, MaxInt);
  if (Length(Name) <= Length('.resources.dll')) or (CompareText(Copy(Name, Length(Name) - Length('.resources.dll') + 1, MaxInt), '.resources.dll') <> 0) then Exit;
  for I := 1 to Length(Name) do if Pos(Copy(Name, I, 1), 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.-') = 0 then Exit;
  Result := True;
end;

function CaptureRetiredResources(Root, Snapshot: String; Owned, Incoming: TStringList): Boolean;
var Names: TStringList; I: Integer; Relative, Target, Records: String;
begin
  Result := False;
  if (Owned.Count > 16384) or (PathComponentProblem(Root) <> '') or not ResourceMetadataBounded(AddBackslash(Root) + 'manifest.json') then Exit;
  Names := TStringList.Create;
  try
    Records := 'retired-resources-v1|' + RemoveBackslashUnlessRoot(ExpandFileName(Root)) + #13#10;
    for I := 0 to Owned.Count - 1 do begin
      Relative := Owned[I];
      if IsRetiredResource(Relative) and (Incoming.IndexOf(Relative) < 0) and (Names.IndexOf(Relative) < 0) then begin
        Target := AddBackslash(Root) + Relative;
        if FileExists(Target) and (PathComponentProblem(Target) = '') then begin
          Names.Add(Relative); Records := Records + Relative + '|' + UpdateIdentity(Target) + #13#10;
        end;
      end;
    end;
    if (Names.Count = 0) or DirExists(Snapshot) then Exit;
    if not CreateDir(Snapshot) then Exit;
    if not FileCopy(AddBackslash(Root) + 'manifest.json', AddBackslash(Snapshot) + 'old-manifest.json', True) then Exit;
    Result := SaveStringToFile(AddBackslash(Snapshot) + 'scope.txt', UTF8Encode(Records), False);
  finally Names.Free; end;
end;

procedure PrepareResourceRetirement;
var Registered, Root, Snapshot: String; Owned, Incoming: TStringList;
begin
  if (ResourceSnapshot <> '') or (UpdateDirectory <> '') then Exit;
  Root := RemoveBackslashUnlessRoot(ExpandFileName(WizardDirValue));
  if not RegQueryStringValue(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#UninstallKey}', 'InstallLocation', Registered) then Exit;
  if CompareText(Root, RemoveBackslashUnlessRoot(ExpandFileName(Registered))) <> 0 then Exit;
  if not ResourceMetadataBounded(AddBackslash(Root) + 'install-files.txt') then Exit;
  Owned := TStringList.Create; Incoming := TStringList.Create;
  try
    Owned.LoadFromFile(AddBackslash(Root) + 'install-files.txt'); AddIncomingPayloadFiles(Incoming);
    Snapshot := AddBackslash(ExpandConstant('{tmp}')) + 'resource-retirement';
    if CaptureRetiredResources(Root, Snapshot, Owned, Incoming) then ResourceSnapshot := Snapshot;
  finally Owned.Free; Incoming.Free; end;
end;

procedure FinishResourceRetirement;
var ExitCode: Integer;
begin
  if ResourceSnapshot = '' then Exit;
  // The current native updater verifies the old manifest and captured disk identities.
  if not Exec(ExpandConstant('{app}\DanmuCompanion.Updater.exe'), '--retire-resources ' + AddQuotes(ResourceSnapshot), '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
    Log('Some old language resources were preserved; the new installation remains usable.');
end;
