#ifndef StartupShortcutName
  #define StartupShortcutName "{userstartup}\爆米花弹幕伴侣.lnk"
#endif
type
  TUpdateAttributes = record
    Attributes, Tag: LongWord;
  end;
  TUpdateFileId = record
    VolumeLow, VolumeHigh, Id0, Id1, Id2, Id3: LongWord;
  end;
function UpdateCreateFile(Name: String; Access, Share: LongWord; Security: Integer;
  Creation, Flags: LongWord; Template: Integer): Integer;
  external 'CreateFileW@kernel32.dll stdcall';
function UpdateGetFileId(Handle: Integer; InformationClass: Integer;
  var Information: TUpdateFileId; Size: LongWord): Boolean;
  external 'GetFileInformationByHandleEx@kernel32.dll stdcall';
function UpdateGetAttributes(Handle, InformationClass: Integer;
  var Information: TUpdateAttributes; Size: LongWord): Boolean;
  external 'GetFileInformationByHandleEx@kernel32.dll stdcall';
function UpdateCloseHandle(Handle: Integer): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function UpdateSetDisposition(Handle, InformationClass: Integer;
  var Disposition: Byte; Size: LongWord): Boolean;
  external 'SetFileInformationByHandle@kernel32.dll stdcall';
function UpdateGetSize(Handle: Integer; var Size: Int64): Boolean;
  external 'GetFileSizeEx@kernel32.dll stdcall';
function UpdateReadBytes(Handle: Integer; Buffer: AnsiString; Count: LongWord;
  var ReadCount: LongWord; Overlapped: Integer): Boolean;
  external 'ReadFile@kernel32.dll stdcall';
function UpdateCreateMutex(Security: Integer; Owned: Boolean; Name: String): Integer;
  external 'CreateMutexW@kernel32.dll stdcall';
