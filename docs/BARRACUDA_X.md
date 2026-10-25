# Razer Barracuda X 2.4

## 官方工具静态分析

从 [Razer 官方下载页面](https://mysupport.razer.com/app/answers/detail/a_id/14722/kw/barracuda%20x%20software)
的配对工具下载链取得 `RazerHeadsetPairingUtility_v1.11.00_r1.exe`，只提取、反编译，没有运行配对或更新程序。
当前支持页面列出较新版本；本次实际分析的是 v1.11.00_r1，不能把结果当作新版二进制分析。

- 安装包 Authenticode：Valid，签名者 Razer USA Ltd.
- 安装包 SHA-256：`870B486182F786CACE7985EA137032905D686422798C04E2FF42649310DE3BCE`。
- 内含 `AWToolLIB2.dll` SHA-256：`8B313D47472FB5F8113571912FBF2F7D6355EC2132652BB767E9B706B3C89F32`。
- 官方 `Common` 类型将 `1532:0552` 定义为 `BarracudaX_BT_Dongle_AW_VidPid`，
  `1532:0550` 定义为 `BarracudaX_BT_Dongle_AW_RP_VidPid`；产品名为 `Razer Barracuda X 2.4`。
- `TwsTester.hid.HID.MXIC_GetMode_IsAPP` 使用命令 `40` 读取应用模式；
  `ReadCompleted_MXIC` 处理 `01 80 LEN ...` 接收分块。模式切换命令是另一个命令 `41`，本实现不发送。
- `TwsTester.cmd_const.Link_Quality_Commands` 定义 `E0` 读路由、`E1` 选路由、`E3` 读连接。
  `DeviceObj.send_REMOTE_SET_MODE_imp` 封装 `50 41 0e SEQ 02 e1 MODE`。
- `DeviceObj` 的 customer data 通道使用 family `08`，OTA `GET_BATTERY` (`31`) 返回电压，
  不能直接把电压当作百分比。

原始官方包、反编译代码仅保留在工作区外的 `D:/develop/EncoDeck-research/barracuda-x/`，不加入仓库。

## 本次接入范围

只向精确 `1532:0552`、usage page `ff00`、usage `1` 的集合发送查询。
通过 WinRT HID 输入事件接收数据，按长度重组 PI 消息；校验成功 ACK、命令和有效值。

1. 读取应用模式，收到 `01 40 01 01` 后继续。
2. `E3` 查询无线连接，只接受匹配 ACK 之后的 `e3 00/01`；USB 枚举本身不算无线连接。
3. 无线连接成立后先以 `E0` 确认本地路由，避免接管其他程序正在使用的远程路由；
   `E1 01` 暂选耳机路由，family `08` 查询 `21 00 00` 电量与 `2a 00 00` 充电线。
4. 无论成功、失败还是取消，均尝试 `E1 00` 恢复本地路由并用 `E0` 读回。恢复等待有独立时限。

百分比及线缆字段的语义结合 [0552 协议实测记录](https://github.com/leag/razer-barracuda/blob/main/docs/PROTOCOL.md)；
官方配对工具的通用 customer data 方法本身不证明 `21/2a` 在每个固件上可用。
电量只显示设备直接报告的 0–100；不从电压估算、不自动补成 100%。线缆连接字段不能证明充电完成。
每次重新读取时若无有效回答就清除旧遥测，并退回接收器状态。

`0550` 使用另一种带 CRC 和 GetInputReport 握手的封装，当前仍只识别名称，不发送本协议。
其他年份的 Barracuda X、Chroma、Pro 不共用此查询许可。没有启用 EQ、游戏模式、配对或固件写入。

## 验证

协议单测覆盖命令字节、设备/集合过滤、ACK 序列、连接字段、有效值、分片及合帧。
本机没有发现雷蛇 USB 接收器，尚未完成 EncoDeck 的实机验证。
连接 `0552` 接收器后可运行：

```powershell
dotnet run --project tools/EncoDeck.Probe -- usb-devices
```
