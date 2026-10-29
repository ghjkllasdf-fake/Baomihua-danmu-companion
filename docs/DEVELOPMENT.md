# 开发与构建

普通用户请下载 Release；源码包只供开发，不包含便携运行时或构建产物。

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

真实 API 检查默认不运行、不附带账号。需要时在本机为测试进程配置 `DANMU_API_BASE` 环境变量；不要把真实值写入源码、命令示例或提交历史。桌面入口以显式环境变量优先，`-NoDefaultApi` 仅禁用环境变量读取。普通使用在顶部输入并点击保存即可。

API 使用 `ProtectedData` 的 `CurrentUser` 范围加密，独立存于 `%LOCALAPPDATA%\BaomihuaDanmuCompanion\api.dat`，原子写入失败时保留旧文件与活动地址；空地址表示显式清除。`DataProfile` 非 `app` 时改用项目 `.cache/<profile>/api.dat`，所有自动化使用隔离 profile，不读写用户的真实 API。客户端不使用 localStorage/sessionStorage 保存服务地址。参考微软的 [ProtectedData 文档](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata.protect)，相同 Windows 用户下的程序属于同一信任范围。

## 组包

```powershell
.\.deps\pwsh\pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\New-PreviewPackage.ps1 -Version 0.1.0-preview.15
```

脚本从固定校验值的原始归档取得便携运行时和 SDK，按白名单组包；附带文件清单、依赖许可、中文说明及 ZIP 的 SHA256。会在独立解压目录里运行包内校验和原生编译。同名产物已存在时拒绝覆盖。

发布前运行 `npm.cmd --prefix ui audit --json`。提交源文件后可运行 `node scripts/Test-PublicPrivacy.mjs` 检查 Git 索引；它不会上传数据。任何日志、缓存、私人配置、私人截图或未审查二进制都不应进入 Git。项目原创部分使用 MIT，组包必须包含根目录 `LICENSE`；第三方许可保留原文。

README 预览图可在 UI 构建后依次运行 `scripts/Capture-ReadmeOverlay.ps1`（PS7，STA）和 `node scripts/Capture-ReadmeScreenshots.mjs` 重建。前两张为真实 React 界面配隔离 fixture，第三张为自制动画视频画面与生产原生渲染器输出的1:1叠合示意，均明确标注演示用途，不访问用户播放器或真实 API。临时 WebM 位于忽略的 `.cache/readme-demo/`，只提交已审查的三张 PNG。

## 质量约定

不能通过跳过测试、删除断言、添加类型抑制或降低门槛来获得通过。播放器状态未知时必须明确显示未知，不用演示数据或手动时钟冒充自动同步。

保留的目标包括：500ms 播放响应、100ms 设置交付、连续10秒暂停零漂移；1080p / 5万条时间轴 / 实际100活动弹幕 / 60秒测试的回调间隔 p95≤20ms、p99≤33.4ms。回调间隔不是实际屏幕帧率，局部通过也不等于上述目标全部达标。

渲染资源上限保持：8192px 单边、16MiB 单位图、64MiB 活动及缓存总像素、512缓存项。真实视频、多屏、长期资源和完整使用流程仍需独立验收。