function UpdateWait(Handle: Integer; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function UpdateReleaseMutex(Handle: Integer): Boolean;
  external 'ReleaseMutex@kernel32.dll stdcall';
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
  Result := GuardNativePath(Name);
end;
function UpdateHandleIdentity(Handle: Integer): String; forward;
procedure AddIncomingPayloadFiles(Files: TStringList); forward;
function ValidateUninstallOwnership(Allowed: TStringList): Boolean; forward;
var
  UpdateDirectory: String;
  UpdateOriginals: TStringList;
  UpdateProofNames: TStringList;
  UpdateFailed: Boolean;
  UninstallSidecar, UninstallMarker, UninstallRoot, UninstallMutex: Integer;
  UninstallMutexOwned, NativeUninstallTrusted: Boolean;
  UninstallSidecarBytes, UninstallMarkerBytes: AnsiString;
  UninstallDirectoryHandles: array of Integer;
  UninstallNames, UninstallIdentities: TStringList;
  UninstallDirectoryNames: array of String;
function ReadUninstallHandle(Handle: Integer; var Bytes: AnsiString): Boolean;
var Size: Int64; Count: LongWord;
begin
  Result := False;
  if not UpdateGetSize(Handle, Size) or (Size < 0) or (Size > 4194304) then Exit;
  SetLength(Bytes, Size); Count := 0;
  Result := (Size = 0) or (UpdateReadBytes(Handle, Bytes, Size, Count, 0) and (Count = Size));
end;
procedure CloseUninstallGuards;
var I: Integer;
begin
  for I := GetArrayLength(UninstallDirectoryHandles) - 1 downto 0 do
    if UninstallDirectoryHandles[I] <> -1 then UpdateCloseHandle(UninstallDirectoryHandles[I]);
  SetArrayLength(UninstallDirectoryHandles, 0);
  SetArrayLength(UninstallDirectoryNames, 0);
  if UninstallNames <> nil then begin UninstallNames.Free; UninstallNames := nil; end;
  if UninstallIdentities <> nil then begin UninstallIdentities.Free; UninstallIdentities := nil; end;
  if UninstallMarker <> -1 then UpdateCloseHandle(UninstallMarker);
  if UninstallSidecar <> -1 then UpdateCloseHandle(UninstallSidecar);
  if UninstallRoot <> -1 then UpdateCloseHandle(UninstallRoot);
  if UninstallMutex <> -1 then begin
    if UninstallMutexOwned then UpdateReleaseMutex(UninstallMutex);
    UpdateCloseHandle(UninstallMutex);
  end;
  UninstallMarker := -1; UninstallSidecar := -1; UninstallRoot := -1; UninstallMutex := -1;
  UninstallMutexOwned := False;
end;
function TrustedNativeUninstall: Boolean;
begin Result := NativeUninstallTrusted; end;
function InitializeUninstall: Boolean;
var Root, Sidecar, Marker: String; Allowed: TStringList;
  Info: TUpdateAttributes; WaitResult: LongWord;
begin
  Result := False; NativeUninstallTrusted := False;
  UninstallSidecar := -1; UninstallMarker := -1; UninstallRoot := -1; UninstallMutex := -1;
  Root := UpdateUppercase(RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}'))));
  UninstallMutex := UpdateCreateMutex(0, False, 'Local\DanmuCompanion-root-' + Uppercase(GetSHA256OfString(UTF8Encode(Root))));
  if UninstallMutex = 0 then begin UninstallMutex := -1; Exit; end;
  WaitResult := UpdateWait(UninstallMutex, 0); UninstallMutexOwned := (WaitResult = 0) or (WaitResult = $80);
  if not UninstallMutexOwned then begin
    if not UninstallSilent then MsgBox('伴侣正在运行或更新，请先从托盘退出，再重试卸载。', mbError, MB_OK);
    CloseUninstallGuards; Exit;
  end;
  Sidecar := AddBackslash(ExtractFileDir(Root)) + '.DanmuCompanion-updates\Owned-Danmu-' + Uppercase(Copy(GetSHA256OfUnicodeString(Root), 1, 12)) + '.txt';
  Marker := AddBackslash(Root) + '.DanmuCompanion-registration.txt';
  if not GuardFileExists(Sidecar) or not GuardFileExists(Marker) or
    (PathComponentProblem(Sidecar) <> '') or (PathComponentProblem(Marker) <> '') then begin
    Log('Ownership unavailable: uninstall removes registration only; all payload/foreign files are retained. Reinstall into a new empty directory.');
    if not UninstallSilent then MsgBox('文件身份登记缺失或无效。本次仅移除安装登记，程序和外来文件均保留；请保留原目录，并在新的空目录安装。', mbInformation, MB_OK);
    Result := True; Exit;
  end;
  UninstallRoot := UpdateCreateFile(UpdateNativePath(Root), $81, 3, 0, 3, $2200000, 0);
  UninstallSidecar := UpdateCreateFile(UpdateNativePath(Sidecar), $80010000, 1, 0, 3, $200000, 0);
  UninstallMarker := UpdateCreateFile(UpdateNativePath(Marker), $80010000, 1, 0, 3, $200000, 0);
  if (UninstallRoot = -1) or (UninstallSidecar = -1) or (UninstallMarker = -1) or
    not UpdateGetAttributes(UninstallMarker, 9, Info, 8) or ((Info.Attributes and $411) <> 0) or
    not UpdateGetAttributes(UninstallSidecar, 9, Info, 8) or ((Info.Attributes and $411) <> 0) or
    not ReadUninstallHandle(UninstallSidecar, UninstallSidecarBytes) or not ReadUninstallHandle(UninstallMarker, UninstallMarkerBytes) then begin
    Log('Ownership metadata is locked/read-only; uninstall did not start.');
    if not UninstallSilent then MsgBox('身份登记文件被占用、只读或无法读取，卸载未开始。请释放占用后重试。', mbError, MB_OK);
    CloseUninstallGuards; Exit;
  end;
  Allowed := TStringList.Create;
  try
    AddIncomingPayloadFiles(Allowed);
    try NativeUninstallTrusted := ValidateUninstallOwnership(Allowed);
    except
      Log('Uninstall preflight failed: ' + GetExceptionMessage);
      if not UninstallSilent then MsgBox('程序目录或身份登记被占用，卸载未开始。请释放占用后重试。', mbError, MB_OK);
      CloseUninstallGuards; Exit;
    end;
  finally Allowed.Free; end;
  if not NativeUninstallTrusted then begin
    Log('Ownership invalid: registration-only uninstall; payload and metadata are preserved.');
    if not UninstallSilent then MsgBox('文件身份登记无效。本次仅移除安装登记，程序文件均保留；请保留原目录，并在新的空目录安装。', mbInformation, MB_OK);
  end;
  Result := True;
end;
procedure DeinitializeUninstall;
begin CloseUninstallGuards; end;
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
    not GuardFileExists(AddBackslash(UpdateDirectory) + 'owner.json') or
    not GuardFileExists(AddBackslash(UpdateDirectory) + 'journal.json') then RaiseException('Invalid update journal location.');
  UpdateOriginals := TStringList.Create;
  if not LoadStringFromFile(GuardNativePath(AddBackslash(UpdateDirectory) + 'journal.json'), Saved) or
    (Length(Saved) > 4096) or (Pos('"schemaVersion":2', UTF8Decode(Saved)) = 0) or
    (Pos('"kind":"direct-overwrite"', UTF8Decode(Saved)) = 0) then RaiseException('Obsolete update journal; no old-version recovery is supported.');
  UpdateProofNames := TStringList.Create; UpdateProofNames.Sorted := True; Count := 0;
  if FindFirst(GuardNativePath(AddBackslash(UpdateDirectory) + '*.file-id'), Entry) then try
    repeat
      Count := Count + 1; if Count > 32770 then RaiseException('Too many update file records.');
      if (Entry.Attributes and $410) = 0 then
        if LoadStringFromFile(GuardNativePath(AddBackslash(UpdateDirectory) + Entry.Name), Saved) and (Length(Saved) <= 2048) then begin
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
  if not GuardFileExists(Target) then Exit;
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
function UpdateDeleteMatching(Name, Identity, ExpectedHash: String): Boolean;
var Handle: Integer; Disposition: Byte; Bytes: AnsiString;
begin
  Result := False;
  if PathComponentProblem(Name) <> '' then Exit;
  if ExpectedHash <> '' then Handle := UpdateCreateFile(UpdateNativePath(Name), $80010080, 1, 0, 3, $200000, 0)
  else Handle := UpdateCreateFile(UpdateNativePath(Name), $10080, 3, 0, 3, $200000, 0);
  if Handle = -1 then Exit;
  try
    if CompareText(UpdateHandleIdentity(Handle), Identity) = 0 then begin
      if ExpectedHash <> '' then
        if not ReadUninstallHandle(Handle, Bytes) or (CompareText(GetSHA256OfString(Bytes), ExpectedHash) <> 0) then Exit;
      Disposition := 1; Result := UpdateSetDisposition(Handle, 4, Disposition, 1);
    end;
  finally UpdateCloseHandle(Handle); end;
end;
procedure NoticePreservedStartup;
begin
  Log('Preserved unconfirmed, changed, replaced or locked startup shortcut.');
  if not UninstallSilent then MsgBox('同名开机启动项无法安全确认或删除，已保留。请检查当前用户的启动文件夹，以免卸载后仍自动启动；不要删除其他程序或副本的启动项。', mbInformation, MB_OK);
end;
procedure RemoveOwnedStartupShortcut;
var Name, Root, Identity, Hash: String; Shell, Link: Variant; Handle: Integer; Bytes: AnsiString; Attributes, Error: LongWord;
begin
  // Never execute installed payload during uninstall; it may have been replaced.
  Name := ExpandConstant('{#StartupShortcutName}');
  Attributes := PathAttributes(Name);
  if Attributes = $FFFFFFFF then begin
    Error := DLLGetLastError;
    if (Error <> 2) and (Error <> 3) then NoticePreservedStartup;
    Exit;
  end;
  if ((Attributes and $410) <> 0) or (PathComponentProblem(Name) <> '') then begin NoticePreservedStartup; Exit; end;
  Root := RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}')));
  Handle := UpdateCreateFile(UpdateNativePath(Name), $80000000, 1, 0, 3, $200000, 0);
  if Handle = -1 then begin NoticePreservedStartup; Exit; end;
  Identity := ''; Hash := '';
  try
    try
      Shell := CreateOleObject('WScript.Shell'); Link := Shell.CreateShortcut(Name);
      if (CompareText(Link.TargetPath, AddBackslash(Root) + 'DanmuCompanion.exe') = 0) and
        (CompareText(RemoveBackslashUnlessRoot(Link.WorkingDirectory), Root) = 0) and
        (Link.Arguments = '') and (Link.Description = '爆米花弹幕伴侣（后台托盘）') then
        if ReadUninstallHandle(Handle, Bytes) then begin Identity := UpdateHandleIdentity(Handle); Hash := GetSHA256OfString(Bytes); end;
      Link := Null; Shell := Null;
    except Log('Preserved unreadable startup shortcut: ' + GetExceptionMessage); end;
  finally UpdateCloseHandle(Handle); end;
