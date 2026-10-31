type
  TUpdateFileId = record
    VolumeLow, VolumeHigh, Id0, Id1, Id2, Id3: LongWord;
  end;
function UpdateCreateFile(Name: String; Access, Share: LongWord; Security: Integer;
  Creation, Flags: LongWord; Template: Integer): Integer;
  external 'CreateFileW@kernel32.dll stdcall';
function UpdateGetFileId(Handle: Integer; InformationClass: Integer;
  var Information: TUpdateFileId; Size: LongWord): Boolean;
  external 'GetFileInformationByHandleEx@kernel32.dll stdcall';
function UpdateCloseHandle(Handle: Integer): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function UpdateSetDisposition(Handle, InformationClass: Integer;
  var Disposition: Byte; Size: LongWord): Boolean;
  external 'SetFileInformationByHandle@kernel32.dll stdcall';
function UpdateGetSize(Handle: Integer; var Size: Int64): Boolean;
  external 'GetFileSizeEx@kernel32.dll stdcall';
function UpdateMapUppercase(Locale, Flags: LongWord; Source: String; SourceLength: Integer;
  Destination: String; DestinationLength: Integer): Integer;
  external 'LCMapStringW@kernel32.dll stdcall';
function UpdateUppercase(Value: String): String;
var Count: Integer;
begin
  Result := StringOfChar(#0, Length(Value) * 2 + 2);
  Count := UpdateMapUppercase($7F, $200, Value, Length(Value), Result, Length(Result));
  if Count = 0 then RaiseException('Cannot normalize update root.');
  SetLength(Result, Count);
end;
function UpdateNativePath(Name: String): String;
begin
  Result := '\\?\' + ExpandFileName(Name);
end;
var
  UpdateDirectory: String;
  UpdateOriginals: TStringList;
  UpdateProofNames: TStringList;
  UpdateFailed: Boolean;
function UpdateWordHex(Value: LongWord): String;
var I: Integer; Digits: String;
begin
  Digits := '0123456789ABCDEF'; Result := '';
  for I := 0 to 3 do begin
    Result := Result + Copy(Digits, ((Value shr 4) and $F) + 1, 1) + Copy(Digits, (Value and $F) + 1, 1);
    Value := Value shr 8;
  end;
end;
function UpdateHandleIdentity(Handle: Integer): String;
var Info: TUpdateFileId;
begin
  if not UpdateGetFileId(Handle, 18, Info, 24) then RaiseException('Cannot read update file identity.');
  Result := UpdateWordHex(Info.VolumeLow) + UpdateWordHex(Info.VolumeHigh) +
    UpdateWordHex(Info.Id0) + UpdateWordHex(Info.Id1) + UpdateWordHex(Info.Id2) + UpdateWordHex(Info.Id3);
end;
function UpdateIdentity(Name: String): String;
var Handle: Integer;
begin
  Handle := UpdateCreateFile(UpdateNativePath(Name), $80000000, 7, 0, 3, $200080, 0);
  if Handle = -1 then RaiseException('Cannot open update file identity.');
  try
    Result := UpdateHandleIdentity(Handle);
  finally UpdateCloseHandle(Handle); end;
end;
procedure PrepareUpdateJournal;
var Expected, RecordText: String; Entry: TFindRec; Saved: AnsiString; Count, Divider: Integer;
begin
  if UpdateOriginals <> nil then Exit;
  if UpdateDirectory = '' then UpdateDirectory := ExpandConstant('{param:UPDATEJOURNAL|}');
  if UpdateDirectory = '' then Exit;
  Expected := AddBackslash(ExtractFileDir(RemoveBackslashUnlessRoot(WizardDirValue))) + '.DanmuCompanion-updates';
  if (CompareText(RemoveBackslashUnlessRoot(ExtractFileDir(UpdateDirectory)), Expected) <> 0) or
    not FileExists(AddBackslash(UpdateDirectory) + 'owner.json') or
    not FileExists(AddBackslash(UpdateDirectory) + 'journal.json') then RaiseException('Invalid update journal location.');
  UpdateOriginals := TStringList.Create;
  if not LoadStringFromFile(AddBackslash(UpdateDirectory) + 'journal.json', Saved) or
    (Length(Saved) > 4096) or (Pos('"schemaVersion":2', UTF8Decode(Saved)) = 0) or
    (Pos('"kind":"direct-overwrite"', UTF8Decode(Saved)) = 0) then RaiseException('Obsolete update journal; no old-version recovery is supported.');
  UpdateProofNames := TStringList.Create; UpdateProofNames.Sorted := True; Count := 0;
  if FindFirst(AddBackslash(UpdateDirectory) + '*.file-id', Entry) then try
    repeat
      Count := Count + 1; if Count > 32770 then RaiseException('Too many update file records.');
      if (Entry.Attributes and $410) = 0 then
        if LoadStringFromFile(AddBackslash(UpdateDirectory) + Entry.Name, Saved) and (Length(Saved) <= 2048) then begin
          RecordText := UTF8Decode(Saved); Divider := Pos('|', RecordText);
          if (Divider > 1) and (Length(RecordText) - Divider = 48) then begin
            UpdateProofNames.Add(RecordText);
          end;
        end;
    until not FindNext(Entry);
  finally FindClose(Entry); end;
end;
function UpdateOwnsNewFile(Relative: String): Boolean;
var Target: String;
begin
  Result := False;
  if UpdateDirectory = '' then Exit;
  Target := AddBackslash(WizardDirValue) + Relative;
  if not FileExists(Target) then Exit;
  Result := UpdateProofNames.IndexOf(Relative + '|' + UpdateIdentity(Target)) >= 0;
end;
procedure ReloadUpdateJournal(Directory: String);
begin
  if UpdateOriginals <> nil then UpdateOriginals.Free;
  if UpdateProofNames <> nil then UpdateProofNames.Free;
  UpdateOriginals := nil; UpdateProofNames := nil;
  UpdateDirectory := Directory; PrepareUpdateJournal;
end;
#include "DirectRepair.iss"
function AdmitUpdateFile: Boolean;
var Target, Relative: String;
begin
  Result := True;
  Target := ExpandConstant(CurrentFileName);
  Relative := Copy(Target, Length(AddBackslash(WizardDirValue)) + 1, MaxInt);
  if UpdateDirectory = '' then begin
    // Inno also evaluates Check during its pre-install Restart Manager scan.
    // The native writer runs later in PrepareToInstall; Inno never copies payload.
    Result := False; Exit;
  end;
  // The native worker already committed these bytes with non-overwriting semantics.
  // Inno must never overwrite a newly introduced path, even after a late replacement.
  Result := False;
  try
    if not UpdateOwnsNewFile(Relative) then begin UpdateFailed := True; Log('New update file identity changed; not overwritten.'); end;
  except UpdateFailed := True; Log('New update file identity could not be verified.'); end;
end;
function GetCustomSetupExitCode: Integer;
begin
  if UpdateFailed then Result := 1 else Result := 0;
end;
function UpdateDeleteMatching(Name, Identity: String): Boolean;
var Handle: Integer; Disposition: Byte;
begin
  Result := False;
  if PathComponentProblem(Name) <> '' then Exit;
  Handle := UpdateCreateFile(UpdateNativePath(Name), $10080, 3, 0, 3, $200000, 0);
  if Handle = -1 then Exit;
  try
    if CompareText(UpdateHandleIdentity(Handle), Identity) = 0 then begin
      Disposition := 1; Result := UpdateSetDisposition(Handle, 4, Disposition, 1);
    end;
  finally UpdateCloseHandle(Handle); end;
end;
procedure UpdateDeleteOwnedFiles(Allowed: TStringList);
var Root, Sidecar, Identity, Relative, Target: String;
  Records, Names, Ids: TStringList; I, J, Divider, Handle: Integer; Size: Int64;
  Saved: AnsiString; Remaining: Boolean;
begin
  Root := UpdateUppercase(RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}'))));
  Sidecar := AddBackslash(ExtractFileDir(Root)) + '.DanmuCompanion-updates\Owned-Danmu-' +
    Uppercase(Copy(GetSHA256OfUnicodeString(Root), 1, 12)) + '.txt';
  Log('Native-owned uninstall registration: ' + Sidecar);
  if not FileExists(Sidecar) or (PathComponentProblem(Sidecar) <> '') then Exit;
  Records := TStringList.Create; Names := TStringList.Create; Ids := TStringList.Create;
  try
   try
    Handle := UpdateCreateFile(UpdateNativePath(Sidecar), $80000000, 1, 0, 3, $200000, 0);
    if Handle = -1 then Exit;
    try
      if not UpdateGetSize(Handle, Size) or (Size > 4194304) then Exit;
      Identity := UpdateHandleIdentity(Handle);
      if not LoadStringFromFile(Sidecar, Saved) then Exit;
      Records.Text := UTF8Decode(Saved);
    finally UpdateCloseHandle(Handle); end;
    if (Records.Count < 1) or (Records.Count > 16386) then Exit;
    if CompareText(Records[0], 'github-updater-owned-v1|' + Root) <> 0 then Exit;
    for I := 1 to Records.Count - 1 do begin
      if Records[I] <> '' then begin
        Divider := Pos('|', Records[I]);
        if (Divider <= 1) or (Length(Records[I]) - Divider <> 48) then Exit;
        Relative := Copy(Records[I], 1, Divider - 1);
        if (Allowed.IndexOf(Relative) < 0) or (Names.IndexOf(Relative) >= 0) then Exit;
        Target := Copy(Records[I], Divider + 1, 48);
        for J := 1 to 48 do if Pos(Uppercase(Copy(Target, J, 1)), '0123456789ABCDEF') = 0 then Exit;
        Names.Add(Relative); Ids.Add(Target);
      end;
    end;
    // Only compiled payload paths with unchanged disk identity are removed. No recursion.
    Remaining := False;
    for I := 0 to Names.Count - 1 do begin
      Target := AddBackslash(Root) + Names[I];
      if FileExists(Target) and not UpdateDeleteMatching(Target, Ids[I]) then begin
        Remaining := True; Log('Preserved replaced or locked native-owned file: ' + Names[I]);
      end;
    end;
    if not Remaining then UpdateDeleteMatching(Sidecar, Identity)
    else if not UninstallSilent then MsgBox('部分更新文件已被替换或占用，未删除。身份登记已保留在应用目录旁的 .DanmuCompanion-updates 中，请检查后清理。', mbInformation, MB_OK);
   except Log('Preserved invalid native-owned uninstall registration: ' + GetExceptionMessage); end;
  finally Records.Free; Names.Free; Ids.Free; end;
end;
