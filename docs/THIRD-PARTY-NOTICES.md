# 第三方组件与许可

发行包保留以下组件的上游许可和声明原文。本说明只列出组成，不改变上游许可；不把原作者或版权信息当作用户隐私删除。

项目原创部分采用根目录 `LICENSE` 中的 MIT 许可证；第三方组件的许可分别保留，不因本项目采用 MIT 而改变。README 使用的日落动画与示例弹幕由本项目自行制作，没有额外影视素材许可依赖。

本原生 Beta 包包含：

- 自包含 .NET 10.0.14 Windows x64 桌面运行库：上游许可与声明保存在 `licenses/Dotnet/LICENSE.txt`、`licenses/Dotnet/ThirdPartyNotices.txt`。不包含 .NET SDK 或 PowerShell。
- Microsoft Edge WebView2 Fixed Version Runtime 156.0.4314.50 x64：位于 `WebView2Runtime/`，从微软官方下载、原样打包；原始许可及文本版位于 `licenses/WebView2Runtime/`，来源记录为 `SOURCE.txt`。浏览器内的第三方声明和上游显示声明工具继续保留。该运行环境不会自动更新，需要随应用版本更新。
- Microsoft.Web.WebView2 SDK 1.0.4314.35 Core 和 x64 加载器：许可、声明和元数据位于 `licenses/WebView2/`。
- Microsoft.Windows.SDK.NET.Ref 10.0.26100.84 运行时投影：许可和原始包元数据位于 `licenses/WindowsSDK/`，RTF 许可取自该包元数据指向的原始许可地址。
- 打包进本地界面的 React 19.3.1、React DOM 19.3.1 和锁文件中的 Scheduler：许可位于 `licenses/React`、`licenses/ReactDOM` 和 `licenses/Scheduler`。

原始构建归档、依赖版本和校验值固定在源码 `native-toolchain.json` 中，发行清单记录实际文件哈希和源码身份。这些完整性记录不是数字签名，也不改变上游许可条款。生成安装包使用 Inno Setup 7.1.1；编译器和开发 SDK 不作为应用文件分发。

WebView2 包含 Microsoft Defender SmartScreen，可能按其许可向微软发送终端用户相关安全及诊断信息。微软隐私声明：https://aka.ms/privacy ；Edge 隐私白皮书：https://learn.microsoft.com/en-us/microsoft-edge/privacy-whitepaper#smartscreen 。本项目的 MIT 许可证仅覆盖原创部分，不能替代包内微软运行环境和其他组件的许可。

发行包不包含网易爆米花播放器、浏览器资料、用户 API 地址、个人偏好文件或旧版程序脚本；启动、采样、开机启动及卸载路径均不调用 PowerShell。上游浏览器的可选第三方声明 BAT 工具不是应用启动入口，也不依赖 PowerShell。
