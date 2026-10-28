# 第三方组件与许可

发行包保留以下组件的上游许可和声明原文。本说明只列出组成，不改变上游许可；不把原作者或版权信息当作用户隐私删除。

本预览包包含：

- PowerShell 7.6.6 Windows x64 便携发行版及其自带 .NET：许可与声明位于 `.deps/pwsh/LICENSE.txt`、`.deps/pwsh/ThirdPartyNotices.txt` 及 Modules 目录。
- Microsoft.Web.WebView2 SDK 1.0.4314.35 Core 和 x64 加载器：许可、声明和元数据位于 `licenses/WebView2/`。
- Microsoft.Windows.SDK.NET.Ref 10.0.26100.84 运行时投影：许可和原始包元数据位于 `licenses/WindowsSDK/`，RTF 许可取自该包元数据指向的原始许可地址。
- 打包进本地界面的 React 19.3.1、React DOM 19.3.1 和锁文件中的 Scheduler：许可位于 `licenses/React`、`licenses/ReactDOM` 和 `licenses/Scheduler`。

PowerShell 和 SDK 原始归档的 SHA256 固定在打包脚本中。发行清单记录包内文件的实际字节。这些完整性记录不是数字签名，也不改变上游许可条款。

发行包不包含独立安装的 Microsoft Edge WebView2 Runtime、网易爆米花播放器、浏览器资料、用户 API 地址、个人偏好文件或旧版程序源文件。
