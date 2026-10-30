# 开发与构建

普通用户请下载 Release；源码包只供开发，不包含便携运行时或构建产物。

## 原生 Beta 构建（当前）

`0.1.1-beta.6` 从 preview.15 的 C# 和 React 功能构建，入口是 `desktop/NativeHost` 与 `desktop/NativeProbe`。用户运行、采样、自启动、WebView2 前置环境安装和卸载无需 PowerShell。本文后半的 PS 命令仅保留为历史预览版开发参考，不能用于生成 Beta。

本地工具由 `native-toolchain.json` 固定：.NET SDK 10.0.403／运行时 10.0.14、WebView2 SDK 1.0.4314.35、Windows 投影 10.0.26100.84、微软 Evergreen Bootstrapper 1.3.307.15、Inno Setup 7.1.1。Fixed Runtime 归档记录仅用于历史 beta.1，不再参与精简版 payload。按清单的官方来源准备 `.deps` 原始归档及对应解压目录；构建器校验归档、投影 DLL、引导程序和编译器，不修改全局 SDK。Node/npm 与 UI 依赖见下表和 `ui/package-lock.json`。

```text
npm.cmd --prefix ui ci --ignore-scripts
npm.cmd --prefix ui run lint
npm.cmd --prefix ui test
npm.cmd --prefix ui run test:e2e
.deps\dotnet\dotnet.exe run --project tests/NativeDistribution/NativeDistribution.csproj -c Release
node --test tests/release-package.test.mjs tests/native-archive.test.mjs tests/installer-guard.test.mjs tests/browser-prerequisite.test.mjs tests/source-state.test.mjs
node scripts/check-release-mutation.mjs
node scripts/build-release.mjs --version 0.1.1-beta.6
node scripts/test-release.mjs --version 0.1.1-beta.6
```

运行本地 .NET 时将 `DOTNET_CLI_HOME` 指向 `.deps/dotnet-home`。最终构建要求干净且已提交的源码；构建器强制 `npm ci --ignore-scripts`、.NET locked restore，并在构建后核对 HEAD、Git 状态和全部非忽略源文件字节。`--allow-dirty` 仅生成候选，不得用作最终交付。禁止覆盖同名产物。最终安装包、ZIP 和 `SHA256SUMS.txt` 位于 `artifacts/releases/0.1.1-beta.6/`，两个版本来自同一 manifest payload；应用文件不得超过 210 MiB，且白名单拒绝私有浏览器目录。

`test-release.mjs` 必须对最终字节运行：校验 ZIP、移动到中文／空格路径、在隔离数据目录和受限环境下完成真实握手、记录所有观测到的后代进程及系统稳定 Runtime 实际路径、确认退出；还实际执行自定义目录安装、拒绝孤立旧安装、原位重装、卸载和外来文件保留测试。若本机已有真实安装记录或同名开始菜单目录，它会拒绝运行，不会覆盖或卸载用户安装。测试报告保留在忽略的 `artifacts/shared-beta/final-validation/`，通过后仅清理本次测试目录。

非 `app` profile 使用隔离 `data-root/profile`，测试禁止读写真实 API 或浏览器数据。常规 Beta 使用 `%LOCALAPPDATA%/BaomihuaDanmuCompanion`，不再使用程序目录下的 `.cache`。启动项迁移须用户显式操作；卸载清理只匹配本安装的原生 EXE。

构建命令不会创建或推送 tag、GitHub Release。远程发布须单独取得授权：先审查并验证最终字节，推送相同来源提交的发布分支／标签，再创建 draft prerelease、上传限定文件、核对远程哈希后发布，不重写旧标签／附件。不要上传本机完整日志。Windows 10 的完整 OS 验收、长期真实播放器及既有性能目标不因打包成功而视为通过。

### 多季匹配回归

`SeriesIdentity` 将已知季数归一为“片名 第N季”，沿用现有采样协议、媒体版本、旧请求隔离和来源绑定链路，不增加不兼容的消息字段。`CachedSeasonReader` 只解析当前用户爆米花既有首页缓存中的最近播放项，核对片名、集数、分集名、文件名季号和时长；跳过非首页端点，拒绝过期、损坏、矛盾和超预算数据。`LibrarySeasonReader` 可从同一播放器进程的详情页补足证据，并在同窗连续下一集时沿用已确认季数。详情页省略秒数的分钟值、以 `:00` 结束的列表时长按截断的分钟区间校验；其他时长继续使用两秒误差。此规则仍须唯一同名季标题、同季文件名及当前集完整分集名共同成立，不放宽首页缓存或矛盾证据。两者不写入播放器文件或持久化观看记录。只有季数明确后才将候选交给数量比较／抽样；未知而存在多个季时拒绝猜测。

