#requires -Version 7.6
$ErrorActionPreference='Stop'
try { & (Join-Path $PSScriptRoot 'Host.ps1') }
catch {
    Add-Type -AssemblyName System.Windows.Forms
    $recovery="请检查发行包是否完整解压；不要在压缩软件内直接运行。"
    if(Test-Path -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) '启动弹幕助手.bat')){ $recovery="可先使用旧版入口：启动弹幕助手.bat" }
    [void][System.Windows.Forms.MessageBox]::Show("新版弹幕助手启动失败：`r`n"+$_.Exception.Message+"`r`n`r`n"+$recovery,'爆米花弹幕助手',[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Error)
    exit 1
}
