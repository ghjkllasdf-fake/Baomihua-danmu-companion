function LocalDriveType(RootPath: String): Integer;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function PathAttributes(Path: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

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

function PayloadCollisionProblem(Destination: String; Incoming: TStringList): String;
var
  Owned: TStringList;
  Index: Integer;
  Target, Parent: String;
begin
  Result := '';
  Owned := TStringList.Create;
  try
    if not FileExists(AddBackslash(Destination) + 'install-files.txt') then
    begin
      Result := '原安装缺少文件所有权清单，请先卸载后安装到新的空目录。';
      Exit;
    end;
    Owned.LoadFromFile(AddBackslash(Destination) + 'install-files.txt');
    for Index := 0 to Incoming.Count - 1 do
    begin
      Target := AddBackslash(Destination) + Incoming[Index];
      Result := PathComponentProblem(Target);
      if Result <> '' then Exit;
      if DirExists(Target) or (FileExists(Target) and (Owned.IndexOf(Incoming[Index]) < 0)) then
      begin
        Result := '新版本文件与原安装目录中的外来文件冲突，未覆盖原文件。请先卸载，再选择新的空目录。';
        Exit;
      end;
      Parent := ExtractFileDir(Target);
      while CompareText(Parent, RemoveBackslashUnlessRoot(Destination)) <> 0 do
      begin
        if FileExists(Parent) then
        begin
          Result := '新版本子目录与已有文件冲突，请改用新的空目录。';
          Exit;
        end;
        Parent := ExtractFileDir(Parent);
      end;
    end;
  finally
    Owned.Free;
  end;
end;

function DestinationProblemForDrive(Destination: String; RegisteredDirectory: String; DriveKind: Integer): String;
var
  Target, Registered: String;
  Entry: TFindRec;
begin
  Result := '';
  Target := RemoveBackslashUnlessRoot(ExpandFileName(Destination));
  if (Copy(Target, 1, 2) = '\\') or
     ((DriveKind <> 2) and (DriveKind <> 3) and (DriveKind <> 6)) then
  begin
    Result := '请选择可用的本地磁盘，不支持网络共享或映射网络盘。';
    Exit;
  end;
  if AddBackslash(Target) = AddBackslash(ExtractFileDrive(Target)) then
  begin
    Result := '请选择专用的安装子目录，不要直接使用磁盘根目录。';
    Exit;
  end;
  if FileExists(Target) then
  begin
    Result := '目标位置是文件，请选择专用的安装文件夹。';
    Exit;
  end;
  Result := PathComponentProblem(Target);
  if Result <> '' then Exit;
  Registered := '';
  if RegisteredDirectory <> '' then
    Registered := RemoveBackslashUnlessRoot(ExpandFileName(RegisteredDirectory));
  if (Registered <> '') and (CompareText(Target, Registered) <> 0) then
  begin
    Result := '本程序已安装在其他目录。请使用原目录重装，或先卸载旧安装再选择新目录，以免留下无法正常卸载的旧文件。';
    Exit;
  end;
  if not DirExists(Target) then Exit;
  if (Registered <> '') and (CompareText(Target, Registered) = 0) and
     FileExists(Target + '\DanmuCompanion.exe') and
     FileExists(Target + '\manifest.json') and FileExists(Target + '\unins000.exe') then Exit;
  // An enumeration failure is not proof of an empty directory.
  if not FindFirst(AddBackslash(Target) + '*', Entry) then
  begin
    Result := '无法确认目标文件夹为空，请选择新的专用子目录。';
    Exit;
  end;
  try
    repeat
      if (Entry.Name <> '.') and (Entry.Name <> '..') then
      begin
        Result := '目标文件夹已有文件。请选择空文件夹，或本程序已登记的原安装目录；不要覆盖 Portable 或其他文件。';
        Exit;
      end;
    until not FindNext(Entry);
  finally
    FindClose(Entry);
  end;
end;
