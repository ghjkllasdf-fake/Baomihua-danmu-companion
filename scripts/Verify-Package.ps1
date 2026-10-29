#requires -Version 7.6
$ErrorActionPreference='Stop'
function Test-AllowedPackagePath([string]$path){
    if(-not $path -or $path.Length -gt 512 -or $path -match '[\\:<>"|?*\x00-\x1f]' -or $path -match '(^/|(^|/)\.\.?(/|$)|[. ](/|$))'){ return $false }
    foreach($part in $path.Split('/')){
        if(-not $part -or $part -match '^(CON|CONIN\$|CONOUT\$|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(\.|$)'){ return $false }
    }
    $rules=@(
        '^(启动弹幕助手新版\.bat|检查文件\.bat|Verify-Package\.ps1|README\.md|LICENSE|THIRD-PARTY-NOTICES\.md)$',
        '^desktop/(Host|Start|ProbeWorker)\.ps1$',
        '^desktop/native/[A-Za-z0-9]+\.cs$',
        '^assets/(app|tray)\.ico$',
        '^ui/dist/(index\.html|favicon\.svg|assets/[A-Za-z0-9_-]+\.(js|css|svg|png|jpg|webp|woff2))$',
        '^\.deps/pwsh/.+$',
        '^\.deps/webview2/(lib_manual/netcoreapp3\.0/Microsoft\.Web\.WebView2\.Core\.dll|runtimes/win-x64/native/WebView2Loader\.dll)$',
        '^\.deps/windows-sdk/lib/net8\.0/(WinRT\.Runtime|Microsoft\.Windows\.SDK\.NET)\.dll$',
        '^licenses/(WebView2|WindowsSDK|React|ReactDOM|Scheduler)/[A-Za-z0-9_.-]+\.(txt|rtf|json|nuspec)$'
    )
    foreach($rule in $rules){ if($path -cmatch $rule){ return $true } }
    return $false
}
try {
    $packageRoot=[IO.Path]::GetFullPath($PSScriptRoot)
    $prefix=$packageRoot+[IO.Path]::DirectorySeparatorChar
    $manifestPath=Join-Path $packageRoot 'manifest.json'
    $manifestInfo=Get-Item -LiteralPath $manifestPath
    if($manifestInfo.Length -gt 4MB -or ($manifestInfo.Attributes -band [IO.FileAttributes]::ReparsePoint)){ throw 'Package manifest is too large or redirected.' }
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 8
    if($manifest.schemaVersion -ne 1 -or $manifest.preview -isnot [bool] -or $manifest.preview -ne $true -or
        $manifest.version -notmatch '^\d+\.\d+\.\d+-preview\.[1-9]\d*$' -or
        $manifest.files.Count -lt 1 -or $manifest.files.Count -gt 2048){ throw 'Unsupported package manifest.' }
    $expected=@{}
    $totalBytes=0L
    foreach($entry in $manifest.files){
        if($entry.path -isnot [string] -or -not (Test-AllowedPackagePath $entry.path) -or
            $entry.sha256 -cnotmatch '^[a-f0-9]{64}$' -or ($entry.length -isnot [int] -and $entry.length -isnot [long]) -or
            $entry.length -lt 0 -or $entry.length -gt 128MB -or $expected.ContainsKey($entry.path)){ throw 'Invalid package file record.' }
        $file=[IO.Path]::GetFullPath((Join-Path $packageRoot $entry.path))
        $totalBytes+=$entry.length
        if($totalBytes -gt 512MB){ throw 'Package byte budget exceeded.' }
        if(-not $file.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){ throw 'Package path escaped its folder.' }
        $parent=[IO.Path]::GetDirectoryName($file)
        while($parent.Length -ge $packageRoot.Length){
            if((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint){ throw 'A package folder was redirected.' }
            $parent=[IO.Path]::GetDirectoryName($parent)
        }
        $info=Get-Item -LiteralPath $file
        if($info.PSIsContainer -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $info.Length -ne $entry.length -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $entry.sha256){ throw 'A package file is missing, changed or invalid.' }
        $expected[$entry.path]=$true
    }
    $pending=[Collections.Generic.Stack[string]]::new()
    $pending.Push($packageRoot)
    while($pending.Count){
        $directory=$pending.Pop()
        foreach($item in Get-ChildItem -LiteralPath $directory -Force){
            if($directory -eq $packageRoot -and $item.Name -eq '.cache'){ continue }
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){ throw 'Unexpected reparse point in package.' }
            if($item.PSIsContainer){ $pending.Push($item.FullName); continue }
            $relative=[IO.Path]::GetRelativePath($packageRoot,$item.FullName).Replace('\','/')
            if($relative -ne 'manifest.json' -and -not $expected.ContainsKey($relative)){ throw 'An unexpected file was added to the package.' }
        }
    }
    'PASS: '+$manifest.files.Count+' files match preview '+$manifest.version+'.'
    'This detects missing or changed files; it is not a digital signature. Local .cache data is excluded.'
} catch {
    [Console]::Error.WriteLine('Package verification failed: '+$_.Exception.Message)
    exit 1
}
