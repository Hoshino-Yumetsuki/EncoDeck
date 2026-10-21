# 开发指南（DEVELOPMENT）

## 环境

| 组件 | 版本（2026-10 实测可用） |
|---|---|
| Windows | 10 19041+ / 11（开发机：Windows 11 build 26340 x64） |
| .NET SDK | 10.0.402（`global.json` 要求 10.0.100+，`latestFeature` 前滚，禁止预览版） |
| Windows App SDK | 2.5.1（稳定版） |
| Windows SDK 目标框架 | `net10.0-windows10.0.26100.0`（`10.0.28000.0` 在 SDK 10.0.402 上报 NETSDK1140） |
| H.NotifyIcon.WinUI | 2.4.1（稳定版；2.5.0 仅有预览版） |
| CommunityToolkit.Mvvm | 8.4.2 |
| xunit.v3 | 3.2.2 + xunit.runner.visualstudio 3.1.5（VSTest） |

**不需要** Visual Studio、MSIX 工具或任何 workload：`dotnet build` 即可编译 XAML。所有包版本集中在 `Directory.Packages.props`。

## 常用命令

```powershell
# 全部库 + 测试 + Probe
dotnet build EncoDeck.slnx -c Debug

# WinUI 应用（需要指定平台）
dotnet build src/EncoDeck.App -c Debug -p:Platform=x64

# 单元测试
dotnet test tests/EncoDeck.Tests -c Debug

# 发布（unpackaged，自包含）
dotnet publish src/EncoDeck.App -c Release -r win-x64 -p:Platform=x64 -o artifacts/EncoDeck-win-x64

# 诊断工具
dotnet run --project tools/EncoDeck.Probe -- help
```

## 项目结构

```
src/EncoDeck.Core        领域模型、状态存储、能力系统、策略（net10.0）
src/EncoDeck.Protocol    帧/会话/解析/型号定义/设备控制器（net10.0）
src/EncoDeck.Bluetooth   WinRT 蓝牙：发现、SDP、RFCOMM、监督器、DeviceManager
src/EncoDeck.Platform    设置、日志、缓存、开机启动、单实例、Shell 查询
src/EncoDeck.App         WinUI 3 界面（unpackaged）
tools/EncoDeck.Probe     无界面诊断工具
tools/assets             图标与 .resw 生成脚本
tests/EncoDeck.Tests     单元测试（含 X4 实测抓包回归）
```

## 已知的坑（均已在本仓库中处理）

1. **发布必须 `EnableMsixTooling=true`**：否则 `dotnet publish` 不复制 `EncoDeck.pri`（编译后的 XAML），发布版启动即 `XamlParseException`；Debug 输出目录不受影响，容易漏掉。
2. **`TaskbarIcon.ForceCreate()` 默认开启效率模式**（EcoQoS + 空闲优先级，作用于整个进程），必须传 `false`。
3. **WinUI 下托盘图标不会自动移除**：退出前调用 `Dispose()`，并设置 `DispatcherShutdownMode = OnExplicitShutdown`，用 `Application.Exit()` 退出。
4. **原生托盘菜单（PopupMenu 模式）忽略 `Click`**，菜单项必须用 `Command`；菜单不跟随深色主题。
5. **不要固定托盘图标 GUID**：Windows 会把 GUID 与 exe 路径绑定，移动目录后注册失败。
6. **自包含 unpackaged 下 `AppNotificationManager.Register()` 抛 `0x8007007E`**（WinAppSDK 2.5.1，上游 #6774）；EncoDeck 使用托盘气泡通知代替 Toast。
7. `WMC9999 未将对象引用设置到对象的实例` 往往只是前面 C# 编译错误的连锁反应，先修 C# 错误。
8. `LibraryImport` 需要 `AllowUnsafeBlocks`。
9. x:Bind 函数绑定调用的是实例方法；静态辅助方法放在独立类型（如 `Services/Ui.cs`）中并写全名。
10. `AppWindow` 的尺寸/位置是物理像素，需要乘以 DPI 缩放。
11. `BluetoothDevice.GetDeviceSelectorFromPairingState(true)` 的结果偶尔包含 `FromIdAsync` 拒绝的条目，必须逐个容错。
12. **设置文件里缺少的 `init` 属性读成类型默认值**（`false` / `0`），不是声明里的默认值：默认开启的新开关要用 `bool?` 并在 `Normalize()` 补回（见 `ShowAboveQuickSettings`、`PopupScale`）。
13. **Windows 11 快速设置没有扩展 API**（`QuickSettingsWatcher`）：在 26H2 build 26340 上实测，它是 `ShellHost.exe` 的 `ControlCenterWindow`，窗口和工作区一样高且透明，只有窗口区域（`GetWindowRgnBox`，卡片加 12 DIP 外边距）可见、能点击；UIA 读不到内部元素。它一失去前台就开始约 300 ms 的关闭动画然后才 HIDE，所以面板在前台切走时就退场。面板必须 `WS_EX_NOACTIVATE`，否则一点它快速设置就关闭。更早的 Windows 11 版本若不是这个窗口类，功能静默不生效。

