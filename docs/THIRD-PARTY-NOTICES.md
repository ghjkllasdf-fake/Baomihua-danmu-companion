# 第三方组件与许可

发行包保留以下组件的上游许可和声明原文。本说明只列出组成，不改变上游许可；不把原作者或版权信息当作用户隐私删除。

项目原创桌面客户端采用根目录 `LICENSE` 中的 MIT 许可证；includeAPI 本机适配器和上游补丁另按 AGPL-3.0 提供。第三方组件的许可分别保留，不因桌面客户端采用 MIT 而改变。README 使用的日落动画与示例弹幕由本项目自行制作，没有额外影视素材许可依赖。

本原生 Beta 包包含：

- 自包含 .NET 10.0.14 Windows x64 桌面运行库：上游许可与声明保存在 `licenses/Dotnet/LICENSE.txt`、`licenses/Dotnet/ThirdPartyNotices.txt`。不包含 .NET SDK 或 PowerShell。
- Microsoft Edge WebView2 Evergreen Runtime：复用系统稳定版，不包含私有浏览器目录。仅随包提供微软签名的 `MicrosoftEdgeWebview2Setup.exe` 1.3.307.15，用于缺少或过旧时联网安装／更新。运行库许可告知原文保留在 `licenses/WebView2Runtime/`，来源见 `SOURCE.txt`；共享浏览器由微软维护和更新，不随本应用卸载。
- Microsoft.Web.WebView2 SDK 1.0.4314.35 Core 和 x64 加载器：许可、声明和元数据位于 `licenses/WebView2/`。
- Microsoft.Windows.SDK.NET.Ref 10.0.26100.84 运行时投影：许可和原始包元数据位于 `licenses/WindowsSDK/`，RTF 许可取自该包元数据指向的原始许可地址。
- 打包进本地界面的 React 19.3.1、React DOM 19.3.1 和锁文件中的 Scheduler：许可位于 `licenses/React`、`licenses/ReactDOM` 和 `licenses/Scheduler`。

原始构建归档、依赖版本和校验值固定在源码 `native-toolchain.json` 中，发行清单记录实际文件哈希和源码身份。这些完整性记录不是数字签名，也不改变上游许可条款。生成安装包使用 Inno Setup 7.1.1；编译器和开发 SDK 不作为应用文件分发。

额外的 **includeAPI 变体** 还包含私有 Node.js、固定上游 danmu_api 提交 `4d325e096bbbf0c1ff261a9bfa638f7c58784f60`、本机适配器源码及锁定的生产依赖，均位于 `api/`。普通版不包含这些文件。对应运行时身份、完整文件清单、依赖锁文件、源码与声明随 API payload 保留。上游根 LICENSE 为 AGPL-3.0，而其 package.json 写 ISC；这里明确保留该差异，按根许可提供上游、本机适配器及补丁，不把它们当作 MIT 或仅按 package 元数据重新授权。Node 和 npm 生产依赖各自的原始许可证仍然适用；具体路径以随包的 API notices/provenance 为准。完整首选修改格式、已校验的原始源码归档、修改记录和构建／替换说明见同版本另附的 `corresponding-source.zip`；运行包里的 `node_modules` 不等于完整对应源码。

WebView2 包含 Microsoft Defender SmartScreen，可能按其许可向微软发送终端用户相关安全及诊断信息。微软隐私声明：https://aka.ms/privacy ；Edge 隐私白皮书：https://learn.microsoft.com/en-us/microsoft-edge/privacy-whitepaper#smartscreen 。本项目的 MIT 许可证仅覆盖原创部分，不能替代包内微软运行环境和其他组件的许可。

发行包不包含网易爆米花播放器、浏览器资料、用户 API 地址、个人偏好文件或旧版程序脚本；启动、采样、开机启动、前置环境安装及卸载均不调用 PowerShell。精简版不包含私有浏览器及其可选 BAT 声明工具；历史 beta.1 完整离线包的组成以其随包声明为准。
