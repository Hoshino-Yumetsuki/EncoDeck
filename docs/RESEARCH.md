# 参考项目研究（RESEARCH）

研究日期：2026-10-20。所有仓库均以只读方式克隆分析，**未在本机构建或运行其中任何代码**。协议细节见 [PROTOCOL_FINDINGS.md](PROTOCOL_FINDINGS.md)。

## 结论摘要

| 项目 | 现状 | 许可证（核实结果） | 对 EncoDeck 的作用 | 是否复用代码 |
|---|---|---|---|---|
| [3295074384/OppoPodsWindows](https://github.com/3295074384/OppoPodsWindows) | 存在；单个提交 `5a7f0cc`（2026-09-05） | 完整 GPLv3 文本，无 “or later” 字样，无版权行 → 按 GPLv3 §14 视为 GPL-3.0-only | WinRT RFCOMM 连接方式、帧格式、命令 ID、X3 抓包、FakeTransport 测试思路 | **否**（独立实现；协议事实与少量测试向量见致谢） |
| [Zhaoyi-ya/OppoPodsManager](https://github.com/Zhaoyi-ya/OppoPodsManager) | 已归档（2026-09-30 停止维护），最后提交 `96eddba` | GPL-3.0-or-later，`Copyright (C) 2026 Zhaoyi-ya` | 能力位图解析、功能 ID、型号能力表的组织方式、连接弹窗/低电量逻辑 | **否** |
| [Leaf-lsgtky/OppoPods](https://github.com/Leaf-lsgtky/OppoPods) | 活跃（v1.4.0，`feb7b70`，2026-10-04） | README 声明 GPL-3.0，但**仓库无 LICENSE 文件**、无版权持有人 | 协议研究笔记（变长 LEN、链路控制字节、降噪父/子索引） | **否**（仅参考事实） |
| [HavenDV/H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) | 活跃；稳定版 2.4.1（2025-12-01） | MIT | 系统托盘（NuGet 依赖） | 以 NuGet 包形式使用 |
| [microsoft/WinUI-Gallery](https://github.com/microsoft/WinUI-Gallery) | 活跃 | MIT | Fluent 控件、Mica/Acrylic、Composition 动画的用法参考 | **否** |
| 欢律 Android 客户端（用户提供的 APK，`com.heytap.headset` 17.6.5） | — | 专有软件 | 互操作研究：命令表、帧格式、Enco X4 型号配置（降噪模式树、EQ 预设名） | **否**：不分发 APK、代码、资源、密钥或解密后的文件，只记录互操作所需的事实 |

**重要：三个社区项目都没有 Enco X4 的数据**（它们共享的 137 条型号表中没有 X4）。EncoDeck 中的 X4 数据全部来自本机实测和官方客户端的型号配置，见 [COMPATIBILITY.md](COMPATIBILITY.md)。

## 1. OppoPodsWindows

- **结构**：.NET 8 + WinUI 3（WindowsAppSDK 1.6、H.NotifyIcon.WinUI 2.3.0），unpackaged 自包含；`Core`（帧/解析）、`Session`（WinRT 传输）、`App`、`Probe`、两个测试项目。
- **蓝牙**：`GetDeviceSelectorFromPairingState(true)` 监视已配对设备；`FromBluetoothAddressAsync` → `GetRfcommServicesAsync(Uncached)` → 取 SDP 顺序中第一个匹配 `00001107-…`/`0000079A-…` 的服务 → `StreamSocket.ConnectAsync(…, BluetoothEncryptionAllowNullAuthentication)`。
- **协议**：`AA LEN 00 00 CMD(LE) SEQ PLEN(LE) payload`，响应 `CMD|0x8000`；部分命令固定 SEQ（`F0`/`00`）；初始化 `0100 → 0103 → 0200 → 010D/0106/010C/010F`，30 秒轮询刷新。
- **已验证设备**：README 仅声明 Enco X3（PID `067410`）。
- **发现的问题**（EncoDeck 已规避，并有回归测试）：
  1. 带 `01 01` 的单耳电量事件会被误解析为降噪变化；
  2. 电量响应把 `[状态][数量]` 当成一对数据；
  3. UUID 选择依赖 SDP 顺序；
  4. 仅控制通道断开（ACL 仍在）时不会重连；
  5. 无请求/响应关联、不检查状态字节；
  6. `BluetoothDevice` 未释放；Probe 的 “SDP 列表” 并不列出服务。
- **测试**：`FakeTransport`（记录发送、模拟接收/远端关闭）思路被 EncoDeck 的 `FakeByteChannel`/`FakeOppoDevice` 借鉴（重新编写）。

## 2. OppoPodsManager

- Avalonia UI（不采用），Windows 上用原生 Winsock RFCOMM；仅使用 `0000079A-…`。
- 解析 `0x8100` 能力位图（67 位 → 命令表）；`0x0105` 固件 CSV、`0x0109` 佩戴状态、`0x0400` 查找耳机、`0x0112/0x0429` 多设备等命令定义。
- 弹窗/低电量提醒、设置项组织方式可作参考；与 OppoPods 在降噪子索引和 EQ 索引的用法上互相矛盾（EncoDeck 以实测为准）。

## 3. OppoPods（Leaf-lsgtky）

- Android/HyperOS（Xposed）实现，协议来自对官方客户端的观察；作者实机：Enco X3（固件 143.143.039）、Enco Free4。
- 笔记指出：LEN 是 7 位变长整数、第 3 字节是链路控制（分片）字节——EncoDeck 已按官方客户端确认并实现。
- 无 LICENSE 文件：EncoDeck 不复制其任何代码，只引用协议事实。

## 4. H.NotifyIcon.WinUI（2.4.1，MIT）

验证结论（本机 smoke 测试，详见 [DEVELOPMENT.md](DEVELOPMENT.md)）：
- 无窗口时用代码创建 `TaskbarIcon`，并调用 `ForceCreate(false)`——**默认参数会为整个进程开启效率模式（EcoQoS + 空闲优先级）**；
- WinUI 下退出前必须手动 `Dispose()`，否则残留“幽灵”图标；配合 `DispatcherShutdownMode.OnExplicitShutdown`；
- 原生菜单模式（`PopupMenu`）下菜单项需用 `Command` 而非 `Click`；
- 内置 `TrayPopup` 需要已有窗口，故 EncoDeck 自己实现托盘面板，用 `Shell_NotifyIconGetRect` 定位（图标在溢出区时返回 `^` 按钮位置）；
- 默认托盘 ID 由 exe 路径生成——**不要固定 GUID**，否则移动安装目录后图标注册失败。

## 5. 微软 WinUI Gallery（MIT）

仅作为控件与样式用法参考（NavigationView、InfoBar、Mica/DesktopAcrylic、Composition 关键帧动画、无障碍属性），未复制源码或资源。

## 6. 欢律客户端（互操作研究）

用户提供了自己的客户端安装包并要求以其为参考。分析在本机进行：
- 反编译后确认帧格式、命令/事件编号、能力位图表、各 payload 结构（见 PROTOCOL_FINDINGS）；
- 读取客户端内置的型号配置，提取 **Enco X4（06EC10）** 的降噪模式树（protocolIndex）、EQ 预设编号与名称、自定义 EQ 频段等事实；
- 不记录、不分发任何密钥、代码、图片、字体或配置文件；官方产品图片与字体不进入本仓库。

## 7. 依赖与许可证兼容性

| 依赖 | 许可证 | 随应用分发 | 备注 |
|---|---|---|---|
| .NET 10 运行时（自包含） | MIT | 是 | |
| Microsoft.WindowsAppSDK 2.5.1（含 WinUI、WebView2 加载器） | Microsoft Software License Terms（可再分发） | 是 | **专有许可**，见下 |
| H.NotifyIcon / H.NotifyIcon.WinUI / H.GeneratedIcons.System.Drawing 2.4.1 | MIT | 是 | |
| System.Drawing.Common 10.0 | MIT | 是 | H.NotifyIcon 依赖 |
| CommunityToolkit.Mvvm 8.4.2 | MIT | 是 | |
| Microsoft.Extensions.* 10.0.13 | MIT | 是 | |
| Microsoft.Windows.SDK.BuildTools | Windows SDK 许可 | 否（仅构建） | |
| xunit.v3、Microsoft.NET.Test.Sdk | Apache-2.0 / MIT | 否（仅测试） | |

**待维护者决定的事项**：EncoDeck 采用 GPL-3.0-only。自包含发行包同时包含以微软专有条款再分发的 Windows App SDK 运行时。GPLv3 的“系统库”例外是否覆盖 Windows App SDK 存在争议。常见做法是由版权持有人依据 GPLv3 §7 增加“附加许可”（允许与 Windows App SDK / .NET 运行时链接与一起分发）。由于 EncoDeck 未复制任何第三方 GPL 代码，版权持有人可以自行添加该附加许可；在维护者明确决定前，本仓库保持纯 GPL-3.0-only 文本，并在 THIRD_PARTY_NOTICES 中说明。
