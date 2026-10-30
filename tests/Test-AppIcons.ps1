#requires -Version 7.6
param([string]$ApplicationExecutable='')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.Drawing
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
$expected=@{app=@(16,20,24,32,40,48,64,96,128,256);tray=@(16,20,24,32,40,48,64,256)}
foreach($name in @('app','tray')){
    $sizes=$expected[$name]
    $path=Join-Path $root ('assets/'+$name+'.ico')
    $bytes=[IO.File]::ReadAllBytes($path)
    Assert ([BitConverter]::ToUInt16($bytes,0) -eq 0 -and [BitConverter]::ToUInt16($bytes,2) -eq 1) 'The asset must be a Windows ICO file.'
    $count=[BitConverter]::ToUInt16($bytes,4)
    Assert ($count -eq $sizes.Count) 'The Windows icon must include every supplied display size.'
    for($i=0;$i -lt $count;$i++){
        $entry=6+16*$i
        $size=if($bytes[$entry] -eq 0){256}else{[int]$bytes[$entry]}
        Assert ($size -eq $sizes[$i]) 'ICO dimensions must match their declared ordered frames.'
        $length=[BitConverter]::ToUInt32($bytes,$entry+8)
        $offset=[BitConverter]::ToUInt32($bytes,$entry+12)
        Assert ($offset -ge 6+16*$count -and [long]$offset+$length -le $bytes.Length) 'Every ICO frame must remain within the file.'
        # Decode each embedded frame directly. The desktop's best-fit selector
        # can choose 128px for a 256px request even when a valid 256px frame exists.
        $frame=[byte[]]::new(22+$length)
        [Buffer]::BlockCopy($bytes,0,$frame,0,6)
        [Buffer]::BlockCopy([BitConverter]::GetBytes([uint16]1),0,$frame,4,2)
        [Buffer]::BlockCopy($bytes,$entry,$frame,6,16)
        [Buffer]::BlockCopy([BitConverter]::GetBytes([uint32]22),0,$frame,18,4)
        [Buffer]::BlockCopy($bytes,$offset,$frame,22,$length)
        $stream=[IO.MemoryStream]::new($frame,$false)
        $icon=[Drawing.Icon]::new($stream,$size,$size)
        $image=$null
        try {
            $image=$icon.ToBitmap()
            Assert ($image.Width -eq $size -and $image.Height -eq $size) 'Windows must decode the requested icon size.'
            $coral=0;$yellow=0;$transparent=0
            for($y=0;$y -lt $size;$y++){
                for($x=0;$x -lt $size;$x++){
                    $p=$image.GetPixel($x,$y)
                    if($p.A -lt 10){$transparent++;continue}
                    if($p.R -gt 200 -and $p.G -lt 160 -and $p.B -lt 180){$coral++}
                    if($p.R -gt 200 -and $p.G -gt 160 -and $p.B -lt 180){$yellow++}
                }
            }
            Assert ($coral -gt 0 -and $yellow -gt 0 -and $transparent -gt 0) 'Every decoded size must preserve coral, popcorn yellow and a transparent perimeter.'
        } finally {if($image){$image.Dispose()};$icon.Dispose();$stream.Dispose()}
    }
}
Assert ((Get-FileHash (Join-Path $root 'assets/app.ico')).Hash -ne (Get-FileHash (Join-Path $root 'assets/tray.ico')).Hash) 'The tray must use its dedicated simplified artwork.'
if($ApplicationExecutable){
    $embedded=[Drawing.Icon]::ExtractAssociatedIcon((Resolve-Path -LiteralPath $ApplicationExecutable).Path)
    $source=[Drawing.Icon]::new((Join-Path $root 'assets/app.ico'),$embedded.Size)
    $actual=$embedded.ToBitmap();$wanted=$source.ToBitmap()
    try {
        Assert ($actual.Size -eq $wanted.Size) 'The built executable icon must have the expected Windows size.'
        for($y=0;$y -lt $actual.Height;$y++){
            for($x=0;$x -lt $actual.Width;$x++){
                Assert ($actual.GetPixel($x,$y).ToArgb() -eq $wanted.GetPixel($x,$y).ToArgb()) 'The built executable must embed the supplied application artwork.'
            }
        }
    } finally {$actual.Dispose();$wanted.Dispose();$embedded.Dispose();$source.Dispose()}
    'PASS: the compiled application icon matches the supplied artwork pixel-for-pixel.'
}
'PASS: actual Windows decoding of 18 ICO frames, alpha and brand colors at every size, distinct application/tray assets.'
