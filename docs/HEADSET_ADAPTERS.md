# AKG Hybrid、森海塞尔与国产 USB 接收器适配

日期：2026-10-24。EncoDeck 与 EncoAPI 分别在独立工作区的 `feat/headset-adapters` 分支开发。未合并 main，EncoDeck 未发布正式版；EncoAPI 已从该 feat 分支同步生产目录。

## 当前实现

| 范围 | 已实现 | 明确限制 |
|---|---|---|
| AKG N5 Hybrid / N9 Hybrid，蓝牙 | 从仅识别改为 Airoha RACE 控制会话；优先官方 Airoha SPP UUID，回退标准 SPP；agent/partner 查询及左右耳映射、电量、盒电量；按实际返回的 6/12 字节 ANC 配置开放关闭、降噪、环境声；保留设备原有增益与模式字段，写入后回读确认 | 不是旧款 AKG BES 协议。未知 ANC 布局不写入；未实现 EQ、手势、固件升级；尚无本地 N5/N9 真机验证 |
| 森海塞尔 GAIA v3，共 11 个型号配置 | MOMENTUM 4、ACCENTUM、ACCENTUM Plus、MOMENTUM TW 3 / TW 4、ACCENTUM True Wireless、MOMENTUM Sport、CX True Wireless、SPORT True Wireless、CX Plus True Wireless、CX 500；RFCOMM 与 BLE 回退；电量、充电、固件、型号；头戴三款在读取确认后开放 ANC/通透两个端点；通知订阅与每 30 秒刷新 | 不能据此声称所有森海塞尔机型都支持。旧 GAIA / Nautilus 型号未实现；TWS 降噪和 EQ 暂未开放。MOMENTUM 4 没有推测“ANC 关闭”命令。Windows BLE、各固件及真机控制待验证 |
| 漫步者 HECATE、达尔优、雷柏，18 个 USB 型号记录 | USB 音频输出端点发现；同容器 USB 硬件祖先校验；型号名称匹配；VH800 Pro 按官方 INF 的 `USB\VID_24AE&PID_7205` 精确识别；标准 HID BatteryStrength / RelativeStateOfCharge 百分比读取；每 30 秒刷新 | 这是接收器识别与条件性标准电量支持，**不是这些型号完整的厂商协议适配**。未声明标准百分比时显示未知，不猜厂商报告偏移。不控制厂商 EQ、7.1、灯效或升级。接收器存在不等于耳机无线链路已建立 |

USB 型号：HECATE G2 无线版、G3 无线版、G3 Max、G4S、G4 Max、G5 无线版、G5 Max；DAREU A710、A710X、A3 Pro、A3 Max、A5 Pro、A5 Max、EH925 ULTRA、EH722X ULTRA；RAPOO VH800、VH800Pro、VH850。

USB 状态单独显示“USB 接收器已连接，耳机无线状态待确认”，不伪装为蓝牙控制就绪。麦克风输入端点、名称相似的非 USB 设备及无关 PID 不会作为支持的接收器。

## 官方资料与研究输入

