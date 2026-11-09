# 第三方组件与许可

发行包保留以下组件的上游许可和声明原文。本说明只列出组成，不改变上游许可；不把原作者或版权信息当作用户隐私删除。

项目原创桌面客户端采用根目录 `LICENSE` 中的 MIT 许可证；includeAPI 本机适配器和上游补丁另按 AGPL-3.0 提供。第三方组件的许可分别保留，不因桌面客户端采用 MIT 而改变。README 使用的日落动画与示例弹幕由本项目自行制作，没有额外影视素材许可依赖。

本原生 0.2 发行包包含：

- 自包含 .NET 10.0.14 Windows x64 桌面运行库：上游许可与声明保存在 `licenses/Dotnet/LICENSE.txt`、`licenses/Dotnet/ThirdPartyNotices.txt`。不包含 .NET SDK 或 PowerShell。
- Microsoft Edge WebView2 Evergreen Runtime：复用系统稳定版，不包含私有浏览器目录。仅随包提供微软签名的 `MicrosoftEdgeWebview2Setup.exe` 1.3.307.15，用于缺少或过旧时联网安装／更新。运行库许可告知原文保留在 `licenses/WebView2Runtime/`，来源见 `SOURCE.txt`；共享浏览器由微软维护和更新，不随本应用卸载。
- Microsoft.Web.WebView2 SDK 1.0.4314.35 Core 和 x64 加载器：许可、声明和元数据位于 `licenses/WebView2/`。
- Microsoft.Windows.SDK.NET.Ref 10.0.26100.84 运行时投影：许可和原始包元数据位于 `licenses/WindowsSDK/`，RTF 许可取自该包元数据指向的原始许可地址。
- 打包进本地界面的 React 19.3.1、React DOM 19.3.1 和锁文件中的 Scheduler：许可位于 `licenses/React`、`licenses/ReactDOM` 和 `licenses/Scheduler`。

## 0.2.1 播放器倍速读取组件

原生采样器使用以下固定诊断组件，只读识别受支持播放器进程中的当前倍速。项目仍使用自包含 .NET 10；从 .NET 8 运行时包选取的 DAC 是诊断 DLL，不是要求用户另外安装或运行 .NET 8。组件由 Microsoft 提供，相关源码许可保留 .NET Foundation and Contributors 的版权与声明；本项目 MIT 许可不替代这些许可。

| NuGet 组件 | 版本 | 许可 | 随包声明目录 |
|---|---|---|---|
| Microsoft.Diagnostics.Runtime（ClrMD） | 3.1.512801 | MIT | `licenses/ClrMD/` |
| Microsoft.Diagnostics.NETCore.Client | 0.2.410101 | MIT | `licenses/DiagnosticsClient/` |
| Microsoft.Bcl.AsyncInterfaces | 1.1.0 | MIT | `licenses/AsyncInterfaces/` |
| Microsoft.Extensions.Configuration、Microsoft.Extensions.Configuration.Abstractions、Microsoft.Extensions.Configuration.Binder | 2.1.1 | Apache-2.0 | `licenses/Extensions-2.1.1/` |
| Microsoft.Extensions.DependencyInjection.Abstractions | 2.1.1 | Apache-2.0 | `licenses/Extensions-2.1.1/` |
| Microsoft.Extensions.Logging、Microsoft.Extensions.Logging.Abstractions | 2.1.1 | Apache-2.0 | `licenses/Extensions-2.1.1/` |
| Microsoft.Extensions.Options、Microsoft.Extensions.Primitives | 2.1.1 | Apache-2.0 | `licenses/Extensions-2.1.1/` |
| Microsoft.NETCore.App.Runtime.win-x64 中的 `mscordaccore.dll`，随包命名为 `Baomihua.PlayerRate.Dac.dll` | 8.0.28 | MIT，另保留该包第三方声明 | `licenses/PlayerRateDac/` |

上表列出倍速读取新增的 11 个托管依赖和一个原生 DAC，实际发布文件以同版本 manifest 为准。NuGet 标识和版本由 `desktop/NativeProbe/packages.lock.json` 与 Probe 项目引用固定；DAC 的包版本由 Probe 项目的 `PackageDownload` 固定。