```text
.deps\dotnet\dotnet.exe run --project tests/NativeDistribution/NativeDistribution.csproj -c Release -- --season-match-tests
.deps\dotnet\dotnet.exe run --project tests/NativeDistribution/NativeDistribution.csproj -c Release -- --season-identity-tests
.deps\dotnet\dotnet.exe run --project tests/NativeDistribution/NativeDistribution.csproj -c Release -- --seek-recovery-tests
```

完整原生套件包含这些检查。`--season-live-sampler N` 为显式只读播放器检查；`--season-live-api ABS_INCLUDEAPI_PAYLOAD N` 另外启动自有临时服务并实际联网匹配，仅记录当前片名、来源和数量，不记录地址、令牌、路径或缓存。真实播放检查与模拟回归分开，不以演示数据代替真实结果。

跳转回归覆盖大幅前后跳转、SMTC 仍报告播放而精确进度停住、播放状态短暂缺失、缓冲恢复及原生弹幕位置连续性。时钟按采样区间内实际位置变化判断跳转，不把推算时钟与停顿媒体的差值当作重复跳转；缓冲恢复不反向移动已有场景。

显式诊断 `--seek-live-sampler SECONDS` 只读输出当前播放器与时钟状态；`--packaged-playback ABS_INCLUDEAPI_PAYLOAD SECONDS` 在隔离配置中实际加载指定成品的主程序 DLL、采样器和内置服务，并记录渲染状态。后者需要当前播放器保持可见且允许实际联网；会短暂显示真实弹幕，不操作视频。输出仅保存在忽略的 `artifacts`，含当前片名与弹幕文本，不应原样公开。

`--packaged-shutdown ABS_INCLUDEAPI_PAYLOAD SECONDS` 只验证观察器定时结束、原生握手与关闭收尾，不要求视频或弹幕出现，不能代替播放验收。观察器防止跨 Dispatcher 取样时的计时器重入，关闭前先停止取样；启动未完成或提前退出仍视为失败。

## 历史预览版开发参考

## 固定工具链

| 组件 | 版本 |
|---|---|
| PowerShell / .NET | 7.6.6 / 运行时自带的 .NET 10 |
| Node.js / npm | 26.7.0 / 11.19.0 |
| React / React DOM | 19.3.1 |
| TypeScript / Vite | 6.0.3 / 8.3.5 |
| WebView2 SDK | 1.0.4314.35 |
| Windows SDK .NET Ref | 10.0.26100.84 |

在 Windows x64 上进行开发。`Setup-Desktop.ps1` 需要从 PowerShell 7.6 环境运行，会准备项目内的 `.deps`，不改变系统执行策略。不要以强制升级依赖或关闭检查的方式解决构建问题。

```powershell
pwsh -NoProfile -File .\scripts\Setup-Desktop.ps1
npm.cmd --prefix ui ci --ignore-scripts
npm.cmd --prefix ui run lint
npm.cmd --prefix ui test
npm.cmd --prefix ui run build
.\.deps\pwsh\pwsh.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\desktop\Host.ps1 -Check
```

`.npmrc` 默认禁用安装脚本。PowerShell、WebView2 和 Windows SDK 下载归档都有固定 SHA256 校验；修改版本时必须同时审查来源和校验值。

## 代码结构

- `ui/src/`：React 控制台、命令类型校验、设置事务和样式演示。
- `desktop/Host.ps1`：独立桌面入口和原生 C# 编译加载。
- `desktop/native/`：消息桥、弹幕 API、自动选源、播放器探测、时钟、覆盖层和资源预算。
- `scripts/`：依赖准备、构建、组包和验证工具。
- `tests/`、`ui/e2e/`：确定性回归、原生边界测试和浏览器测试。

## 测试

```powershell
npm.cmd --prefix ui run test:latency
npm.cmd --prefix ui run test:e2e
.\.deps\pwsh\pwsh.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\scripts\Test-Native.ps1 -Module all
.\.deps\pwsh\pwsh.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\scripts\Test-WebViewPrivacy.ps1
```

原生全套检查会短暂打开自有测试窗口，用于核对焦点和覆盖层行为；不会控制爆米花播放器。`Test-PlaybackIntegration.ps1` 是另外的真实播放器观察工具，不属于自动播放输入。

