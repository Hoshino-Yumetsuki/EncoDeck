# 设备兼容性

以下为当前开发版的设备与功能列表；正式版以对应版本为准。同一行设备具有相同的功能范围。实际可用项仍取决于固件和设备返回的能力；目前主要在 OPPO Enco X4 上完成实机验证。

## 水月雨 MOONDROP

依据 [MiuixMoondrop](https://github.com/huime180/MiuixMoondrop/tree/2f78f6740b2f30d1c28d75ba05badb98b2eeb687) 移植。下列“上游验证”仅指参考项目的 Android / 其他平台记录，**不是 EncoDeck 的 Windows 真机验证**；本项目已验证模拟字节通道、控制器和实际 WinUI 页面。

| 型号 | 当前范围 |
| --- | --- |
| PUDDING / 布丁 / MD-TWS-056 | RFCOMM GAIA；左右耳 / 盒电量（无数据的组件保持未知），关闭 / 降噪 / 通透 / 抗风噪 / 自适应，提示音量 0–100、提示音开关、增益、LED、双耳 TOUCHV2 手势，LHDC 与双设备连接开关；均需实际能力与状态回包 |
| EDGE / 羽翼 | BLE GAIA；单设备电量，关闭 / 降噪 / 通透，增益，双设备连接（需能力回包）；其他发现功能按实验性策略开放 |
| GOLDEN AGES 2 / 梦回2 | BLE GAIA；电量，关闭 / 降噪 / 通透 / 抗风噪，独立的读写模式映射；增益仅在固件实际提供 feature 15 且读回状态时开放，不保证 GA2 固件具备；TOUCHV2 等非确认功能须实验性开关 |
| SPACE TRAVEL 2 / 太空漫游2 | 识别及 GAIA 兼容路径；仅当固件实际提供 GAIA 服务与能力回包时可读电量和控制降噪 / 抗风噪、增益；只有 9ECA 服务的固件不可控制 |
| GOLDEN AGES、MOCA、NEKOCAKE、MUSIC PILL、ULTRASONIC、ROBIN、ALICE、SPARKS、VOYAGER、BLOCK、SPACE TRAVEL、EDGE2、SPACE TRAVEL 2 ULTRA | 识别和探测；型号表主要来自芯片级推断，发现的推断功能须显式开启实验性功能，不代表整机支持 |
| 名称含 MOONDROP / 水月雨但未匹配型号 | 默认通用 GAIA 只读探测，不默认开放推断的降噪写入 |

- 特有选项不作为全局功能显示：抗风噪仅在当前型号提供该档位、控制通道就绪且已选中降噪时出现；切换型号、失去连接或设备能力排除 ANC 后隐藏。
- 能力分页探测用于排除固件未提供的功能；广告中的 feature ID 不会把未知型号的模式映射自动标成已验证。
- LHDC 和双设备连接互斥，开启一项会先关闭并回读确认另一项。LHDC 这里只是耳机端开关，不等于给 Windows 安装编码器。
- 提示音开关和音量均保留完整三字节 `[enabled,volume,index]` 的其他字段；全部设置经回读确认，不把发送成功当成设备已生效。增益 UI 索引 0 / 1 / 2 为低 / 中 / 高，经型号目录映射为设备值。
- TOUCHV2 为五字节双耳半字节配置；支持单击、双击、三击、长按 1 秒 / 3 秒，同耳两种长按互斥。动作 4 / 5（音量）明确标为推断且需实验性开关；未知动作 8–15 仅显示 / 保留读回值，不可选择写入。
- RFCOMM 先探测带帧封装、再探测裸 CSR API version；固定长度回包增量解析，可变长度回包依赖 40ms 静默分段。任意可变长消息粘包、payload 内长停顿仍不可靠且未获硬件验证。BLE 连接使用经典蓝牙地址，地址不同的双模固件尚需硬件验证。
- 空间音频与头部跟踪始终为 Unsupported，不属于实验性功能。低延迟（仅见 HyperOS 设置）、EQ / 9ECA、双设备详细管理 / 固件信息，以及佩戴 / 充电 / 查找 / 贴合度 / 降噪循环 / OTA 均未支持。
- 本轮验证：29 项 MOONDROP 定向测试、全套 509 项测试通过；WinUI x64 Debug 构建通过；实际控制器的有状态模拟 GATT 冒烟覆盖增益映射、LED、并发提示音修改、双耳互斥与未知值保留、拒绝写入和 pending 清理。实际 PUDDING / EDGE / GA2 / OPPO 页面截图及可视树测量覆盖实验性开关、pending、断连、未知状态、能力排除、恢复和未连接写入回退。证据位于 `artifacts/moondrop-verification`、`artifacts/moondrop-complete-ui`、`artifacts/moondrop-edge-complete-ui`、`artifacts/moondrop-ga2-complete-ui`、`artifacts/oppo-moondrop-regression-ui`；**没有 Windows MOONDROP 真机验证，17 个档案不等于 17 型号完整支持**。

## OPPO / 一加 / realme / DIZO

| 兼容设备 | 对应功能 |
| --- | --- |
| OPPO Enco X4、OPPO Enco Bohr、OnePlus Buds Pro 4、OnePlus Buds Bohr | 电量、充电状态、降噪、通透、自适应降噪、降噪档位、预设 EQ、自定义 EQ、空间音频、头部跟踪、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、提示音量、动态低音、固件信息 |
| OPPO Enco M31、OPPO Enco W31、realme Buds Wireless 2S、realme Buds Wireless 3、realme Buds Wireless 3 Neo、realme Buds Wireless 5 ANC、realme Buds Wireless 6 ANC、realme Buds Air 3、realme Buds Q2s、realme Buds T100、realme Buds Air 3 Neo、realme Buds T110、realme Buds T01、realme Buds T200 Lite、realme Buds T200x、realme Buds Air 2 Neo、realme Buds Air、realme Cobble Speaker、realme Buds Q2、realme Buds Air Pro、realme Brick Speaker、realme Buds Air 2、realme Buds Wireless 2、realme Buds Wireless 2 Neo、DIZO Wireless、DIZO GoPods D、DIZO GoPods、realme Pocket Speaker、realme Buds Wireless Pro、realme Buds Air Neo | 电量、充电状态、固件信息 |
| OPPO Enco M33 | 电量、充电状态、降噪、通透、预设 EQ、手势设置、查找耳机、贴合度检测、动态低音、固件信息 |
| OnePlus BulletsWireless Z2 ANC | 电量、充电状态、降噪、通透、预设 EQ、自定义 EQ、手势设置、查找耳机、贴合度检测、动态低音、固件信息 |
| OnePlus Bullets Wireless Z3 | 电量、充电状态、预设 EQ、空间音频、手势设置、动态低音、固件信息 |
| OPPO Enco Free、OnePlus Buds、OnePlus Buds Z、OPPO Enco Air、OPPO Enco Play | 电量、充电状态、手势设置、查找耳机、固件信息 |
| OPPO Enco W51 | 电量、充电状态、佩戴检测开关、手势设置、查找耳机、固件信息 |
| OnePlus Buds Pro | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、自定义 EQ、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、固件信息 |
| OnePlus Buds Z2 | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、自定义 EQ、佩戴检测开关、手势设置、查找耳机、贴合度检测、固件信息 |
| OPPO Enco X | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、佩戴检测开关、手势设置、查找耳机、贴合度检测、固件信息 |
| OnePlus Buds N、OnePlus Nord Buds 2r、OnePlus Buds V | 电量、充电状态、预设 EQ、自定义 EQ、游戏模式、手势设置、查找耳机、固件信息 |
| OnePlus Nord Buds CE、OPPO Enco R、OPPO Enco Air2、OPPO Enco Air2i、OPPO Enco Buds2、OPPO Enco Air 3i | 电量、充电状态、预设 EQ、游戏模式、手势设置、固件信息 |
| OPPO Enco Free2、OPPO Enco Free2i | 电量、充电状态、降噪、通透、预设 EQ、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、固件信息 |
| OnePlus Buds Pro 2、OnePlus Buds Pro 2（轻享版）、OnePlus Buds Pro 2R | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、自定义 EQ、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、动态低音、固件信息 |
| OPPO Enco Buds、OPPO Enco Air Lite | 电量、充电状态、游戏模式、手势设置、固件信息 |
| OnePlus Nord Buds 2、OnePlus Buds Ace | 电量、充电状态、降噪、通透、预设 EQ、自定义 EQ、游戏模式、手势设置、查找耳机、动态低音、固件信息 |
| OPPO Enco Air2 Pro、OPPO Enco R Pro | 电量、充电状态、降噪、通透、预设 EQ、游戏模式、佩戴检测开关、手势设置、查找耳机、固件信息 |
| OPPO Enco X2 | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、固件信息 |
| OnePlus Buds 3、OnePlus Nord Buds 3 Pro | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、自定义 EQ、空间音频、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、动态低音、固件信息 |
| OnePlus Buds Pro 3 | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、自定义 EQ、空间音频、头部跟踪、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、动态低音、固件信息 |
| OPPO Enco Air3、OPPO Enco R2、OPPO Enco Air3s | 电量、充电状态、预设 EQ、空间音频、游戏模式、手势设置、双设备连接、查找耳机、固件信息 |
| OnePlus Buds Ace 2、OnePlus Nord Buds 3 | 电量、充电状态、降噪、通透、预设 EQ、自定义 EQ、空间音频、游戏模式、手势设置、双设备连接、查找耳机、动态低音、固件信息 |
| OnePlus Buds 4 | 电量、充电状态、降噪、通透、自适应降噪、降噪档位、预设 EQ、自定义 EQ、空间音频、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、提示音量、动态低音、固件信息 |
| OnePlus Nord Buds 3r、OnePlus Buds 3V、OnePlus Nord Curie DO、OPPO Enco Air4i、OPPO Enco Buds3 Pro、OPPO Enco R4、OPPO Enco Buds3s | 电量、充电状态、预设 EQ、自定义 EQ、空间音频、游戏模式、手势设置、双设备连接、查找耳机、提示音量、固件信息 |
| OPPO Enco Air3 Pro、OPPO Enco Free3、OPPO Enco Air4 Pro、OPPO Enco R3 Pro | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、空间音频、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、固件信息 |
| OnePlus Open Buds、OPPO Enco Clip、OPPO Enco Clip2、OPPO Enco Nobel、OnePlus Buds Clip、OnePlus Open Nobel | 电量、充电状态、预设 EQ、空间音频、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、提示音量、固件信息 |
| OnePlus Buds Ace 3、OnePlus Nord Buds Einstein 1、OnePlus Nord Buds 4 Pro、OnePlus Nord Buds Einstein 2、OnePlus Nord Buds 4、OnePlus Nord Buds ohm EX、OPPO Enco Air5 Pro、OPPO Enco Einstein 1、OPPO Enco Einstein 3 | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、自定义 EQ、空间音频、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、提示音量、动态低音、固件信息 |
| OPPO Enco X3i | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、空间音频、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、动态低音、固件信息 |
| OPPO Enco Air2（新声版）、OPPO Enco Air3i、OPPO Enco R3 | 电量、充电状态、预设 EQ、空间音频、游戏模式、手势设置、固件信息 |
| OPPO Enco X3 | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、自定义 EQ、空间音频、头部跟踪、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、固件信息 |
| OnePlus Flow Buds、OnePlus Nord Buds Laplace 4、OPPO Enco Air5s、OPPO Enco Laplace 1、OPPO Enco Laplace 2、OPPO Enco Air5s（星光版）、OPPO Enco Laplace 3 | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、自定义 EQ、空间音频、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、提示音量、动态低音、固件信息 |
| OPPO Enco Buds2 Pro | 电量、充电状态、预设 EQ、游戏模式、手势设置、查找耳机、固件信息 |
| OPPO Enco Free4、OPPO Enco X3s、OPPO Enco Free4（丹拿版） | 电量、充电状态、降噪、通透、自适应降噪、降噪档位、预设 EQ、自定义 EQ、空间音频、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、提示音量、固件信息 |
| OPPO Enco Air4、OPPO Enco Buds3 Pro+、OPPO Enco Air4s | 电量、充电状态、降噪、通透、预设 EQ、空间音频、游戏模式、手势设置、双设备连接、查找耳机、固件信息 |
| OPPO Enco Buds3、OPPO Enco Air4（新声版）、OPPO Enco R5、OPPO Enco xiapu OF | 电量、充电状态、预设 EQ、空间音频、游戏模式、手势设置、双设备连接、查找耳机、提示音量、固件信息 |
| OPPO Enco Air5、OPPO Enco ohm DO、OPPO Enco ohm EX、OPPO Enco ohm | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、自定义 EQ、空间音频、游戏模式、佩戴检测开关、手势设置、双设备连接、查找耳机、贴合度检测、提示音量、固件信息 |
| realme Buds Wireless 6 Neo、realme Buds Wireless 6、realme Buds Air 3S、realme Buds T300、realme Buds N1 Pro、realme Buds T310、realme Buds N1、realme Buds T200、realme Buds Clip、realme TechLife Buds、realme Buds T500 | 电量、充电状态、查找耳机、固件信息 |
| OnePlus Buds N | 电量、充电状态、预设 EQ、自定义 EQ、手势设置、查找耳机、固件信息 |
| realme Buds Air 5 Pro、realme Buds Air 5、realme Buds Air6 Pro、realme Buds Air6、realme Buds Air7、realme Buds Air7 Pro、realme Buds T500 Pro、realme Buds Air8、realme Buds Air8 Pro、realme Buds T500 Pro Harry Potter Edition | 电量、充电状态、佩戴检测开关、查找耳机、固件信息 |

## vivo / iQOO

| 兼容设备 | 对应功能 |
| --- | --- |
| vivo TWS 1 | 电量、充电状态、佩戴状态（只读）、固件信息 |
| vivo TWS Neo、vivo TWS 2e、vivo TWS Air、vivo TWS A1、iQOO TWS Air、vivo TWS Air2、vivo TWS A2、iQOO TWS Air2、vivo TWS 3i、iQOO TWS 1i、vivo TWS A3、vivo TWS 3i (Long Battery) | 电量、充电状态、预设 EQ、佩戴状态（只读）、查找耳机、固件信息 |
| vivo TWS 2、vivo TWS 3、vivo TWS 3 Pro、vivo TWS Air Pro、vivo TWS A1 Pro、iQOO TWS Air Pro、vivo TWS X1、iQOO TWS 1、vivo TWS 3e、iQOO TWS 1e | 电量、充电状态、降噪、通透、预设 EQ、佩戴状态（只读）、查找耳机、固件信息 |
| vivo TWS 4、iQOO TWS 2、vivo TWS 4 Hi-Fi、vivo TWS Air3 Pro、iQOO TWS Air3 Pro、vivo TWS Air3 Pro (Long Battery)、vivo TWS 5、vivo TWS 5 Hi-Fi、iQOO TWS 5、iQOO TWS 5e、vivo TWS 5e、vivo TWS 5 Pro | 电量、充电状态、降噪、通透、预设 EQ、游戏模式、佩戴状态（只读）、查找耳机、固件信息 |
| vivo TWS Air3、iQOO TWS Air3、vivo TWS A4、vivo TWS 5i、vivo TWS A5、iQOO TWS 5i | 电量、充电状态、预设 EQ、游戏模式、佩戴状态（只读）、查找耳机、固件信息 |
| vivo Headphones、iQOO Headphones | 电量、充电状态、降噪、通透、预设 EQ、游戏模式、查找耳机、固件信息 |

## 华为

| 兼容设备 | 对应功能 |
| --- | --- |
| HUAWEI FreeBuds 3 | 型号识别 / 素材；独立空间音频需设备确认；暂不开放降噪控制 |
| HUAWEI FreeBuds 3i | 电量、充电状态、降噪、通透、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeBuds 4E | 电量、充电状态、降噪、降噪档位、佩戴检测开关、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeBuds 4i | 电量、充电状态、降噪、通透、佩戴检测开关、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeBuds 5 | 电量、充电状态、降噪、降噪档位、游戏模式、佩戴检测开关、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeBuds 5i、HUAWEI FreeBuds 6i、HUAWEI FreeBuds Pro 3、HUAWEI FreeBuds Pro 5 | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、游戏模式、佩戴检测开关、双设备连接、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeBuds 7i、HUAWEI FreeBuds Studio | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、佩戴检测开关、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeBuds Pro | 电量、充电状态、降噪、通透、降噪档位、佩戴检测开关、双设备连接、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeBuds Pro 2 | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、佩戴检测开关、双设备连接、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeBuds Pro 4、HUAWEI FreeClip 2 | 电量、充电状态、预设 EQ、游戏模式、佩戴检测开关、双设备连接、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeBuds SE、HUAWEI FreeClip、HUAWEI Eyewear | 电量、充电状态、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeBuds SE 2 | 电量、充电状态、预设 EQ、游戏模式、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeBuds SE 4 | 电量、充电状态、降噪、通透、降噪档位、预设 EQ、游戏模式、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeArc | 电量、充电状态、预设 EQ、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeLace Pro | 电量、充电状态、降噪、通透、降噪档位、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI FreeLace Pro 2 | 电量、充电状态、降噪、通透、降噪档位、游戏模式、双设备连接、独立空间音频 / 头部跟踪（需设备确认） |
| HUAWEI Eyewear 2 | 电量、充电状态、游戏模式、独立空间音频 / 头部跟踪（需设备确认） |

## 索尼

| 兼容设备 | 对应功能 |
| --- | --- |
| INZONE H9 II、LinkBuds、LinkBuds Clip、LinkBuds Fit、LinkBuds Open、LinkBuds S、MDR-XB950B1、MDR-XB950N1、ULT WEAR、WF-1000X、WF-1000XM3、WF-1000XM4、WF-1000XM5、WF-1000XM6、WF-C500、WF-C510、WF-C700N、WF-C710N、WF-H800、WF-SP700N、WF-SP800N、WF-SP900、WH-1000XM2、WH-1000XM3、WH-1000XM4、WH-1000XM4C、WH-1000XM5、WH-1000XM6、WH-CH520、WH-CH530 series、WH-CH700N、WH-CH720N、WH-CH730N series、WH-H800、WH-H810、WH-H900N、WH-H910N、WH-XB700、WH-XB900N、WH-XB910N、WI-1000X、WI-1000XM2、WI-C100、WI-C600N、WI-H700、WI-SP600N | 电量 / 充电状态（按设备上报）；降噪 / 环境声、预设 EQ（需设备确认） |

## AKG

| 兼容设备 | 对应功能 |
| --- | --- |
| AKG N400NC、AKG N700NCM2、AKG N200NC、AKG Y600 | 电量、降噪 / 环境声、入耳自动播放 / 暂停（需设备确认） |
| AKG N700NC、AKG N5 Hybrid、AKG N9 Hybrid | 仅型号识别和素材，暂不提供耳机控制 |

## 小米 / Redmi

| 兼容设备 | 对应功能 |
| --- | --- |
| REDMI Buds 6 Pro 电竞版、REDMI Buds 7S、REDMI Buds 8、REDMI Buds 8 Active、REDMI Buds 8 Lite、REDMI Buds 8 Pro、REDMI Buds 8 活力版、REDMI Buds 8 青春版、REDMI Buds 8S、REDMI Headphones Neo、REDMI 头戴降噪耳机、REDMI Buds 6 Pro、REDMI Buds SE、Redmi Buds 4、Redmi Buds 4 Active、Redmi Buds 4 Pro、Redmi Buds 4 哈利·波特版、Redmi Buds 4活力版、Redmi Buds 5、Redmi Buds 5 AAPE、Redmi Buds 5 Pro、Redmi Buds 5 Pro Gaming、Redmi Buds 6 Active、Redmi Buds 6 Lite、Redmi Buds 6 Play、Redmi Buds 6 活力版、Redmi Buds 6 青春版、Redmi Buds 6S、Redmi Buds 6、Xiaomi Air 5、Xiaomi Buds 3、Xiaomi Buds 3 Pro、Xiaomi Buds 3 Star Wars、Xiaomi Buds 3T Pro、Xiaomi Buds 4、Xiaomi Buds 4 Pro、Xiaomi Buds 5、Xiaomi Buds 6、Xiaomi OpenWear Stereo、Xiaomi OpenWear Stereo Pro、Xiaomi 开放式耳机、Xiaomi 开放式耳机 Pro、Xiaomi 真无线降噪耳机 3 迪士尼100周年限定版、Xiaomi 耳夹式耳机、Xiaomi 骨传导耳机2、Xiaomi Buds 5 Pro、Xiaomi Buds 5 Pro Wi-Fi、小米 Air4 SE | 电量；降噪 / 通透及档位、预设 EQ、游戏低延迟、双设备开关、空间音频 / 头部跟踪、提示音量（按官方型号配置与设备回读开放） |

## 漫步者

| 兼容设备 | 对应功能 |
| --- | --- |
| EDIFIER W820NB、EDIFIER W200BT | 电量、降噪、通透 |

## 未列出的型号

| 系列 | 对应功能 |
| --- | --- |
| OPPO / 一加 / realme / DIZO | 尝试通用协议；按返回的产品 ID 识别，未知产品仅提供基础状态 |
| vivo / iQOO、华为、漫步者 | 尝试各品牌通用协议，基础状态以设备实际应答为准 |
| Xiaomi / Redmi | 按系列名称尝试鉴权、电量及已实现配置；目录未收录的型号不开放猜测的 EQ 预设或降噪档位 |
| Sony / AKG | 无通用型号兜底；仅匹配上表内置名称 |

通用识别不保证兼容。设备素材可用不代表控制可用；小米、华为、索尼、AKG 暂未达到 OPPO 的全部功能覆盖。