- [AKG N5 官方说明书](https://uk.akg.com/on/demandware.static/-/Sites-masterCatalog_Harman/default/dwdeae07ca/pdfs/AKG%20N5%20Hybrid_QSG_Multilingual.pdf)。AKG Headphones `com.harman.akgn20lt` 3.5.20 的目录把 N5/N9 标为 Airoha；核对 `UuidTable`、`RacePacket`、`MmiStageCheckAgentChannel`、`MmiStageTwsGetBattery`、`MmiStage_GetBoxBattery`、`SDKAncSettings` 等实现。APK SHA-256：`e6314d5282a5375d2a20484064b07109b65b74a1ee118847dbdfccf19067ebe8`。
- Sennheiser Smart Control `com.sennheiser.control` 4.9.3 的 base APK 及 xxxhdpi split；本次自行下载 XAPK 并解包，不要求用户提供。base APK SHA-256：`194be9f1d9fa8fde255c49e87cc6ab5c85d6e01a572fdd6c6053ed65bbe101de`。直接核对 `assets/app/*.json`、`features/*.json` 与 `bundle.js`；另以 [sennheiser-control 的协议研究](https://github.com/xndbogdan/sennheiser-control/blob/main/PROTOCOL.md) 交叉核对帧结构。未复制第三方控制器代码。
- [漫步者官方驱动下载](https://www.edifier.com/cn/driver.html)、[达尔优官方驱动下载](https://dareu.com/pages/driver)、[达尔优官方耳机目录](https://dareu.com/collections/headphones)、[雷柏官方驱动下载](https://www.rapoo.cn/downloadcenter/?model=VH800)。本次下载并静态解包 Rapoo Headset Control 1.0.0.2，核对 `VH800PRO.Inf` 与驱动 UI 的电量代码。**没有运行或安装厂商驱动**。达尔优安装包下载超时；漫步者当前整包约 865 MB，本次未完整下载。

雷柏驱动的专有 `0x55 / 0x65 / 0x01` 入口只证明存在查询代码，尚不足以确认响应布局和真实链路状态；没有把它套用到其他型号，也没有将返回写成功当作电量响应成功。

APK、解包输出、安装器和提取工具只放在忽略的 `artifacts/headset-research`，不随代码或客户端分发。图片版权仍归各自权利人。

## EncoAPI 同步

独立工作区：`D:\develop\encoapi.worktrees\feat\headset-adapters`。

- 新增 `sennheiser`、`dareu`、`rapoo` 品牌，模型 ID 验证、OpenAPI、首页目录同步。
- 新增 11 个森海塞尔型号和 48 组精确配色照片，来自对应官方 APK 资源；按内容哈希打包并校验。
- 新增 18 个 USB 型号记录；其中 9 个有精确产品图，9 个没有匹配素材，明确保留 `artworkAvailable=false`，不借用邻近型号图片。
- N5/N9 标注实际 Airoha RACE 传输；USB 记录标注条件性电量、无专有控制、未真机验证。
- 客户端的 `OfficialArtworkCatalog.g.cs` 从同一 EncoAPI 索引重新生成，未夹带图片。

## 验证与复现

```powershell
dotnet test tests/EncoDeck.Tests -c Release
dotnet build src/EncoDeck.App -c Release -p:Platform=x64
dotnet run --project tools/EncoDeck.Probe -c Release -- usb-devices
# AKG / 森海塞尔真机配对后，使用应用选择该型号；监控工具只读模式也可观察会话。
dotnet run --project tools/EncoDeck.Probe -c Release -- monitor --seconds 30
```

新测试覆盖：RACE 分片和拼包；GAIA v3 响应/错误位、厂商 ID、标志位和校验；BLE PDU 转换；N5/N9 左右角色切换；两代 ANC 配置的增益/模式保留、拒绝写入及回读；森海塞尔无 ANC-off；USB 传输隔离、相似型号边界、精确 VID/PID、百分比单位校验。

验证机只发现 OPPO Enco X4 与手机，未发现上述新型号。模拟设备回归不等于真机验证，USB 的标准电量路径亦不能证明上述全部接收器都提供该报告。

最终本地验证：EncoDeck 549/549 测试通过；x64 Release 构建及自包含测试包生成成功。EncoAPI 50/50 测试通过，28 个有图的新型号均完成本地实际取包和哈希验证，17 个无图型号明确保留缺失状态。模拟测试不构成真机验证。

本地测试包：`artifacts/EncoDeck-headset-adapters-test-win-x64.zip`。SHA-256：`cad2e55c2b352f7abc8d00101923e9a2cb3d1d4f2a29f8ae7ececd7506a763cc`。这不是 GitHub Release 或商店提交。

后续新增迈从 / 雷蛇及水月雨 APK 动画检查见 [补充报告](MCHOSE_RAZER_MOONDROP.md)。最终验证数据以该补充工作完成后的结果为准。
