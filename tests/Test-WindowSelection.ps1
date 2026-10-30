#requires -Version 7.6
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Import-NativeTypes.ps1')
function Assert($condition,[string]$message){ if(-not $condition){ throw $message } }
$library=[Popcorn.VideoWindow]::new([IntPtr]1,'网易爆米花',0,0,3200,1400)
$video=[Popcorn.VideoWindow]::new([IntPtr]2,'示例电视剧 第17集',200,200,1280,720)
$dialog=[Popcorn.VideoWindow]::new([IntPtr]3,'设置',300,300,800,600)
$windows=[Popcorn.VideoWindow[]]@($library,$video)
Assert ([Popcorn.NativeWindows]::SelectPlayer($windows,[IntPtr]0,'').Handle -eq $video.Handle) 'A larger library window must not hide an independent video when the companion is foreground.'
Assert ([Popcorn.NativeWindows]::SelectPlayer($windows,$library.Handle,'').Handle -eq $video.Handle) 'Browsing the library must not switch playback sampling away from the actual video.'
Assert ([Popcorn.NativeWindows]::SelectPlayer([Popcorn.VideoWindow[]]@($library,$video,$dialog),$dialog.Handle,$video.Title).Handle -eq $video.Handle) 'The verified media-session title must beat a foreground settings dialog.'
Assert ([Popcorn.NativeWindows]::SelectPlayer([Popcorn.VideoWindow[]]@($library),[IntPtr]0,'').Handle -eq $library.Handle) 'An app-only window may remain a read-only discovery fallback, not proof of video playback.'
Assert ($null -eq [Popcorn.NativeWindows]::SelectPlayer([Popcorn.VideoWindow[]]@(),[IntPtr]0,'')) 'No windows must produce an explicit unavailable target.'
'PASS: playback-window selection prioritizes verified titles/video windows over the library and dialogs'
