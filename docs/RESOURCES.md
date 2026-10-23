# 设备资源缓存（开发说明）

识别到耳机上报的产品 ID 和颜色后，应用后台通过 [EncoAPI](https://encoapi.vercel.app) 获取对应图片、连接动画和可用的旋转模型，写入本地缓存。下载在后台线程进行，不影响蓝牙会话初始化，也不弹出任何提示。

## 3D 模型

`model` 素材包的 `config.json` 二选一：`modelWebp`（预渲染 360° 转盘，动画 WebP，首页左右拖动）或 `model3D`（`.vfxms`，欢律的 Filament 场景）。`.vfxms` 的解析与欢律 `ModelScene.loadSceneFromBuffer` 一致：

- 40 字节大端文件头：config / glb / ibl / skybox / animation 五段的（起始，长度），各段紧随文件头依次存放；
- config：场景 JSON（`aaType`、`enableBloom`/`enableSSAO`/`enableShadow`、`backgroundColor`、`modelScale`、`light.intensity/direction/iblIntensity/iblAngle`，以及按材质序号覆盖的 Filament 材质参数，只应用 FLOAT/FLOAT4 类型）；缺省值与欢律相同；
- glb：glTF 2.0 二进制模型（金属度-粗糙度 PBR，贴图为 PNG）；
- ibl：cmgen 生成的 KTX 1.1 立方体贴图（R11G11B10F，含 mip 预滤波与 `sh` 球谐系数）；skybox 不使用（欢律详情页为透明背景）。

渲染见 `src/EncoDeck.Platform/Media/Scene3D`：模型按包围盒缩放到 `modelScale`、中心放在 (0,0,-4)，相机从 (0,0,1) 环绕（竖直视角 45°，曝光 f/16、1/125 s、ISO 100），交互参数与欢律 `CameraController` 相同。`EncoDeck.Probe render-model --file x.vfxms --out <目录>` 可离屏渲染为 PNG 用于排查。

## 设置页“设备素材”

设置 › 设备素材 显示当前耳机（名称、产品 ID、颜色）对应的下载状态：

- 状态：正在检查、正在下载素材包 i/n（服务器提供 Content-Length 时显示已下载/总大小并显示确定进度条，否则为不确定进度条）、正在解压、已更新、已是最新（24 小时内的缓存），以及失败原因（无法连接、服务器不可用、暂无该型号/颜色的素材、完整性校验失败、无法写入缓存）。失败时已有缓存继续使用。
- 上次更新时间（`manifest.json` 的 `updatedAt`）、已解析的资源文件数与缓存目录占用的大致空间。
- “重新拉取”：忽略 24 小时缓存期，以 `fresh=1` 重新获取元数据（相同哈希的原包仍复用本地已校验的副本）。没有识别到产品 ID 与颜色或正在下载时不可用。
- “打开缓存文件夹”：打开该产品/颜色的缓存目录。

进度由 `EncoApiResourceClient.EnsureAsync` 的 `IProgress<ResourceDownloadProgress>` 报告，`DeviceResourceSync` 汇总为 `Status` 快照与 `StatusChanged` 事件（字节进度至多每 100 ms 发布一次），设置页在 UI 线程合并刷新。

缓存目录为 `%LOCALAPPDATA%\EncoDeck\Resources\downloads\<产品ID>\<颜色>\`，其中 `manifest.json` 引用经过 SHA-256 校验的资源。优先从 EncoAPI 返回的 Vercel 哈希缓存地址下载。后续连接复用文件，元数据至多每 24 小时重新检查一次；资源哈希未变化时不重复下载原包。更新失败保留已有缓存，无缓存时显示通用插图。

请求只包含产品 ID 和颜色，不发送设备名称、蓝牙地址、序列号或账户信息。Vercel 下载不可用时，后台自动尝试元数据中已验证域名的供应商地址，仍校验相同 SHA-256。动画使用原配置中的时长、深浅色版本；缺少动画的型号使用自身产品图的本地通用动效：首页轻微悬浮，连接弹窗缩放入场；首页明确标为非原厂开盖。无型号图则使用通用插画，不借用其他型号。系统关闭动画时保持静止。旧版用户已有的本地文件继续兼容。

构建与安装不会收集本机缓存。MOONDROP 的 17 款产品图片及来源记录已迁移至 EncoAPI，客户端按精确型号下载并缓存，不再随安装包附带。颜色 0 为目录照片，未识别型号继续使用通用插图。图片权利仍属于原权利人，不适用本项目代码 GPL。

EncoAPI 是独立社区服务，与 OPPO 无隶属关系；图片及动画的权利属于原权利人。

## 多品牌动态展示兜底

首页优先展示官方 3D / 转盘模型，其次官方产品动画，最后为 `DeviceMedia` 对静态产品图的原生 Composition 动效。连接弹窗独立选择官方连接视频、逐帧 WebP 或 Lottie；Lottie 在弹窗内只播放一次。未取得动态资源时使用产品图入场动效，有整机图时不再单独展示左耳、右耳和充电仓。控件在隐藏/卸载时停止动画，重新显示时恢复。

EncoAPI 的 `animationSupport` 区分 `official` 与 `client`，通用动效不算新增的官方素材角色。客户端兜底无需等待服务端上线，离线和旧缓存也可用。vivo TWS 5 Pro 当前属于通用动效，不宣称已取得原厂开盖序列。

华为首页视频分别解析 `homeAnimationRes` 与 `homeAnimationDarkRes`，控件根据 ActualTheme 切换，主题变更时重新选择媒体。深色资源缺失则回退产品图动效，避免播放浅色白底视频。原厂首页视频的背景是非透明底色，与使用独立透明蒙版的弹窗视频不同。
