#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'desktop/Host.ps1') -Check
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
$profile='api-restore-'+[Guid]::NewGuid().ToString('N')
$path=Join-Path $root ('.cache/'+$profile+'/api.dat')
[Popcorn.ApiPreferences]::Save($path,'https://danmu.example.com/EXAMPLE_ONLY')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
foreach($initial in @('','https://danmu.example.com/ENVIRONMENT_EXAMPLE')){
    $window=[Popcorn.DesktopWindow]::new($root,$initial,$profile)
    try{
        $active=$window.GetType().GetField('apiBase',$flags).GetValue($window)
        $saved=$window.GetType().GetField('savedApiBase',$flags).GetValue($window)
        Assert ($saved -ceq 'https://danmu.example.com/EXAMPLE_ONLY') 'A fresh workbench must restore its encrypted saved address.'
        Assert ($active -ceq $(if($initial){$initial}else{$saved})) 'An explicit environment address takes precedence without overwriting the saved value.'
    }finally{$window.Dispose()}
}
Assert ([Popcorn.ApiPreferences]::Load($path).ApiBase -ceq 'https://danmu.example.com/EXAMPLE_ONLY') 'Opening the workbench must not rewrite persistent API settings.'
'PASS: fresh workbench startup restores isolated encrypted API data; explicit environment precedence and prior saved value preserved.'
