var DirectRepairDirectory: String;
procedure PrepareDirectRepair(Registered, Version, Variant: String; Incoming: TStringList);
var Parent, Candidate, Header, Target: String; Entry: TFindRec; Saved: AnsiString; Count, Handle: Integer; Size: Int64;
begin
  DirectRepairDirectory := ''; AllowRegisteredRepair := False;
  if ExpandConstant('{param:UPDATEJOURNAL|}') <> '' then Exit;
  Target := UpdateUppercase(RemoveBackslashUnlessRoot(ExpandFileName(WizardDirValue)));
  if (Registered <> '') and (CompareText(RemoveBackslashUnlessRoot(ExpandFileName(Registered)), Target) <> 0) then Exit;
  Parent := AddBackslash(ExtractFileDir(Target)) + '.DanmuCompanion-updates';
  if not GuardDirExists(Parent) then Exit;
  if PathComponentProblem(Parent) <> '' then RaiseException('Unsafe repair metadata location.');
  Header := 'github-direct-repair-v1|' + Target + '|' + Version + '|' + Variant + #13#10;
  Count := 0;
  if FindFirst(GuardNativePath(AddBackslash(Parent) + '*'), Entry) then try
    repeat
      Count := Count + 1; if Count > 4096 then RaiseException('Too many update metadata entries.');
      if ((Entry.Attributes and $410) = $10) and (Length(Entry.Name) = 32) then begin
        Candidate := AddBackslash(Parent) + Entry.Name + '\repair.txt';
        if GuardFileExists(Candidate) then begin
          if PathComponentProblem(Candidate) <> '' then RaiseException('Unsafe repair metadata.');
          Handle := UpdateCreateFile(UpdateNativePath(Candidate), $80000000, 1, 0, 3, $200000, 0);
          if Handle = -1 then RaiseException('Cannot inspect repair metadata.');
          try
            if not UpdateGetSize(Handle, Size) or (Size > 4096) or not LoadStringFromFile(GuardNativePath(Candidate), Saved) then RaiseException('Invalid repair metadata size.');
            if CompareText(UTF8Decode(Saved), Header) = 0 then begin
              if DirectRepairDirectory <> '' then RaiseException('Multiple unfinished repair operations; no files were changed.');
              DirectRepairDirectory := AddBackslash(Parent) + Entry.Name;
            end;
          finally UpdateCloseHandle(Handle); end;
        end;
      end;
    until not FindNext(Entry);
  finally FindClose(Entry); end;
  // This only admits the selected directory. The native writer verifies every hash and disk ID before any payload mutation.
  AllowRegisteredRepair := DirectRepairDirectory <> '';
end;
