type
  THelperHandles = array of Integer;
  THelperAttributes = record
    Attributes, Tag: LongWord;
  end;
function HelperGetAttributes(Handle, InformationClass: Integer;
  var Information: THelperAttributes; Size: LongWord): Boolean;
  external 'GetFileInformationByHandleEx@kernel32.dll stdcall';
var
  HelperHandles: THelperHandles;
  HelperDirectories, HelperRoots: TStringList;

procedure RetainHelperHandle(Handle: Integer);
var Count: Integer;
begin
  Count := GetArrayLength(HelperHandles); SetArrayLength(HelperHandles, Count + 1);
  HelperHandles[Count] := Handle;
end;

procedure ReleaseHelperGuards;
var I: Integer;
begin
  for I := GetArrayLength(HelperHandles) - 1 downto 0 do UpdateCloseHandle(HelperHandles[I]);
  SetArrayLength(HelperHandles, 0);
  if HelperDirectories <> nil then HelperDirectories.Free;
  if HelperRoots <> nil then HelperRoots.Free;
  HelperDirectories := nil; HelperRoots := nil;
end;

procedure HoldHelperDirectory(Path: String);
var Parent: String; Handle: Integer; Info: THelperAttributes;
begin
  Path := RemoveBackslashUnlessRoot(ExpandFileName(Path));
  if HelperDirectories.IndexOf(Path) >= 0 then Exit;
  Parent := ExtractFileDir(Path);
  if (Parent <> '') and (CompareText(Parent, Path) <> 0) then HoldHelperDirectory(Parent);
  // FILE_LIST_DIRECTORY is required: attributes-only handles do not prevent rename.
  Handle := UpdateCreateFile(UpdateNativePath(Path), $81, 3, 0, 3, $2200000, 0);
  if Handle = -1 then RaiseException('Cannot lock trusted helper directory.');
  RetainHelperHandle(Handle);
  if not HelperGetAttributes(Handle, 9, Info, 8) or ((Info.Attributes and $410) <> $10) then
    RaiseException('Trusted helper directory was replaced or is a link.');
  HelperDirectories.Add(Path);
end;

procedure AcquireHelperGuards(Stage: String);
var Names, Sizes, Hashes: TStringList; I, Handle: Integer; Path: String;
  Size: Int64; Info: THelperAttributes;
begin
  Stage := RemoveBackslashUnlessRoot(ExpandFileName(Stage));
  if HelperDirectories = nil then begin HelperDirectories := TStringList.Create; HelperRoots := TStringList.Create; end
  else if HelperRoots.IndexOf(Stage) >= 0 then Exit;
  Names := TStringList.Create; Sizes := TStringList.Create; Hashes := TStringList.Create;
  try
   try
    AddTrustedHelperFiles(Names, Sizes, Hashes);
    if (Names.Count = 0) or (Names.Count > 16385) or
      (Sizes.Count <> Names.Count) or (Hashes.Count <> Names.Count) then RaiseException('Invalid compiled helper trust table.');
    HoldHelperDirectory(Stage);
    for I := 0 to Names.Count - 1 do begin
      Path := AddBackslash(Stage) + Names[I]; HoldHelperDirectory(ExtractFileDir(Path));
      Handle := UpdateCreateFile(UpdateNativePath(Path), $80000000, 1, 0, 3, $200000, 0);
      if Handle = -1 then RaiseException('Cannot lock trusted helper file.');
      RetainHelperHandle(Handle);
      if not HelperGetAttributes(Handle, 9, Info, 8) or ((Info.Attributes and $410) <> 0) or
        not UpdateGetSize(Handle, Size) or (IntToStr(Size) <> Sizes[I]) or
        (CompareText(GetSHA256OfFile(Path), Hashes[I]) <> 0) then RaiseException('Extracted helper failed compiled checksum validation.');
    end;
    HelperRoots.Add(Stage);
  except
    ReleaseHelperGuards; RaiseException(GetExceptionMessage);
   end;
  finally Names.Free; Sizes.Free; Hashes.Free; end;
end;
