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

真实 API 检查默认不运行、不附带账号。需要时在本机为测试进程配置 `DANMU_API_BASE` 环境变量；不要把真实值写入源码、命令示例或提交历史。桌面入口也可读取这个由用户显式提供的环境变量，`-NoDefaultApi` 可禁用读取。普通使用直接在界面填写即可。

## 组包

```powershell
.\.deps\pwsh\pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\New-PreviewPackage.ps1 -Version 0.1.0-preview.13
```

脚本从固定校验值的原始归档取得便携运行时和 SDK，按白名单组包；附带文件清单、依赖许可、中文说明及 ZIP 的 SHA256。会在独立解压目录里运行包内校验和原生编译。同名产物已存在时拒绝覆盖。

发布前运行 `npm.cmd --prefix ui audit --json`。提交源文件后可运行 `node scripts/Test-PublicPrivacy.mjs` 检查 Git 索引；它不会上传数据。任何日志、缓存、私人配置、截图或未审查二进制都不应进入 Git。

## 质量约定

不能通过跳过测试、删除断言、添加类型抑制或降低门槛来获得通过。播放器状态未知时必须明确显示未知，不用演示数据或手动时钟冒充自动同步。

保留的目标包括：500ms 播放响应、100ms 设置交付、连续10秒暂停零漂移；1080p / 5万条时间轴 / 实际100活动弹幕 / 60秒测试的回调间隔 p95≤20ms、p99≤33.4ms。回调间隔不是实际屏幕帧率，局部通过也不等于上述目标全部达标。

渲染资源上限保持：8192px 单边、16MiB 单位图、64MiB 活动及缓存总像素、512缓存项。真实视频、多屏、长期资源和完整使用流程仍需独立验收。
