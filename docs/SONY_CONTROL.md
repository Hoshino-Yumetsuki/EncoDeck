# Sony 控制通道连接修复（2026-10-25）

## 诊断依据

`EncoDeck-diagnostics-20261025-125034.zip` 记录应用 1.7.0、WH-1000XM6、Windows 10.0.26300 x64。控制 socket 多次打开后约 7–31 ms 在读取时报告 `0x80072745`（WSAECONNABORTED），还有一次连接超时；最终进入 `ControlUnavailable`。没有电量、固件或降噪读数。该错误说明控制流中止，不足以确定是设备、Windows 蓝牙栈、其他客户端占用还是协议不兼容。原始协议日志未启用，不能据此声称已找到唯一根因。

## 官方 App 核对

使用工作区已有的 Sony Sound Connect Android APK，包名 `com.sony.songpal.mdr`，版本 12.5.1，versionCode 120515029。APK 位于忽略目录 `artifacts/apk-research/sony-official.apk`，SHA-256：

`E6C5AC8085D5CB785A9391B53EF96944C3AAC9DCD20512794A4A4847E819E1A2`

对应反编译资料位于 `artifacts/apk-research/sony/sources/`。不把 APK 或反编译源码纳入分发，仅记录核对结果：

| 官方类 / 方法 | 行为 | 本次处理 |
| --- | --- | --- |
| `p759yl.i` | 按 `956c7b26-d49a-4ba8-b03f-b17d393cb6e2`、`96cc203e-5068-46ad-b32d-e316f5e069ba` 顺序提供服务 | 保留顺序，不按型号名称强制替换 UUID |
| `Rm.b.a` | `createRfcommSocketToServiceRecord`，安全 RFCOMM socket | Sony 在 Windows 使用 `BluetoothEncryptionWithAuthentication`；其他品牌保留原配置 |
| `Wl.c`、`Wl.d`、`Hl.C1195y` | table-1 协议查询 `00 00`，v2 功能查询 `06 00` | 核对现有握手，无需改写报文 |
| `Dl.b`、`Pm.d`、`p759yl.c` | ACK 用接收序号的反码；重发保留原始帧及序号 | 保留序号语义，新增原帧重传 |
| `Bl.e.k`、`p759yl.c.n` | 750 ms 等待 ACK，最多重发十次；耗尽后结束会话 | 对齐重传上限；断开或取消时立即停止，避免复用不确定序号 |
| `DataType.ackRequired`、`Bl.f` | common、table-2、其他 DATA 也需要确认；重复帧仍需 ACK | 先 ACK，再按共享接收序号去重；仅解析已实现的 table-1，防止跨表 opcode 混淆 |

Windows 枚举依据：[SocketProtectionLevel](https://learn.microsoft.com/en-us/uwp/api/windows.networking.sockets.socketprotectionlevel)。Android 和 Windows 的蓝牙 API 不同，此处对齐安全连接要求，仍需实机确认具体适配器和配对的兼容性。

另外补上 EncoDeck 自身的恢复机制：Sony 握手失败后，下次有界重试优先尝试另一候选服务；成功后优先复用已验证服务。每次 Sony SDP 解析使用 `Uncached`，避免持续连接旧端点。这两项是 Windows 客户端的恢复策略，不宣称来自官方 App。连接日志新增服务、RFCOMM 通道、保护级别、握手进度与 ACK 超时次数，不记录设备地址或原始个人数据。

## 验证与真机复测

`SonyConnectionTests` 覆盖其他表阻塞发送队列、重复通知确认、错误 / 丢失 ACK 后原帧重传、连接立即中止；`SonyControlConnectionTests` 使用内置 XM6 profile 和真实控制器经过生产 supervisor，覆盖主服务握手失败后切换备用服务，以及重连时保留成功服务。静默服务测试确认最多发送十一帧并结束会话。模拟测试不代表 XM6 真机连接验证。

关闭旧版 EncoDeck，再运行修复版。先在 Windows 中连接 XM6，观察是否出现电量和 `ControlReady`。首次排查可暂时断开手机和其他控制客户端，以区分并发连接影响；不要把这个操作当作已经证实的必要条件。

如仍失败，导出修复版诊断包，重点查看 `RFCOMM connecting` 的保护级别与服务、`Sony handshake failed` 的备用服务切换，以及 `Sony MDR protocol ... confirmed` 是否出现。不要仅凭播放音频成功判断控制通道可用。
