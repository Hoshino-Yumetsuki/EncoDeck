# Generates Strings/zh-CN/Resources.resw and Strings/en-US/Resources.resw for EncoDeck.App.
# Usage: python -I make_resw.py <app-project-dir>
import sys
from pathlib import Path
from xml.sax.saxutils import escape

A = "[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"
T = "[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip"

S = [
    # key, zh-CN, en-US
    ("Battery_Left", "左耳", "Left"),
    ("Battery_Right", "右耳", "Right"),
    ("Battery_Case", "充电盒", "Case"),
    ("Battery_Unknown", "未知", "Unknown"),
    ("Battery_LastRead", "上次读取 {0}", "Last read {0}"),
    ("Battery_Charging", "正在充电", "charging"),
    ("Device_None", "未发现耳机", "No earbuds"),
    ("Connection_NotDetected", "未发现支持的耳机", "No supported earbuds"),
    ("Connection_PairedNotConnected", "已配对，未连接", "Paired, not connected"),
    ("Connection_AudioConnected", "已连接，正在准备控制", "Connected, preparing controls"),
    ("Connection_ControlConnecting", "正在连接控制通道…", "Connecting controls…"),
    ("Connection_ControlReady", "已连接", "Connected"),
    ("Connection_ControlUnavailable", "控制通道不可用", "Controls unavailable"),
    ("Connection_Reconnecting", "正在重新连接…", "Reconnecting…"),
    ("Connection_Disconnected", "已断开", "Disconnected"),
    ("Error_Timeout", "耳机没有响应，设置未改变。", "The earbuds did not respond. Nothing was changed."),
    ("Error_Transport", "与耳机的控制连接出错，请稍后重试。", "The control connection failed. Please try again."),
    ("Error_Rejected", "耳机拒绝了这项设置。", "The earbuds rejected this setting."),
    ("Error_Unsupported", "此型号尚未验证支持该功能。", "This feature is not verified for this model."),
    ("Error_NotConnected", "耳机控制通道未连接。", "The earbuds' control channel is not connected."),
    ("Status_UpdatedAt", "更新于 {0}", "Updated {0}"),
    ("Noise_Unknown", "降噪状态未知", "Noise control unknown"),
    ("NoiseMode_Off", "关闭", "Off"),
    ("NoiseMode_NoiseCancellation", "降噪", "Noise cancelling"),
    ("NoiseMode_Transparency", "通透", "Transparency"),
    ("NoiseMode_Adaptive", "自适应", "Adaptive"),
    ("NoiseLevel_Smart", "智能切换", "Auto"),
    ("NoiseLevel_Deep", "深度", "High"),
    ("NoiseLevel_Medium", "中度", "Moderate"),
    ("NoiseLevel_Light", "轻度", "Low"),
    ("NoiseLevel_AdaptiveLess", "更少噪音", "Less noise"),
    ("NoiseLevel_AdaptiveStandard", "标准", "Standard"),
    ("NoiseLevel_AdaptiveMore", "更多噪音", "More noise"),
    ("NoiseSelector_Level", "降噪强度", "Noise cancelling level"),
    ("NoiseSelector_AdaptiveLevel", "自适应偏好", "Adaptive preference"),
    ("Eq_DynaudioOriginal", "丹拿原声", "Dynaudio original sound"),
    ("Eq_DynaudioHiRes", "丹拿高解析", "Dynaudio high-resolution"),
    ("Eq_PureVocals", "纯享人声", "Pure vocals"),
    ("Eq_CrispHighs", "清亮高音", "Crisp highs"),
    ("Eq_ThunderingBass", "澎湃低音", "Thundering bass"),
    ("Eq_UnknownPreset", "音效 {0}", "Preset {0}"),
    ("Toggle_InEarDetection", "佩戴检测", "In-ear detection"),
    ("Toggle_InEarDetection_Description", "摘下耳机时自动暂停播放", "Pause playback when an earbud is removed"),
    ("Toggle_GamingMode", "游戏模式", "Game mode"),
    ("Toggle_GamingMode_Description", "降低音频延迟", "Lower audio latency"),
    ("Toggle_DualDeviceConnection", "双设备连接", "Dual-device connection"),
    ("Toggle_DualDeviceConnection_Description", "同时连接两台设备", "Stay connected to two devices"),
    ("Toggle_SpatialAudio", "空间音频", "Spatial audio"),
    ("Toggle_SpatialAudio_Description", "", ""),
    ("Toggle_HeadTracking", "头部追踪", "Head tracking"),
    ("Toggle_HeadTracking_Description", "", ""),
    ("Common_ExperimentalShort", "实验", "experimental"),
    ("Cap_BatteryLevel", "电量", "Battery level"),
    ("Cap_ChargingStatus", "充电状态", "Charging status"),
    ("Cap_NoiseCancellation", "降噪模式", "Noise control"),
    ("Cap_AdaptiveNoiseCancellation", "智能 / 自适应降噪", "Smart / adaptive ANC"),
    ("Cap_NoiseCancellationLevel", "降噪强度", "Noise cancelling level"),
    ("Cap_Equalizer", "耳机端 EQ 预设", "Device EQ presets"),
    ("Cap_CustomEqualizer", "自定义 EQ", "Custom EQ"),
    ("Cap_SpatialAudio", "空间音频", "Spatial audio"),
    ("Cap_HeadTracking", "头部追踪", "Head tracking"),
    ("Cap_GamingMode", "游戏模式", "Game mode"),
    ("Cap_InEarDetection", "佩戴检测", "In-ear detection"),
    ("Cap_TouchCustomization", "触控自定义", "Touch controls"),
    ("Cap_DualDeviceConnection", "双设备连接", "Dual-device connection"),
    ("Cap_FindMyEarbuds", "查找耳机", "Find my earbuds"),
    ("Cap_FirmwareInformation", "固件信息", "Firmware information"),
    ("Support_Supported", "已验证", "Verified"),
    ("Support_Experimental", "实验性", "Experimental"),
    ("Support_Unknown", "未知", "Unknown"),
    ("Support_Unsupported", "不支持", "Not supported"),
    ("Devices_StatusConnected", "已连接", "Connected"),
    ("Devices_StatusNotConnected", "未连接", "Not connected"),
    ("Devices_Active", "当前设备", "Current device"),
    ("Devices_UseThis", "设为当前设备", "Use this device"),
    ("Devices_ExportDone", "已导出：{0}", "Exported: {0}"),
    ("Devices_ExportFailed", "导出失败：{0}", "Export failed: {0}"),
    ("Tray_Open", "打开 EncoDeck", "Open EncoDeck"),
    ("Tray_NoiseControl", "降噪模式", "Noise control"),
    ("Tray_ConnectionPopup", "连接弹窗", "Connection popup"),
    ("Tray_Settings", "设置", "Settings"),
    ("Tray_Exit", "退出", "Exit"),
    ("Popup_Announcement", "{0} 已连接", "{0} connected"),
    ("Popup_ConnectedTitle", "已连接 {0}", "Connected {0}"),
    ("Notify_LowBattery_Title", "{0} 电量低", "{0} battery low"),


    # x:Uid resources
    ("TitleBar_Unofficial.Text", "非官方 · 与 OPPO 无关", "Unofficial · not affiliated with OPPO"),
    ("Nav_Home.Content", "主页", "Home"),
    ("Nav_Sound.Content", "音效", "Sound"),
    ("Nav_Controls.Content", "控制", "Controls"),
    ("Nav_Devices.Content", "设备", "Devices"),
    ("Nav_Settings.Content", "设置", "Settings"),
    ("Home_Title.Text", "主页", "Home"),
    ("Home_GenericModel.Title", "未验证的型号", "Unverified model"),
    ("Home_GenericModel.Message", "EncoDeck 尚未在这款耳机上验证，只显示只读信息，不会发送设置命令。", "EncoDeck has not been verified with these earbuds. Only read-only information is shown and no settings are sent."),
    ("Home_NoDevice_Title.Text", "没有找到支持的耳机", "No supported earbuds found"),
    ("Home_NoDevice_Body.Text", "请先在 Windows 蓝牙设置中配对 OPPO 耳机。配对后 EncoDeck 会自动发现它。", "Pair your OPPO earbuds in Windows Bluetooth settings first. EncoDeck finds paired earbuds automatically."),
    ("Home_NoDevice_OpenSettings.Content", "打开蓝牙设置", "Open Bluetooth settings"),
    ("Home_Refresh.Content", "刷新", "Refresh"),
    ("Home_Reconnect.Content", "重新连接", "Reconnect"),
    ("Home_Unavailable_Title.Text", "耳机控制暂不可用", "Earbud controls are unavailable"),
    ("Home_Unavailable_Body.Text", "音频可能仍然正常，但 EncoDeck 无法打开耳机的控制通道。", "Audio may still work, but EncoDeck could not open the earbuds' control channel."),
    ("Home_Reconnect2.Content", "重试", "Try again"),
    ("Home_Diagnostics.Content", "诊断", "Diagnostics"),
    ("Home_Battery.Text", "电量", "Battery"),
    ("Home_NoiseControl.Text", "降噪", "Noise control"),
    ("Home_Sound.Text", "音效（耳机端）", "Sound (device EQ)"),
    ("Home_SoundMore.Content", "音效设置", "Sound settings"),
    ("Home_DeviceInfo.Text", "设备", "Device"),
    ("NoiseSelector_Off.Text", "关闭", "Off"),
    ("NoiseSelector_Anc.Text", "降噪", "Noise cancelling"),
    ("NoiseSelector_Adaptive.Text", "自适应", "Adaptive"),
    ("NoiseSelector_Transparency.Text", "通透", "Transparency"),
    ("Sound_Title.Text", "音效", "Sound"),
    ("Sound_DeviceEq.Text", "耳机端 EQ（在耳机内部生效）", "Earbud EQ (applied inside the earbuds)"),
    ("Sound_DeviceEq_Note.Text", "这些预设由耳机执行。EncoDeck 不会修改 Windows 的音频处理，也不提供 Windows 端均衡器。", "These presets run on the earbuds. EncoDeck never changes Windows audio processing and does not provide a Windows-side equalizer."),
    ("Sound_DeviceEq_Unavailable.Text", "此型号的音效切换尚未完成实机验证。可在“设置”中开启实验性功能后尝试。", "Switching EQ presets has not been verified for this model yet. You can enable experimental features in Settings to try it."),
    ("Sound_Spatial.Text", "空间音频", "Spatial audio"),
    ("Sound_Spatial_Unknown.Text", "空间音频和头部追踪尚未在此型号上验证，因此暂不提供。", "Spatial audio and head tracking have not been verified on this model, so they are not offered yet."),
    ("Controls_Title.Text", "控制", "Controls"),
    ("Controls_Settings.Text", "耳机设置", "Earbud settings"),
    ("Controls_NoToggles.Text", "此型号暂无已验证的耳机设置。可在“设置”中开启实验性功能。", "No verified earbud settings for this model yet. Experimental settings can be enabled in Settings."),
    ("Controls_Capabilities.Text", "功能支持情况", "Feature support"),
    ("Controls_Capabilities_Note.Text", "只有经过实机验证的功能才会启用；实验性功能需在设置中开启；未知功能不会提供。", "Only features verified on real hardware are enabled. Experimental ones must be enabled in Settings; unknown ones are never offered."),
    ("Devices_Title.Text", "设备", "Devices"),
    ("Devices_Paired.Text", "已配对的耳机", "Paired earbuds"),
    ("Devices_None.Text", "没有 EncoDeck 支持的已配对耳机。请在 Windows 蓝牙设置中配对。", "No paired earbuds that EncoDeck supports. Pair them in Windows Bluetooth settings."),
    ("Devices_OpenBluetooth.Content", "打开蓝牙设置", "Open Bluetooth settings"),
    ("Devices_Diagnostics.Text", "诊断", "Diagnostics"),
    ("Devices_Diagnostics_Note.Text", "诊断包包含日志和设置，蓝牙地址已脱敏。更深入的分析请使用 EncoDeck.Probe（见 docs/TESTING.md）。", "Diagnostic exports contain logs and settings with Bluetooth addresses redacted. For deeper analysis, run EncoDeck.Probe (see docs/TESTING.md)."),
    ("Devices_Reconnect.Content", "重新连接控制通道", "Reconnect control channel"),
    ("Devices_Export.Content", "导出诊断包", "Export diagnostics"),
    ("Devices_OpenLogs.Content", "打开日志文件夹", "Open log folder"),
    ("Settings_Title.Text", "设置", "Settings"),
    ("Settings_General.Text", "常规", "General"),
    ("Settings_StartWithWindows.Header", "开机时启动", "Start with Windows"),
    ("Settings_KeepInTray.Header", "关闭窗口后继续在通知区域运行", "Keep running in the notification area when the window is closed"),
    ("Settings_Theme.Header", "主题", "Theme"),
    ("Settings_Theme_System.Content", "跟随系统", "Use system setting"),
    ("Settings_Theme_Light.Content", "浅色", "Light"),
    ("Settings_Theme_Dark.Content", "深色", "Dark"),
    ("Settings_Language.Header", "语言", "Language"),
    ("Settings_Language_System.Content", "跟随系统", "Use system language"),
    ("Settings_Language_Restart.Text", "重新启动 EncoDeck 后生效。", "Takes effect after restarting EncoDeck."),
    ("Settings_Popup.Text", "连接弹窗", "Connection popup"),
    ("Settings_ShowPopup.Header", "耳机连接时显示弹窗", "Show a popup when earbuds connect"),
    ("Settings_PopupDuration.Header", "显示时长（秒）", "Display time (seconds)"),
    ("Settings_SuppressFullscreen.Header", "全屏应用或游戏在前台时不显示", "Don't show while a fullscreen app or game is in front"),
    ("Settings_Notifications.Text", "通知", "Notifications"),
    ("Settings_LowBattery.Header", "耳机电量低时通知", "Notify when an earbud's battery is low"),
    ("Settings_LowBatteryThreshold.Header", "低电量阈值（%）", "Low battery level (%)"),
    ("Settings_Advanced.Text", "高级", "Advanced"),
    ("Settings_Experimental.Header", "启用实验性功能", "Enable experimental features"),
    ("Settings_Experimental_Note.Text", "实验性功能有协议依据，但尚未在你的型号上验证。它们会向耳机发送真实命令，欢迎反馈结果。", "Experimental features have protocol evidence but have not been verified on your model. They send real commands to the earbuds. Please report results."),
    ("Settings_RawLogging.Header", "记录控制通道数据（诊断）", "Log control-channel traffic (diagnostics)"),
    ("Settings_RawLogging_Note.Text", "把协议帧写入日志。包含其他设备名称或地址的帧会被遮盖。", "Writes protocol frames to the log. Frames containing other devices' names or addresses are masked."),
    ("Settings_About.Text", "关于", "About"),
    ("Settings_Disclaimer.Text", "EncoDeck 是非官方的社区项目，与 OPPO、OnePlus、realme 没有任何官方关系，也未获得其认可或支持。产品名称仅用于说明兼容性。", "EncoDeck is an unofficial, community project. It is not affiliated with, endorsed by or supported by OPPO, OnePlus or realme. Product names are used only to describe compatibility."),
    ("Settings_License.Text", "本软件以 GNU 通用公共许可证第 3 版（仅限，GPL-3.0-only）发布。第三方声明随应用附带。", "Licensed under the GNU General Public License v3.0 only (GPL-3.0-only). Third-party notices are included with the app."),
    ("Settings_Source.Content", "源代码", "Source code"),
    ("Settings_Notices.Content", "第三方声明", "Third-party notices"),
    ("Panel_OpenApp." + A, "打开 EncoDeck", "Open EncoDeck"),
    ("Panel_OpenApp." + T, "打开 EncoDeck", "Open EncoDeck"),
    ("Panel_Noise.Text", "降噪", "Noise control"),
    ("Panel_NoDevice.Text", "没有已配对的支持耳机。", "No supported earbuds paired."),
    ("Panel_MoreSettings.Content", "更多设置", "More settings"),
    ("Panel_Reconnect.Content", "重新连接", "Reconnect"),
    ("Popup_Close." + A, "关闭", "Close"),
]

HEADER = """<?xml version="1.0" encoding="utf-8"?>
<!-- Generated from one table so zh-CN and en-US stay in sync. SPDX-License-Identifier: GPL-3.0-only -->
<root>
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" msdata:Ordinal="1" />
              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
"""

root = Path(sys.argv[1])
keys = [k for k, _, _ in S]
dupes = {k for k in keys if keys.count(k) > 1}
assert not dupes, dupes
for culture, idx in (("zh-CN", 1), ("en-US", 2)):
    out = root / "Strings" / culture / "Resources.resw"
    out.parent.mkdir(parents=True, exist_ok=True)
    body = "".join(f'  <data name="{escape(k, {chr(34): "&quot;"})}" xml:space="preserve"><value>{escape(row[idx])}</value></data>\n' for row in S for k in [row[0]])
    out.write_text(HEADER + body + "</root>\n", encoding="utf-8")
    print("wrote", out, len(S))
