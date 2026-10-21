<p align="center"><img src="src/EncoDeck.App/Assets/EncoDeck-256.png" width="96" alt="EncoDeck" /></p>

# EncoDeck

**Windows 原生的 OPPO 耳机控制中心**——在电脑上查看电量、切换降噪、调整音效，不用每次都拿起手机。

> ⚠️ **非官方项目。** EncoDeck 是社区开发的开源软件，与 OPPO、OnePlus、realme 没有任何官方关系，未获得其认可或支持。产品名称仅用于说明兼容性。

[English](README_EN.md) · [兼容性](docs/COMPATIBILITY.md) · [协议记录](docs/PROTOCOL_FINDINGS.md) · [实现状态](docs/IMPLEMENTATION_STATUS.md)

<p align="center">
  <img src="docs/images/popup-demo.gif" width="300" alt="耳机连接时在任务栏旁弹出的开盖动画与电量" />
</p>

| 首页 | 托盘快捷面板（叠在系统快速设置上方） |
|---|---|
| <img src="docs/images/home.png" alt="首页：设备 3D 模型、电量、降噪与设备管理" /> | <img src="docs/images/quick-panel.png" width="320" alt="快捷面板：电量与降噪" /> |

## 功能

- **托盘常驻**：左键弹出快捷面板（电量 + 降噪），右键原生菜单；空闲时几乎不占 CPU。
- **连接弹窗**：耳机连接时在任务栏旁播放该型号的开盖动画并显示电量（Composition 动画，悬停暂停，全屏游戏时自动不弹）。动画只解码画面：不占用音频输出、不出现在系统媒体控件里，耳机点按或媒体键也不会把它暂停。
- **设备 3D 模型**：首页显示与欢律一致的设备模型——预渲染转盘可左右拖动；`.vfxms` 3D 模型（如 Enco X3、Free4）实时渲染，可任意方向拖动旋转、滚轮缩放、双击复位。
- **电量**：左耳 / 右耳 / 充电盒，充电状态，主动上报；盒子不在线时显示“上次读取”。
- **降噪**：降噪（深度 / 中度 / 轻度 / 智能切换）、自适应、通透、关闭；耳机触控或手机切换后自动同步。
- **全局快捷键**：在任何程序里（包括全屏游戏）一键切换降噪 / 通透、轮换模式或直接切到某个模式，在设置 › 快捷键中绑定。
- **真实通信**：所有控制都通过蓝牙 RFCOMM 发给耳机，并等待耳机确认、回读；失败自动回滚，绝不显示伪造状态。
- **Fluent Design**：WinUI 3、Mica / Acrylic、深浅色、系统强调色、简体中文 / English。

## 已实测型号

| 型号 | 固件 | 状态 |
|---|---|---|
| OPPO Enco X4 | 左 116 / 右 116 / 盒 100 | 电量、佩戴状态、降噪全部模式、固件信息已验证；EQ 等见 [兼容性表](docs/COMPATIBILITY.md) |

## 下载与安装

### Microsoft Store（推荐）

<a href="https://apps.microsoft.com/detail/9PN6D8G1K9WZ?mode=direct"><img src="https://get.microsoft.com/images/zh-cn%20dark.svg" width="200" alt="从 Microsoft 获取" /></a>

[在 Microsoft Store 中打开](https://apps.microsoft.com/detail/9PN6D8G1K9WZ) · 支持 x64 / ARM64，由商店自动更新；设置 › 更新 也可手动检查并安装商店更新。开机自启使用系统启动任务，可在任务管理器“启动应用”中管理。

### 安装包

从 [Releases](https://github.com/TIANLI0/EncoDeck/releases) 下载 `EncoDeck-Setup-<版本>-win-x64.exe` 运行即可：

- 默认按用户安装到 `%LOCALAPPDATA%\Programs\EncoDeck`，无需管理员权限；
- **新版本直接覆盖安装即可升级**，设置、缓存和自定义资源保留；
- 卸载时会询问是否删除用户数据（`%LOCALAPPDATA%\EncoDeck`）；
- **应用内更新**：设置 › 更新 可检查 GitHub Releases 上的新版本（默认每天自动检查并在后台下载，可关闭；可选择接收预览版）。安装包下载后会校验大小与 SHA-256，只有点击“立即重启并更新”才会安装，完成后自动重新启动 EncoDeck。

系统要求：Windows 10 19041 及以上（推荐 Windows 11）；安装包为 x64，商店版支持 x64 / ARM64。

## 自己编译

```powershell
git clone https://github.com/TIANLI0/EncoDeck
cd EncoDeck
dotnet test tests/EncoDeck.Tests                      # 单元测试
pwsh build/build-installer.ps1                        # 生成安装包到 artifacts\installer
```

需要 .NET SDK 10.0.100+，不需要 Visual Studio。详见 [开发指南](docs/DEVELOPMENT.md)。

## 诊断工具

`EncoDeck.Probe` 可在不打开界面的情况下检查耳机（列设备、SDP、只读查询、事件监听），默认对蓝牙地址脱敏。用法见 [测试文档](docs/TESTING.md)。

## 参与贡献

欢迎提交兼容性报告、协议发现和代码，请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 致谢

协议研究参考了 [OppoPodsWindows](https://github.com/3295074384/OppoPodsWindows)、[OppoPodsManager](https://github.com/Zhaoyi-ya/OppoPodsManager)、[OppoPods](https://github.com/Leaf-lsgtky/OppoPods)；托盘使用 [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)。完整列表见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 许可证

[GPL-3.0-only](LICENSE)。