#ifdef StartupDeleteHook
  {#StartupDeleteHook};
#endif
  if (Identity = '') or not UpdateDeleteMatching(Name, Identity, Hash) then NoticePreservedStartup;
end;
function ValidateUninstallOwnership(Allowed: TStringList): Boolean;
var Root, Relative, Target, Parent, Row, Identity, ManifestHash: String;
  Records, MarkerRecords, AllowedDirs, DirectoryRows, DirectoryIds, Names, Ids: TStringList;
  I, J, Divider, Handle, Count, Index: Integer; Saved: AnsiString; Info: TUpdateAttributes;
begin
  Result := False;
  Root := UpdateUppercase(RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}'))));
  Records := TStringList.Create; MarkerRecords := TStringList.Create; AllowedDirs := TStringList.Create;
  DirectoryRows := TStringList.Create; DirectoryIds := TStringList.Create; Names := TStringList.Create; Ids := TStringList.Create;
  AllowedDirs.Sorted := True;
  try
    Records.Text := UTF8Decode(UninstallSidecarBytes); MarkerRecords.Text := UTF8Decode(UninstallMarkerBytes);
    if (Records.Count <> Allowed.Count + 1) or (Records.Count > 16386) or
      (CompareText(Records[0], 'github-updater-owned-v1|' + Root) <> 0) then Exit;
    if not UpdateGetAttributes(UninstallRoot, 9, Info, 8) or ((Info.Attributes and $410) <> $10) then Exit;
    if (MarkerRecords.Count < 10) or (MarkerRecords.Count > 32778) or
      (MarkerRecords[0] <> 'github-initial-registration-v1') or (CompareText(MarkerRecords[1], Root) <> 0) or
      (CompareText(MarkerRecords[2], UpdateHandleIdentity(UninstallRoot)) <> 0) or
      (MarkerRecords[4] <> '{#AppVersion}') or (MarkerRecords[6] <> 'registered') or
      (CompareText(MarkerRecords[7], UpdateHandleIdentity(UninstallMarker)) <> 0) or
      (CompareText(MarkerRecords[8], UpdateHandleIdentity(UninstallSidecar)) <> 0) or
      (CompareText(MarkerRecords[9], GetSHA256OfString(UninstallSidecarBytes)) <> 0) then Exit;
#ifdef IncludeApi
    if MarkerRecords[5] <> 'includeAPI' then Exit;
#else
    if MarkerRecords[5] <> 'standard' then Exit;
#endif
    Target := AddBackslash(Root) + 'manifest.json';
    if not GuardFileExists(Target) or (PathComponentProblem(Target) <> '') then Exit;
    Handle := UpdateCreateFile(UpdateNativePath(Target), $80000000, 3, 0, 3, $200000, 0);
    if Handle = -1 then RaiseException('Manifest is locked; uninstall did not start.');
    try
      if not ReadUninstallHandle(Handle, Saved) then Exit;
      ManifestHash := GetSHA256OfString(Saved);
    finally UpdateCloseHandle(Handle); end;
    if CompareText(MarkerRecords[3], ManifestHash) <> 0 then Exit;
    for I := 0 to Allowed.Count - 1 do begin
      Parent := ExtractFileDir(Allowed[I]);
      while Parent <> '' do begin
        if AllowedDirs.IndexOf(Parent) < 0 then AllowedDirs.Add(Parent);
        Parent := ExtractFileDir(Parent);
      end;
    end;
    for I := 1 to Records.Count - 1 do begin
      Divider := Pos('|', Records[I]);
      if (Divider <= 1) or (Length(Records[I]) - Divider <> 48) then Exit;
      Relative := Copy(Records[I], 1, Divider - 1);
      if (Allowed.IndexOf(Relative) < 0) or (Names.IndexOf(Relative) >= 0) then Exit;
      Identity := Copy(Records[I], Divider + 1, 48);
      for J := 1 to 48 do if Pos(Uppercase(Copy(Identity, J, 1)), '0123456789ABCDEF') = 0 then Exit;
      Names.Add(Relative); Ids.Add(Identity);
    end;
    for I := 10 to MarkerRecords.Count - 1 do begin
      Row := MarkerRecords[I]; Divider := Pos('|', Copy(Row, 3, MaxInt)) + 2;
      if (Copy(Row, 1, 2) <> 'D|') or (Divider <= 3) or (Length(Row) - Divider <> 48) then Exit;
      Relative := Copy(Row, 3, Divider - 3); StringChangeEx(Relative, '/', '\', True);
      if (AllowedDirs.IndexOf(Relative) < 0) or (DirectoryRows.IndexOf(Relative) >= 0) then Exit;
      Identity := Copy(Row, Divider + 1, 48);
      for J := 1 to 48 do if Pos(Uppercase(Copy(Identity, J, 1)), '0123456789ABCDEF') = 0 then Exit;
      DirectoryRows.Add(Relative); DirectoryIds.Add(Identity);
    end;
    // Acquire all guards before Inno removes registry, icons or its own uninstaller.
    for I := 0 to AllowedDirs.Count - 1 do begin
      Relative := AllowedDirs[I]; Target := AddBackslash(Root) + Relative;
      if PathComponentProblem(Target) <> '' then Exit;
      Handle := -1; Identity := ''; Index := DirectoryRows.IndexOf(Relative);
      if Index >= 0 then Identity := DirectoryIds[Index];
      if GuardDirExists(Target) then begin
        if Identity <> '' then Handle := UpdateCreateFile(UpdateNativePath(Target), $10081, 3, 0, 3, $2200000, 0)
        else Handle := UpdateCreateFile(UpdateNativePath(Target), $81, 3, 0, 3, $2200000, 0);
        if Handle = -1 then RaiseException('Owned directory is locked; uninstall did not start.');
        if not UpdateGetAttributes(Handle, 9, Info, 8) or ((Info.Attributes and $410) <> $10) then begin UpdateCloseHandle(Handle); Exit; end;
        if (Identity <> '') and (CompareText(UpdateHandleIdentity(Handle), Identity) <> 0) then begin
          // Pin the foreign directory too, but never request its deletion.
          UpdateCloseHandle(Handle); Handle := UpdateCreateFile(UpdateNativePath(Target), $81, 3, 0, 3, $2200000, 0);
          if Handle = -1 then RaiseException('Replaced directory is locked; uninstall did not start.');
          Identity := '';
        end;
      end;
      Count := GetArrayLength(UninstallDirectoryHandles);
      SetArrayLength(UninstallDirectoryHandles, Count + 1); SetArrayLength(UninstallDirectoryNames, Count + 1);
      UninstallDirectoryHandles[Count] := Handle;
      if Identity <> '' then UninstallDirectoryNames[Count] := Relative else UninstallDirectoryNames[Count] := '';
    end;
    UninstallNames := TStringList.Create; UninstallIdentities := TStringList.Create;
    UninstallNames.Assign(Names); UninstallIdentities.Assign(Ids); Result := True;
  finally Records.Free; MarkerRecords.Free; AllowedDirs.Free; DirectoryRows.Free; DirectoryIds.Free; Names.Free; Ids.Free; end;
end;
procedure UpdateDeleteOwnedFiles(Allowed: TStringList);
var Root, Target: String; I, J, Best: Integer; Remaining, MetadataRemoved: Boolean; Disposition: Byte;
begin
  if not NativeUninstallTrusted then begin Log('Registration-only uninstall: retained every payload and ownership file.'); Exit; end;
  Root := UpdateUppercase(RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}'))));
  Remaining := False;
  for I := 0 to UninstallNames.Count - 1 do begin
    Target := AddBackslash(Root) + UninstallNames[I];
    if GuardFileExists(Target) and not UpdateDeleteMatching(Target, UninstallIdentities[I], '') then begin
      Remaining := True; Log('Preserved replaced or locked native-owned file: ' + UninstallNames[I]);
    end;
  end;
  if Remaining then begin
    Log('Uninstall retained payload/metadata because a file was replaced or locked. Preserve the original directory; reinstall into a new empty directory.');
    if not UninstallSilent then MsgBox('部分程序文件被替换或占用，未删除；身份登记一并保留。请保留原目录，并在新的空目录安装。', mbInformation, MB_OK);
    Exit;
  end;
  Disposition := 1;
  MetadataRemoved := UpdateSetDisposition(UninstallSidecar, 4, Disposition, 1);
  if MetadataRemoved then MetadataRemoved := UpdateSetDisposition(UninstallMarker, 4, Disposition, 1);
  if not MetadataRemoved then begin
    Log('Ownership retirement failed; preserved remaining metadata and directories. Reinstall into a new empty directory.');
    if not UninstallSilent then MsgBox('身份登记清理未完成，请保留原目录，并在新的空目录安装。', mbInformation, MB_OK);
    Exit;
  end;
  // Retire children before parents through the preflight handles; never recurse.
  for I := 0 to GetArrayLength(UninstallDirectoryHandles) - 1 do begin
    Best := -1;
    for J := 0 to GetArrayLength(UninstallDirectoryHandles) - 1 do
      if (UninstallDirectoryHandles[J] <> -1) and (UninstallDirectoryNames[J] <> '') then
        if (Best = -1) or (Length(UninstallDirectoryNames[J]) > Length(UninstallDirectoryNames[Best])) then Best := J;
    if Best = -1 then Break;
    if not UpdateSetDisposition(UninstallDirectoryHandles[Best], 4, Disposition, 1) then
      Log('Preserved nonempty/locked owned directory: ' + UninstallDirectoryNames[Best]);
    UpdateCloseHandle(UninstallDirectoryHandles[Best]); UninstallDirectoryHandles[Best] := -1;
  end;
end;
