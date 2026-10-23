# 迈从、雷蛇 USB 适配与水月雨 APK 动画检查

2026-10-24，独立工作区 `feat/headset-adapters`，不发布 EncoDeck 正式版本。

## 实现范围

| 型号 | 当前实现 | 边界 |
|---|---|---|
| MCHOSE X9 | USB 接收器识别；`3837:6045`、HID `FF90:0001` 精确限定的 64 字节电量查询 `55 65 01`；只接受 `55 65 value 02` 回复 | 不把心跳、其他字段或大于 100 的值当作电量；未实现 EQ、7.1、灯光、升级；本机无真机 |
| Razer BlackShark V2 Pro (2020) | `1532:0528`、`FF00:0001` 的 feature report `FF` 查询；校验完整新回复前缀、电压非零和电量范围 | 排除充电线 `052E`、关机缓存回复；充电中隐藏不可靠百分比。设备电量本身是粗略档位；本机无真机 |
| Razer BlackShark V2 Pro (2023) | `1532:0555`、`FF00:0001` 的 PA 输出/输入报告；远程模式唤醒、`21` 电量请求、ACK/长度/百分比校验；完成、超时或取消均尝试释放远程模式 | 不套用 2020 协议；不推测 `0556/0557`、其他配色/平台 PID；未实现设置写入；本机无真机 |
| 迈从 V9 / V9 Pro / V9 Turbo+ / S9 Pro / G9 Pro / R9 | USB 音频端点识别；仅在声明标准百分比时读取电量 | 没有把 X9 的厂商报告套用到这些型号 |
| 雷蛇 BlackShark V2 HyperSpeed / V3 Pro、Barracuda X / Pro、Kraken V3 Pro / V4 / V4 Pro | USB 音频端点识别；条件性标准 HID 电量 | 没有推测这些型号的厂商协议。没有年款信息的 BlackShark V2 Pro 需要精确 PID 才能确定 2020/2023 |

所有识别先确认同容器 USB 硬件和音频输出端点。已知型号以完整名称匹配，拒绝把 V4 X、X9 Pro、Barracuda X Chroma 等未列入型号认作较短的名称。蓝牙名称不会进入 USB 协议路径。读取失败、超时或离线时清除旧电量；插着接收器不证明耳机无线链路在线。

## 协议证据

协议事实据公开抓包实现重新编写；未分发其 Python/C++ 源码，也未运行厂商安装器：

- [HeadsetControl 的 MCHOSE X9 实现](https://github.com/Sapd/HeadsetControl/blob/master/lib/devices/mchose_x9.hpp)：文档说明与 Windows M HUB 显示值交叉验证。研究输入 SHA-256：`35e58ab691eb359d8e909bc0003aa36dc78b238bf5a009224d7660fbf9da3cd0`。
- [HaloBattery 的 BlackShark 2020 实现与硬件读数](https://github.com/HeyOkay/HaloBattery/blob/main/providers/blackshark2020.py)：SHA-256 `4bfa6490fd616c80eff5faecd0b12eff4fbd2909092e962e25a2a896b8f3fd19`。
- [HaloBattery 的 BlackShark 2023 PA 实现](https://github.com/HeyOkay/HaloBattery/blob/main/providers/blackshark.py)：SHA-256 `9d7cb125d1cf20a1f546202424e7e013ab5eea088d49dd50ad6b12321c4fb551`。
- [迈从官方 M HUB 耳机说明](https://support.mchose.store/hc/en-us/articles/52492201202324-Gaming-Headset-Software-Guide)、[X9 连接说明](https://support.mchose.store/hc/en-us/articles/52491696655124-X9-M-HUB-Guide-Desktop-Driver-Web-Driver-Setup)：耳机使用桌面客户端；Web Driver 不支持音频产品。型号照片来自官方商店，而非第三方评测图。
- [雷蛇 BlackShark V2 Pro 2023 产品页](https://www.razer.com/gaming-headsets/razer-blackshark-v2-pro)、[Kraken V4 产品页](https://www.razer.com/gaming-headsets/razer-kraken-v4)：只采用页面直接引用的精确产品照片；没有图片的型号保留缺失。

## 水月雨：能做连接动画，但没有找到可直接复用的官方产品动画

从[水月雨官网下载页](https://moondroplab.com/cn/moondrop-link)的 APK 链接自行下载并检查：

- 文件：`https://download.moondroplab.com/moondroplink/android-release.apk`。
- 包名 `com.moondroplab.moondrop.moondrop_app`，版本 `2.25.0c-260813ai`；SHA-256 `04756b49acfea523758d86101c96c1824b7b209ae3a8836c07837088e795d2d5`。
- 完整 ZIP CRC 校验通过，共 1,472 个条目。没有 `.mp4/.webm/.webp/.lottie/.riv/.svga` 文件，也未发现耳机开盖或连接序列帧。
- 三张 GIF：`ic_playing_b.gif` / `ic_playing_w.gif`，72×72、48 帧，为音乐播放指示；`gt4-loading.gif`，80×80、77 帧，为验证码加载。不能把它们标成耳机连接动画。
- Flutter `libapp.so` 中找到公开产品目录地址；取回 108 条产品记录，共 729 个非空图片引用（723 PNG、6 JPG），未发现产品动画字段。图标接口也只返回静态分类图标。
- 参数化 App 资源接口返回 HTTP 400，本次未取回该目录。因此该结论只覆盖打包资源和已取回的公开目录；**不能排除 Flutter 运行时生成动画、其他版本或未公开远程素材**。

EncoDeck 的连接弹窗已有产品图淡入/位移动画，水月雨精确型号图片已由 EncoAPI 提供，可以直接采用这条路径。它是客户端制作的产品入场动画，不冒充官方开盖动画。本次没有把加载 GIF 塞进连接素材，也没有新增伪造视频。

EncoAPI 同步了这份 APK 检查的可复现脚本、机器可读检查记录、16 条迈从/雷蛇型号及 8 款型号的 21 组官方配色照片。其余 8 款明确无图；客户端目录从同一 API 索引重新生成。

## 验证

新增测试覆盖精确 VID/PID/usage 门槛、错误型号边界、X9 无关字段、2020 关机缓存/充电值、2023 错命令 ACK/长度/越界电量。模拟数据只验证实现行为，不能代替真实耳机测试。

真机步骤：退出旧版 EncoDeck，运行本地 feat 测试包；直接插接收器并将耳机切到 2.4G，确认型号和电量；依次关机、开机、拔接收器、接充电线，确认旧电量消失、恢复读取且没有重复误识别。此电脑目前没有这些接收器，以上真机步骤待执行。