## 部署说明（unpackaged）

- `WindowsAppSDKSelfContained=true` + `SelfContained=true`：Windows App SDK 运行时与 .NET 运行时都放在应用目录，用户无需安装任何运行时；不需要 VC++ 运行库（已检查原生 DLL 导入表）。
- 实测发布目录约 178 MB / 479 个文件（已排除 WinAppSDK 的 AI/ML/Search/Widgets 组件），包含 `EncoDeck.exe`、`EncoDeck.pri`、`Microsoft.WindowsAppRuntime.dll`、`Microsoft.ui.xaml.dll`。
- 已在本机验证发布版：`--background` 启动 → 发现 X4 → `--exit` 正常退出（退出码 0）。**尚未**在“干净”的、从未安装过开发工具的机器上验证；发布前请在虚拟机中复测。
- 单文件发布会解压到 `%TEMP%`，不推荐；用户数据始终写入 `%LOCALAPPDATA%\EncoDeck`。
- ARM64：项目已配置 `win-arm64`，但尚未构建测试，CI 只发布 x64。

## 命令行参数

| 参数 | 作用 |
|---|---|
| `--background` | 启动后只驻留托盘（开机启动使用） |
| `--exit` | 让已运行的实例干净退出（移除托盘图标、关闭控制通道） |
| `--popup-preview <boot.mp4> <输出目录> [--theme light\|dark] [--name 名称] [--levels 80,75,60] [--at 0.3,1,2.5,4] [--offscreen]` | 诊断：不连耳机，用任意资源包视频（自动找上级 `config.json`）和示例电量显示连接弹窗，在指定秒数截图（屏幕 + PrintWindow）并写 `<前缀>.json` 后退出；不启动蓝牙、不写设置、不抢焦点，有全屏应用时自动改为屏幕外 |
| `--updated` | 由安装程序在应用内更新完成后传入：无全屏应用时打开设置页，否则只发托盘通知 |
| 再次启动 | 唤醒已运行实例的主窗口（单实例） |

## 数据位置

`%LOCALAPPDATA%\EncoDeck\`：`settings.json`、`device-cache.json`（哈希键，无地址）、`logs\encodeck-yyyyMMdd.log`（JSON Lines，保留 7 天，已脱敏）、`diagnostics\`、`Resources\`（设备素材缓存，见 [RESOURCES.md](RESOURCES.md)）、`Updates\`（应用内更新下载的安装包、`last-check.txt`、`install.log`）。

## 应用内更新

`UpdateManager`（`src/EncoDeck.Platform/Updates`）匿名查询 `https://api.github.com/repos/TIANLI0/EncoDeck/releases`：跳过草稿；未开启“接收预览版”时跳过预发布（GitHub 标记或带 `-` 后缀的标签）；按 SemVer 取最高版本中带有 `EncoDeck-Setup-<版本>-win-x64.exe` 的发布。下载时校验大小，并按 `<安装包>.sha256` / `SHA256SUMS` 资产（或 GitHub 资产的 `digest`）校验 SHA-256，完成后才原子重命名。开启“自动检查更新”时启动约 1 分钟后检查，之后每 24 小时一次（时间记录在 `Updates\last-check.txt`），发现新版本即后台下载并发一次托盘通知（全屏时延后）；只有用户点击“立即重启并更新”才会以 `/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1` 启动安装程序并退出，安装完成后 `[Run]` 以 `--updated` 重新启动 EncoDeck。

## 新增设备型号

1. 用 Probe 收集证据（`services`、`query`、`watch`），见 [TESTING.md](TESTING.md)。
2. 欢律型号列表中的型号由 `python -I tools/assets/make_models.py <whitelist.json> src/EncoDeck.Protocol/Oppo/Models/OppoCatalog.g.cs` 生成（只写互操作事实），解读规则在 `OppoCatalog.cs`；有真机证据的型号可以像 X4 一样在 `OppoModels.cs` 中手写定义，并用测试保证与配置生成的结果一致。
3. 把实测抓包（去除事件 06 等敏感数据）加入测试。
4. 更新 COMPATIBILITY.md 与 PROTOCOL_FINDINGS.md。

## 代码规范

- 每个源文件以 `// SPDX-License-Identifier: GPL-3.0-only` 开头。
- 协议解析器必须严格：格式不符返回 false，而不是猜测。
- 任何写命令都要：能力门控 → 确认 → 回读 → 失败回滚。
- 日志中不得出现完整蓝牙地址；涉及设备列表的帧用 `OppoRedaction`。