真实 API 检查默认不运行、不附带账号。需要时在本机为测试进程配置 `DANMU_API_BASE` 环境变量；不要把真实值写入源码、命令示例或提交历史。桌面入口以显式环境变量优先，`-NoDefaultApi` 仅禁用环境变量读取。普通版在托盘面板第一行输入并点击保存即可。

API 使用 `ProtectedData` 的 `CurrentUser` 范围加密，独立存于 `%LOCALAPPDATA%\BaomihuaDanmuCompanion\api.dat`，原子写入失败时保留旧文件与活动地址；空地址表示显式清除。`DataProfile` 非 `app` 时改用项目 `.cache/<profile>/api.dat`，所有自动化使用隔离 profile，不读写用户的真实 API。客户端不使用 localStorage/sessionStorage 保存服务地址。参考微软的 [ProtectedData 文档](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata.protect)，相同 Windows 用户下的程序属于同一信任范围。

## 组包

```powershell
.\.deps\pwsh\pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\New-PreviewPackage.ps1 -Version 0.1.0-preview.15
```

脚本从固定校验值的原始归档取得便携运行时和 SDK，按白名单组包；附带文件清单、依赖许可、中文说明及 ZIP 的 SHA256。会在独立解压目录里运行包内校验和原生编译。同名产物已存在时拒绝覆盖。

发布前运行 `npm.cmd --prefix ui audit --json`。提交源文件后可运行 `node scripts/Test-PublicPrivacy.mjs` 检查 Git 索引；它不会上传数据。任何日志、缓存、私人配置、私人截图或未审查二进制都不应进入 Git。项目原创部分使用 MIT，组包必须包含根目录 `LICENSE`；第三方许可保留原文。

当前托盘面板图在 UI 构建后运行 `node scripts/Capture-TrayScreenshots.mjs`；六张图（含皮肤和来源批量选择）使用隔离 fixture，明确标注演示，不访问真实 API 或播放器。原有视频效果示意仍可依次运行 `scripts/Capture-ReadmeOverlay.ps1`（PS7，STA）和 `node scripts/Capture-ReadmeScreenshots.mjs` 重建。前两张为真实 React 界面配隔离 fixture，第三张为自制动画视频画面与生产原生渲染器输出的1:1叠合示意，均明确标注演示用途，不访问用户播放器或真实 API。临时 WebM 位于忽略的 `.cache/readme-demo/`，只提交已审查的三张 PNG。

## 质量约定

不能通过跳过测试、删除断言、添加类型抑制或降低门槛来获得通过。播放器状态未知时必须明确显示未知，不用演示数据或手动时钟冒充自动同步。

保留的目标包括：500ms 播放响应、100ms 设置交付、连续10秒暂停零漂移；1080p / 5万条时间轴 / 实际100活动弹幕 / 60秒测试的回调间隔 p95≤20ms、p99≤33.4ms。回调间隔不是实际屏幕帧率，局部通过也不等于上述目标全部达标。

渲染资源上限保持：8192px 单边、16MiB 单位图、64MiB 活动及缓存总像素、512缓存项。真实视频、多屏、长期资源和完整使用流程仍需独立验收。

## 图标资源

当前图标采用提供的 `baomihua-danmu-icons` 素材：`assets/app.ico` 用于主程序、窗口、任务栏、启动快捷方式和安装器，`assets/tray.ico` 用于通知区域。应用 ICO 含 16、20、24、32、40、48、64、96、128、256px 共 10 帧，托盘 ICO 含 16、20、24、32、40、48、64、256px 共 8 帧。小尺寸帧有独立优化，必须保留原始 ICO 字节，不应从大图或 SVG 批量缩放重建。

`docs/assets/app-icon.png` 和 `docs/assets/tray-icon.png` 保留提供的 1024px 原稿。`assets/app.svg`、`assets/tray.svg` 是提供的位图包装 SVG，不代表可重新生成小尺寸 ICO 的矢量源。浏览器入口使用与应用相同的 `ui/public/favicon.ico`；继续保留同步更新的 `favicon.svg` 供旧源码组包流程兼容。

```text
node scripts/Build-Icons.mjs
node scripts/Build-Icons.mjs --out artifacts/icon-preview
node --test tests/icon-assets.test.mjs
.deps\pwsh\pwsh.exe -NoProfile -File tests/Test-AppIcons.ps1
```

图标脚本只刷新浏览器副本和尺寸／灰阶预览，不改写 `assets/*.ico`。可用 `--out` 将输出放到隔离目录。原生构建后，可给 `Test-AppIcons.ps1` 传入 `-ApplicationExecutable`，逐像素核对 EXE 内嵌图标与提供的应用图标。此流程不创建 Release、不移动既有标签，也不替换发布附件。
