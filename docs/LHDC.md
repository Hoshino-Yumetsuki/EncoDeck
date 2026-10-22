# LHDC V5 可选组件

EncoDeck 使用自己的 WinUI 设置界面：选择配对耳机、采样率、位深、码率、
自适应码率，启用 LHDC 或恢复 Windows 音频。不会打开上游控制面板。
状态由后台工具读取，Hi-Res 开关不等于 Windows 已使用 LHDC。

普通安装包不捆绑 LHDC。用户点击下载后，从 EncoDeck 仓库的专用
`lhdc-runtime` Release 获取 `EncoDeck-LHDC-runtime-win-x64.zip`，
校验 GitHub SHA-256、大小、组件标识和上游提交号后解压到
`%LOCALAPPDATA%/EncoDeck/Components/LHDC`。显示进度，支持取消与重试。
下载本身不安装驱动；安装及恢复仅在点击对应按钮、确认系统管理员授权后执行。
后台组件复用，打开应用不自动联网。旧的上游面板缓存不视为新运行时。

## 源码构建与云端发布

每次构建均从 https://github.com/Wayne-u/LHDC-Win.git 的 main 全新克隆，
按上游固定提交拉取编码器与 Windows 驱动样例。拉取失败立即中止。
自编译四个原生后台工具及传输、音频驱动，运行上游测试、生成测试签名并验证包。
不编译或分发上游 WPF UI，运行时仅包含后端、驱动、部署脚本和完整 PowerShell 7。
`runtime.json` 与 `release.json` 记录源码仓库和提交；`checksums.json` 记录文件摘要。
许可证随组件保留。构建不会安装驱动或修改系统启动配置。

```powershell
# 普通应用安装包，不捆绑 LHDC
pwsh build/build-installer.ps1
# 构建独立组件和 ZIP；每次都重新拉取源码
pwsh build/publish-lhdc-runtime.ps1 -PowerShellDirectory 'C:/Program Files/PowerShell/7'
# 构建后上传至组件 Release（需要 gh 登录对应仓库账户）
pwsh build/publish-lhdc-runtime.ps1 -PowerShellDirectory 'C:/Program Files/PowerShell/7' -Publish
# 可选：离线捆绑安装包
pwsh build/build-installer.ps1 -WithLhdc -LhdcPowerShellDirectory 'C:/Program Files/PowerShell/7'
```

组件 Release 独立于应用更新，不标记为 Latest。发布新版本前审查源码提交和构建结果。
构建依赖以上游为准，目前包括 MSVC x64、SDK/WDK 28000、KMDF 1.33、
CMake、Ninja、.NET 10、PowerShell 7 x64，以及 Visual Studio 驱动组件和 Spectre 库。

## 支持范围

- 商店/MSIX 版本禁用 LHDC，显示原因与非商店版下载入口；不下载、不启动组件。
  当前实现依赖需要管理员安装的独立驱动和后台服务，仅非商店 x64 版本实验性支持。
- 要求 Windows 11 22H2（22621+）、原生 x64 和真实支持 LHDC V5 的耳机。
- 选项是请求参数，启用时由上游实际探测兼容性；不兼容或失败按上游逻辑恢复。
  操作期间会短暂中断音频，可能需要手动重启；未完成时不宣称已启用。
- 仍使用测试签名，普通系统未必满足驱动加载条件。EncoDeck 不自动修改
  测试模式、Secure Boot 或内存完整性。
- 安装与恢复调用上游部署脚本并读取结果文件；权限取消或失败显示错误，
  不强行终止正在部署和恢复的进程。
- 卸载应用前应先恢复 Windows 音频。关闭应用不会停止上游后台服务。
- 音频设备在此处独立选择，可能与 EncoDeck 当前控制的耳机不同；一次只管理一台。

实际播放、重连与恢复仍需真机测试；编译与只读检查不能代替实机音频验证。