许可原文与来源：

- ClrMD：保留源码提交 [`c9edb15`](https://github.com/microsoft/clrmd/blob/c9edb15ebd4c58a1f2db3e17693124b70235faa1/LICENSE) 的 MIT `LICENSE`。
- Diagnostics Client：保留源码提交 [`f993832`](https://github.com/dotnet/diagnostics/tree/f99383213ea19741908f5aa3cf0ed400db2e5f0a) 的 `LICENSE.TXT` 和 `THIRD-PARTY-NOTICES.TXT`。
- AsyncInterfaces：保留 1.1.0 NuGet 包自己的 `LICENSE.TXT`、`THIRD-PARTY-NOTICES.TXT`；包内 `version.txt` 对应 corefx 提交 [`0f7f38c`](https://github.com/dotnet/corefx/tree/0f7f38c4fd323b26da10cce95f857f77f0f09b48)。
- Extensions 2.1.1：各包 nuspec 的 `licenseUrl` 指向 `aspnet` 组织的 `Home` 仓库中 `2.0.0` 版本的 `LICENSE.txt`，其内容是 Apache-2.0 告知，而非 MIT。发行时须保留该告知、[Apache-2.0 完整许可](https://www.apache.org/licenses/LICENSE-2.0.txt)，以及对应源码仓库的版权许可文件；不能仅附一个链接或用当前版本的 MIT 许可替代。具体仓库提交见 [构建说明](DEVELOPMENT.md#播放器倍速诊断组件与许可输入)。
- DAC：选自 8.0.28 NuGet 包，仅重命名文件，不修改 DLL 字节；保留该包自己的 `LICENSE.TXT`、`THIRD-PARTY-NOTICES.TXT`，源码对应 dotnet/runtime 提交 [`46295af`](https://github.com/dotnet/runtime/tree/46295af5828b062bbbf93a9cef50fd8cb9fbcb09)。这两份文件不能由随包 .NET 10 的许可与第三方声明替代。

各声明目录同时保留版本、来源及文件重命名说明。诊断组件、传递依赖和 DAC 的声明应随普通版、includeAPI 版及对应源码一并保留，不改变原有第三方许可文本。

## 其他组件与隐私

原始构建归档、依赖版本和校验值固定在源码 `native-toolchain.json` 中，发行清单记录实际文件哈希和源码身份。这些完整性记录不是数字签名，也不改变上游许可条款。生成安装包使用 Inno Setup 7.1.1；编译器和开发 SDK 不作为应用文件分发。

额外的 **includeAPI 变体** 还包含私有 Node.js、固定上游 danmu_api 提交 `4d325e096bbbf0c1ff261a9bfa638f7c58784f60`、本机适配器源码及锁定的生产依赖，均位于 `api/`。普通版不包含这些文件。对应运行时身份、完整文件清单、依赖锁文件、源码与声明随 API payload 保留。上游根 LICENSE 为 AGPL-3.0，而其 package.json 写 ISC；这里明确保留该差异，按根许可提供上游、本机适配器及补丁，不把它们当作 MIT 或仅按 package 元数据重新授权。Node 和 npm 生产依赖各自的原始许可证仍然适用；具体路径以随包的 API notices/provenance 为准。完整首选修改格式、已校验的原始源码归档、修改记录和构建／替换说明见同版本另附的 `corresponding-source.zip`；运行包里的 `node_modules` 不等于完整对应源码。

WebView2 包含 Microsoft Defender SmartScreen，可能按其许可向微软发送终端用户相关安全及诊断信息。微软隐私声明：https://aka.ms/privacy ；Edge 隐私白皮书：https://learn.microsoft.com/en-us/microsoft-edge/privacy-whitepaper#smartscreen 。本项目的 MIT 许可证仅覆盖原创部分，不能替代包内微软运行环境和其他组件的许可。

发行包不包含网易爆米花播放器、浏览器资料、用户 API 地址、个人偏好文件或旧版程序脚本；启动、采样、开机启动、前置环境安装及卸载均不调用 PowerShell。精简版不包含私有浏览器及其可选 BAT 声明工具；历史 beta.1 完整离线包的组成以其随包声明为准。
