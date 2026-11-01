function LocalDriveType(RootPath: String): Integer;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function RawPathAttributes(Path: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function GuardNativePath(Path: String): String;
begin
  Result := ExpandFileName(Path);
  if Copy(Result, 1, 4) = '\\?\' then Exit;
  if Copy(Result, 1, 2) = '\\' then Result := '\\?\UNC\' + Copy(Result, 3, MaxInt)
  else Result := '\\?\' + Result;
end;
function PathAttributes(Path: String): LongWord;
begin Result := RawPathAttributes(GuardNativePath(Path)); end;
function GuardFileExists(Path: String): Boolean;
var Attributes: LongWord;
begin Attributes := PathAttributes(Path); Result := (Attributes <> $FFFFFFFF) and ((Attributes and $10) = 0); end;
function GuardDirExists(Path: String): Boolean;
var Attributes: LongWord;
begin Attributes := PathAttributes(Path); Result := (Attributes <> $FFFFFFFF) and ((Attributes and $10) <> 0); end;

function PathComponentProblem(Path: String): String;
var
  Current, Parent: String;
  Attributes, Error: LongWord;
begin
  Result := '';
  Current := RemoveBackslashUnlessRoot(ExpandFileName(Path));
  repeat
    Attributes := PathAttributes(Current);
    if Attributes = $FFFFFFFF then
    begin
      Error := DLLGetLastError;
      if (Error <> 2) and (Error <> 3) then
      begin
        Result := '无法确认安装路径的安全性，请选择有权限访问的新目录。';
        Exit;
      end;
    end
    else if (Attributes and $400) <> 0 then
    begin
      Result := '安装路径不能经过目录联接、符号链接或其他重解析点，请选择实际的本地目录。';
      Exit;
    end;
    Parent := ExtractFileDir(Current);
    if CompareText(Parent, Current) = 0 then Exit;
    Current := Parent;
  until Current = '';
end;

function PayloadPathsProblem(Destination: String; Incoming: TStringList): String;
var Checked: TStringList; I: Integer; Root, Target, Parent: String; Attributes, Error: LongWord;
begin
  Result := ''; Root := RemoveBackslashUnlessRoot(ExpandFileName(Destination));
  Checked := TStringList.Create; Checked.Sorted := True;
  try
    for I := 0 to Incoming.Count - 1 do begin
      Target := ExpandFileName(AddBackslash(Root) + Incoming[I]);
      if CompareText(Copy(Target, 1, Length(AddBackslash(Root))), AddBackslash(Root)) <> 0 then begin
        Result := '安装文件路径超出目标目录。'; Exit;
      end;
      Attributes := PathAttributes(Target);
      if (Attributes <> $FFFFFFFF) and ((Attributes and $410) <> 0) then begin
        Result := '程序文件位置是目录或链接，请先处理冲突文件，或选择新的目录。'; Exit;
      end;
      Parent := ExtractFileDir(Target);
      while CompareText(Parent, Root) <> 0 do begin
        if Checked.IndexOf(Parent) >= 0 then Break;
        Checked.Add(Parent); Attributes := PathAttributes(Parent);
        if Attributes <> $FFFFFFFF then begin
          if ((Attributes and $400) <> 0) or ((Attributes and $10) = 0) then begin
            Result := '安装子目录是文件或链接，请先处理，或选择新的目录。'; Exit;
          end;
        end else begin
          Error := DLLGetLastError;
          if (Error <> 2) and (Error <> 3) then begin Result := '无法访问安装子目录，请选择有写入权限的位置。'; Exit; end;
        end;
        Parent := ExtractFileDir(Parent);
      end;
    end;
  finally Checked.Free; end;
end;

function DestinationProblemForDrive(Destination, RegisteredDirectory: String; DriveKind: Integer): String;
var Target, Registered: String;
begin
  Result := ''; Target := RemoveBackslashUnlessRoot(ExpandFileName(Destination));
  if (Copy(Target, 1, 2) = '\\') or ((DriveKind <> 2) and (DriveKind <> 3) and (DriveKind <> 6)) then begin
    Result := '请选择可用的本地磁盘，不支持网络共享或映射网络盘。'; Exit;
  end;
  if AddBackslash(Target) = AddBackslash(ExtractFileDrive(Target)) then begin
    Result := '请选择专用的安装子目录，不要直接使用磁盘根目录。'; Exit;
  end;
  if GuardFileExists(Target) then begin Result := '目标位置是文件，请选择安装文件夹。'; Exit; end;
  Result := PathComponentProblem(Target); if Result <> '' then Exit;
  if RegisteredDirectory = '' then Exit;
  Registered := RemoveBackslashUnlessRoot(ExpandFileName(RegisteredDirectory));
  if CompareText(Target, Registered) <> 0 then
    Result := '本程序已安装在其他目录。请使用原目录重装，或先卸载旧安装再选择新目录。';
end;
