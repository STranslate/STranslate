# 图片翻译链路

## 模块职责
- 管理图片翻译独立窗口/精简窗口的导入、截图、重试、标注图、矢量译文覆盖和静态贴图显示。
- 维护图片翻译专用 OCR 服务与翻译服务绑定，避免普通 OCR 服务误入需要坐标的流程。
- 对 OCR 结果执行结构化投影、分段逻辑、翻译分发、文字覆盖回写和图片级文本选中。
- 约束 OCR 插件坐标框支持声明、结构化分段返回方式和本地 `Smart` 分段回退策略。

## 关键入口
- `STranslate/ViewModels/ImageTranslateWindowViewModel.cs`
  - `ExecuteAsync(Bitmap)`：图片翻译窗口主执行命令。
  - `ApplyLayoutAnalysis(OcrResult)`：按分段模式生成 `OcrLayoutBlock`。
  - `ImageTranslateRenderer.CreateTranslatedOverlay(...)`：生成译文矢量绘制文档和原图坐标系中的选择框。
  - `RefreshSelectableOcrWords()`：在原文标注图和矢量译文视图之间切换图片文本选中数据源。
- `STranslate/Views/ImageTranslateWindow.xaml` / `ImageTranslateCompactWindow.xaml`
  - `Standalone`：原独立窗口，保留服务、语言、文本框和完整工具栏。
  - `Compact`：无标题精简窗口，图片区贴回截图选区，底部预留悬浮核心按钮区。
- `STranslate/Core/PinnedWindowController.cs` / `STranslate/Views/PinnedImageTranslateWindow.xaml`
  - 把已完成的 Compact 结果保存为静态快照；贴图窗口不再持有 ViewModel，也不重新执行 OCR 或翻译。
  - 截图开始前临时 cloak 全部贴图，截图结束后恢复，避免旧贴图进入新截图。
- `STranslate/Core/Screenshot.cs`
  - `GetScreenshotCaptureAsync()`：调用 `ScreenGrabber.CaptureWithRegionAsync`，截图时直接回传选区物理坐标，无需事后反推。
  - 精简窗口模式下传 `padImage: false`，关闭 ScreenGrab 对 <64px 小截图的背景画布 padding 扩展，保证 `bitmap.Size == 选区物理尺寸`，避免贴回时 Viewbox 把 padding 图缩放导致原始内容缩小；其他窗口模式保留默认 padding 行为。
- `STranslate/Core/OcrLayoutAnalyzer.cs`
  - `AnalyzeBlocks(OcrResult, LayoutAnalysisMode)`：分段逻辑入口。
  - `Auto` / `Provider` / `Smart` / `NoMerge`：图片翻译分段策略。
- `STranslate/Core/OcrLayoutBlock.cs`
  - 图片翻译内部分段块，记录段落框、行框、分段来源和置信度。
- `STranslate/Core/ImageTranslateTextOverlayLayout.cs`
  - 计算覆盖层矩形、擦除矩形、字体大小、多行回退、裁剪策略和主题色。
- `STranslate/Services/OcrService.cs`
  - `GetImageTranslateOcrServices()` / `GetImageTranslateOcrServiceOrDefault()`：图片翻译 OCR 服务筛选和兜底。
- `STranslate/Services/TranslateService.cs`
  - `ImageTranslateService`：图片翻译专用翻译服务。
- `STranslate.Plugin/IOcrPlugin.cs`
  - `IOcrPlugin.SupportBoxPoints()`、`OcrRequest`、`OcrResult`、`BoxPoint`。

## 从入口到结果
1. 入口来自主窗口图片翻译命令、图片翻译窗口导入文件、剪贴板图片、重新执行或窗口内截图。
2. 主窗口图片翻译入口会先关闭已打开的图片翻译独立窗口或精简窗口，再截图，避免把旧结果截入新图片。
3. `IScreenshot.GetScreenshotCaptureAsync()` 调用 `ScreenGrabber.CaptureWithRegionAsync`，截图时即回传选区物理坐标；精简窗口优先按该坐标贴回选区，仅异常情况下回退到光标附近定位。
4. `Settings.ImageTranslateWindowMode` 决定使用 `ImageTranslateWindow` 还是 `ImageTranslateCompactWindow` 承载同一个 `ImageTranslateWindowViewModel`。
5. `ImageTranslateWindowViewModel.ExecuteAsync(bitmap)` 清空旧状态，缓存 `_sourceImage` 并显示原图。
6. 获取图片翻译专用 OCR：`OcrService.GetImageTranslateOcrSvcOrDefault()`。候选服务必须实现 `IOcrPlugin`，并让 `SupportBoxPoints()` 返回 `true`。
7. 宿主用真实图片尺寸构造 `OcrRequest(data, Settings.ImageTranslateOcrLanguage, bitmap.Width, bitmap.Height)`，插件必须返回图片像素坐标 `BoxPoints`。
8. OCR 返回后调用 `Utilities.PrepareOcrResult()`；如果插件只填充结构化 `Regions`，宿主会投影出兼容的 `OcrContents`。
9. 原始 OCR 结果用于图片文本选中：`OcrWordBuilder.CreateFromOcrContents(_lastOcrResult.OcrContents)` 生成原文选中块。
10. `ApplyLayoutAnalysis()` 生成 `OcrLayoutBlock`，并把分析后的块投影回 `OcrResult.OcrContents`，供标注图、复制和结果文本复用。
11. 获取 `TranslateService.ImageTranslateService`，该服务必须是 `ITranslatePlugin`，词典类服务不会进入图片翻译翻译列表。
12. 对每个 `OcrLayoutBlock.Text` 并发执行语言检测和翻译；翻译成功后用 `ImageTranslateTextOverlayLayout.NormalizeOverlayText()` 收敛空白，再回写到对应 block。
13. 使用翻译后的分段块生成 `ImageTranslateOverlayDocument`：每个绘制项保存覆盖背景、裁剪范围、阴影和 `FormattedText`；选择框保持原图像素坐标，不再创建超采样结果位图。
14. `ImageZoom` 在同一个 Viewbox 内按“原图 → 矢量译文 Overlay → 文字选择高亮”绘制，图片缩放、拖动和 DPI 变换会同步作用于三层。
15. `Settings.IsImTranShowingAnnotated` 控制显示标注图还是原图加译文 Overlay；图片文本选中同步切换为原文块或译文块。
16. Compact 结果完成后可通过贴图按钮或可选窗口快捷键创建静态贴图；创建成功后关闭 Compact，后续截图翻译使用新的窗口和执行链路。

## 窗口模式
- 设置 → 通用 → 立即翻译提供独立开关：切换图片翻译 OCR 服务、翻译服务、语言和分段后立即重新翻译，默认开启，关闭后沿用手动重新执行的行为。
- 语言开关覆盖 `ImageTranslateOcrLanguage`、`ImageTranslateSourceLang` 和 `ImageTranslateTargetLang`；分段开关对应 `LayoutAnalysisMode`。独立窗口与精简窗口共用 ViewModel，因此都适用。
- 自动重新执行复用当前原图，重新完成 OCR、分段与翻译；没有图片、正在执行或 ViewModel 已释放时不触发。切换标注/译文显示不触发请求。
- `Standalone` 是默认模式，保留当前可缩放、可调整大小的独立窗口。
- `Compact` 使用无标题、不可缩放、非任务栏、**完全透明**窗口，窗口本身无背景色；屏幕上只看到截图内容 + 悬浮按钮条（按钮条自带半透明胶囊背景）。
- 精简窗口的图片始终钉在截图选区的物理屏幕位置（贴图位置不变铁律）；按钮条作为悬浮额外内容，根据空间自动选择位置：
  - 横向：按钮条窄于选区时居中；宽于选区时贴左缘向右延展，右边放不下则镜像向左延展。
  - 纵向：默认在图片下方；下方放不下翻上方；上下都放不下则叠加在图片底部之上（按钮条 ZIndex 高于图片）。
- 布局算法在 `ImageTranslateCompactWindowPlacement.CreateLayout`，返回窗口矩形、图片偏移、按钮条位置与 `ToolbarSide`（`Below`/`Above`/`Overlay`），由 `ImageTranslateCompactWindow.ApplyLayoutToVisualTree` 换算成 DIP 应用到 `ImageZoom` 与按钮条 `Border`。
- 精简窗口不支持图片拖拽、滚轮缩放或双击复位，底部只保留关闭、复制/全选、标注切换、重新截图、重新执行和设置等核心按钮。
- 精简窗口按 `Esc`、点击窗口外部或再次触发图片翻译关闭；右键菜单、由菜单打开的保存对话框和窗口内部文字选择不会触发外部关闭。
- 精简窗口不显示右侧文本框，`Settings.IsImTranShowingTextControl` 只影响独立窗口。

## Compact 静态贴图
- 只有已完成且具有有效译文 Overlay 的 Compact 结果可以贴图；执行中、失败或无覆盖结果时贴图按钮保持禁用。
- 贴图入口包括 Compact 工具栏按钮和 `HotkeySettings.PinImageTranslateHotkey` 窗口快捷键；快捷键默认 `Ctrl+T`，支持用户修改或清空。
- 贴图快照仅保留冻结的原图、译文 Overlay、原文/译文选择数据和截图物理矩形，不保留 OCR 标注图、翻译服务或 `ImageTranslateWindowViewModel`。
- 显示原图时使用未经标注的 `SourceImage` 且关闭 Overlay；显示译文时使用同一原图并叠加静态 `TranslationOverlay`。
- 贴图窗口无标题栏和工具栏，右键菜单支持复制全文、复制选区、原图/译文切换和关闭；获得焦点后可通过 `HotkeySettings.SwitchImageHotkey`（默认 `Ctrl+/`）切换原图/译文，跟随快捷键配置实时更新；文字区域可选择复制，空白区域可拖动，方向键可微调位置，`Esc` 或空白处双击关闭。
- 每个贴图只有一个无边框置顶 HWND；失焦时在同一透明窗口内绘制轻量阴影，获得键盘焦点后切换为蓝色辉光，明确当前接收 `Esc`、方向键和复制操作的贴图，不创建伴随窗口。
- `PinnedCaptureCoordinator` 使用非排队门控：截图进行中再次触发会直接忽略；截图前关闭贴图右键菜单并 cloak 所有贴图，截图结束后统一恢复。

## 分段模式
- `Auto`：默认模式。OCR 返回结构化 `Regions` 时使用 Provider 段落；没有结构化分段时回退 `Smart`。
- `Provider`：只使用服务商结构化 `Regions -> Paragraphs -> Lines`；缺失结构化分段时退化为 `NoMerge`，不自行猜段落。
- `Smart`：本地智能分段。适用于只返回扁平 `OcrContents` 但有坐标框的 OCR。
- `NoMerge`：保留 OCR 原始块，适合用户希望逐块翻译或服务商块已经足够稳定的场景。
- 无有效坐标：跳过智能分段，保留 OCR 返回文本；图片翻译无法可靠生成覆盖框和图片文本选中框。

## Smart 分段策略
`Smart` 只在宿主内部生效，不改变插件接口和外部枚举。

1. `BuildLineSegments()` 先按 Y 位置恢复视觉行，并按横向间距拆成行内 segment。
2. 表格/网格上下文会提前参与视觉行拆分：多行重复列起点、表格式行距和足够列跨度同时满足时，列边界优先于普通行内碎片合并，避免 `File Explorer Add-ons File Locksmith` 这类同一行跨单元格误合并。
3. 小图标、符号、图标色块等前置装饰不会单独拆开，仍和后续文本组成同一个功能项。
4. `BuildLayoutRegions()` 按列/区域相似度聚合，避免不同栏之间链式吞并。
5. `AnalyzeRegion()` 在 region 内合并 paragraph；普通段落、PDF 连续行、多列正文和英文断词续行继续使用原段落合并规则。
6. 表格/网格 region 会被识别为 `TableLike`：至少多行、多列、多个视觉行有横向 peers，并且列左边缘或中心点在多行重复对齐。
7. `TableLike` region 内禁止跨视觉行继续追加为同一 paragraph，所以功能列表、表格单元格默认按每个视觉行/单元项独立翻译。
8. 单个 OCR 块内如果包含从首行开始、连续递增的编号列表（如 `1.`、`2.`、`3.`），会按编号恢复为独立条目；条目自身的换行续文仍保留在同一分段。普通多行正文和不连续数字行不会触发该拆分。

## 结构化 OCR 与插件契约
图片翻译对 OCR 插件的要求比普通 OCR 更高：

- 必须 override `IOcrPlugin.SupportBoxPoints()` 并返回 `true`。
- 必须为文本块返回图片像素坐标 `BoxPoints`。
- 可以只返回扁平 `OcrResult.OcrContents`，宿主会用 `Smart` 分段。
- 如果服务商能返回区域/段落/行，插件应填充 `OcrResult.Regions`。
- 内置百度 OCR 使用 `paragraph=true` 获取 `paragraphs_result.words_result_idx`，再把对应 `words_result` 行组装成 `OcrRegion -> OcrParagraph -> OcrContent`。
- `OcrResult.OcrContents` 仍是兼容旧插件和旧调用链的扁平结果；结构化插件可以同时填充它，也可以只填充 `Regions`。
- 如服务商返回归一化坐标，插件需要使用 `OcrRequest.PixelWidth` / `PixelHeight` 换算成图片像素坐标后再写入 `BoxPoints`。
- 插件不要按屏幕缩放或窗口缩放改写坐标；图片翻译使用图片自身的像素坐标。

`Auto` 模式下，结构化 OCR 的 Provider 段落优先级高于本地 `Smart`，所以插件返回的 `Regions` 会直接影响分段粒度。插件侧应尽量让 `Paragraphs` 表示真实语义段落或表格单元项，而不是把整列/整表合成一个 paragraph。

## 译文覆盖策略
- 结果视图保留原始截图作为 `ImageZoom.Source`，由单个 retained-mode `ImageTranslateOverlay` 控件绘制全部译文；不为每个文本块创建独立 WPF 控件，也不生成常驻结果位图。
- 覆盖层不直接使用截图背景采样颜色，而是跟随软件主题：
  - 浅色主题：浅色覆盖层 + 黑字。
  - 深色主题：深色覆盖层 + 白字。
- 擦除区域优先来自 `OcrLayoutBlock.LineBoxPoints`，尽量只擦除原文行；缺少行框时退回 block 外接框。
- 段落框与行框范围不一致时，译文选区取两者并集，避免只按段落框的局部高度缩小字号。
- 字体大小按完整译文选区动态拟合；原文单行块先按单行渲染，最小字号仍放不下时才扩展为多行区域，扩展高度最多约为原行高的 `3.2` 倍。
- 所有多行译文统一使用 `1.24 × 字号` 作为基础行高，字号测量与最终绘制使用相同规则，不区分译文语言。
- 多行译文达到至少 3 行且排版高度不足原选区 90% 时，会按实际行数自适应增加行高，最大不超过 `2.0 × 字号`；仍有余量时整体垂直居中。
- 如果最小字号仍放不下，保留裁剪/截断保护，避免文本绘制溢出到相邻区域。
- 覆盖文本会先归一化空白，减少翻译服务返回的多余换行和空格破坏布局。

## 图片文本选中
- `ImageZoom` 使用 `OcrWords` 模拟图片上的文本选中。
- 标注图显示时，`OcrWords` 来自原始 OCR 坐标和原文文本。
- 结果视图显示时，`OcrWords` 来自矢量文档中的译文字符框，复制所选内容时拿到的是译文。
- 鼠标拖动可按字符选中；在文字上双击会选中当前视觉行，自动换行的译文只选中点击所在行。
- 未选择文字时复制图片继续使用原图；“保存图片”按原图分辨率导出当前图片显示内容：标注模式保存 OCR 标注图，译文模式临时栅格化原图与译文 Overlay。文字选择高亮、窗口缩放和工具栏不会进入导出结果，临时位图也不会由 ViewModel 常驻持有。
- 缺少坐标框时显示无位置信息提示，图片上无法可靠模拟选区。

## 服务绑定与配置
- OCR 专用绑定：`ServiceSettings.ImageTranslateOcrSvcID`，由图片翻译窗口的 OCR 选择写入。
- 翻译专用绑定：`ServiceSettings.ImageTranslateSvcID`，由图片翻译窗口的翻译服务选择写入。
- 服务缺失或插件被删除时，启动加载会重置失效的图片翻译服务 ID。
- `Settings.ImageTranslateWindowMode` 控制图片翻译结果使用独立窗口或精简窗口，默认 `Standalone`。
- `Settings.LayoutAnalysisMode` 是分段模式配置，默认 `Auto`，序列化支持 `auto`、`provider`、`smart`、`noMerge`；旧未知值归一为 `Auto`。
- `Settings.IsImTranShowingAnnotated` 控制标注图/原图加矢量译文 Overlay 显示。
- `Settings.IsImTranShowingTextControl` 控制图片翻译窗口文本区域显示。
- `Settings.ImageTranslateOcrLanguage` 控制图片翻译 OCR 识别语言，独立于截图翻译、静默 OCR 和 OCR 窗口。
- `Settings.IsImageTranslateCompactOcrLanguageVisible` 控制精简窗口底部工具条是否显示图片翻译 OCR 识别语言选框，默认隐藏。
- `HotkeySettings.PinImageTranslateHotkey` 控制 Compact 窗口内的贴图快捷键，默认 `Ctrl+T`。
- `Settings.ImageTranslateSourceLang` / `ImageTranslateTargetLang` 控制图片翻译语言。
- `Settings.ShowImageTranslateItemInNotifyIconMenu` 控制托盘菜单是否显示图片翻译入口。

## 错误处理
- 图片翻译 OCR 服务未配置：`Helper.PromptConfigureService()` 弹出配置提示并定位到 `OcrPage`。
- 图片翻译翻译服务未配置：窗口内 `_snackbar.ShowWarning("NoTranslateService")`。
- OCR 失败、翻译异常或运行时异常：窗口内 Snackbar 提示，日志写入 `ImageTranslateWindowViewModel` logger。
- 语言检测失败：当前 block 跳过翻译并提示 `LanguageDetectionFailed`。
- 用户取消执行：捕获 `TaskCanceledException`，当前实现不额外弹提示。

## 内存与生命周期
图片翻译窗口的内存释放主要涉及窗口视觉树、消息提示控件和 ViewModel 所有权。精简窗口与独立窗口都使用独立 DI scope，并在关闭时显式拆解各自的长生命周期持有链：

- **窗口视觉树泄漏（已修复）**：精简窗口每次截图翻译都新建并 `OnDeactivated → Close()` 关闭。旧版 `ui:InfoBar` 会被 WPF 静态 `System.ComponentModel.PropertyDescriptor._propertyMap` 通过 `MS.Internal.ComponentModel.DependencyObjectPropertyDescriptor` 注册 `PropertyChangeTracker`，窗口关闭后反向钉死整窗及其 BitmapSource 原生帧缓冲。
  - 根因修复：`SnackbarContainer`、图片翻译/OCR 窗口的内联提示和热键冲突提示统一改用纯 WPF `NoticeBar`，应用代码不再创建 iNKORE `InfoBar`。
  - Snackbar 所有权：删除窗口 XAML 上的 `SnackbarBehavior` 预挂载；全局 `Snackbar` 首次向某个窗口显示消息时才把一个 `SnackbarContainer` 加入该窗口，并在 `Window.Closed` 时从视觉树移除并 `Dispose()`。
  - 容器清理：`SnackbarContainer.Dispose()` 幂等停止自动隐藏计时器和可控 Storyboard，解绑 `NoticeBar` 事件并清除 action callback；释放后再次 `Show()` 会抛出 `ObjectDisposedException`。
  - 防御性清理：`ImageTranslateCompactWindow.OnClosed` 在释放独立 DI scope 前继续调用 `DetachVisualTree()`，清除子元素、`Content` 和 `DataContext`，避免后续新增控件重新把整窗生命周期延长。
  - 独立窗口的 iNKORE `TitleBarControl` 会通过 `DependencyPropertyDescriptor` 监听父窗口的 `WindowStyle` / `ResizeMode`；普通 `Window.Close()` 不会触发其视觉父级清理。`ImageTranslateWindow.OnClosing` 会显式解除这两个监听并替换 modern window template，`OnClosed` 再清空内容、输入绑定和 `DataContext`。

- **ViewModel 被 DI root scope 钉住（两个图片翻译窗口均已修复）**：`ImageTranslateWindowViewModel` 注册为 `Transient` 且实现 `IDisposable`。Microsoft.Extensions.DI 会跟踪从 **root provider** 解析的 Disposable Transient 服务，窗口手动调用 `Dispose()` 也不会把实例从 root scope 的 `_disposables` 列表移除；实例要到应用退出、根容器释放时才解除引用。
  - 修复：精简窗口和独立窗口都改用 `Ioc.Default.CreateScope()` 解析 VM，`OnClosed` 时 `scope.Dispose()` 释放 VM，使其脱离 root 跟踪列表。
  - **该类累积的条件**：①Disposable Transient VM 从 root provider 解析；②窗口反复新建关闭。只有同时满足才会按窗口创建次数在 root scope 中累积。

- **其他窗口的同类修复**：`OcrWindow`、`WelcomeSetupWindow`、`SettingsWindow` 的 transient VM 同样会从 root provider 解析，现已统一改用 `Ioc.Default.CreateScope()` 解析，`OnClosed` 经 `ModernWindowLifecycle.Release(this, _serviceScope.Dispose)` 释放。
  - `OcrWindow` / `WelcomeSetupWindow` 的 VM 实现了 `IDisposable`，关闭时释放 scope 会触发 `Dispose()`，取消对 `OcrService` / `Settings` 等单例的事件订阅。
  - `SettingsWindowViewModel` 未实现 `IDisposable`，但其各页面与页面 VM（如 `HistoryViewModel`）为 `Scoped` 注册，从 root provider 解析会被 root scope 跟踪、`Dispose()` 永不触发；改用独立 scope 后关闭窗口即可释放已解析的页面 VM，触发其 `Dispose()` 取消全局订阅。
  - `SettingsWindow.OnClosed` 在释放 scope 前还会清空 `RootFrame.Content` 并解绑导航事件，避免长期页面通过 `Frame` 反向持有已关闭窗口。

- **译文矢量覆盖（已优化）**：
  - 结果视图直接复用已缓存并 `Freeze` 的 `_sourceImage`，译文由 `ImageTranslateOverlay` 按原图坐标执行 retained-mode 矢量绘制。
  - 旧版按图片尺寸进行 2–4 倍超采样并生成 `RenderTargetBitmap`；该结果位图和对应常驻原生帧缓冲已移除。标注图仍只按原始分辨率生成。
  - Overlay 文档随 ViewModel 清理，窗口关闭时再由现有视觉树与 DI scope 释放链断开引用。

- **回归测试**：`SnackbarLifecycleTests` 覆盖 `NoticeBar.IsOpen` 可见性、容器重复释放、显示/隐藏动画最终状态，以及 `NoticeBar`/`SnackbarContainer` 在重复创建释放后可被 GC 回收；`ModernWindowLifecycleTests` 验证 modern 窗口关闭后 `WindowStyle` / `ResizeMode` 的属性描述符 tracker 已移除，并验证视觉树引用被清空。

- **诊断方法**：复现泄漏后用 `dotnet-gcdump report` 看是否有 `ImageTranslateCompactWindow`/`ImageTranslateWindowViewModel` 实例数随操作次数线性增长；用 `dotnet-dump analyze` 的 `gcroot <地址>` 追踪引用链，确认是 `PropertyDescriptor._propertyMap` 静态链还是 `ServiceProviderEngineScope._disposables` 钉住。

## 关键文件
- `STranslate/ViewModels/ImageTranslateWindowViewModel.cs`
- `STranslate/Views/ImageTranslateWindow.xaml.cs`
- `STranslate/Views/ImageTranslateCompactWindow.xaml`
- `STranslate/Controls/NoticeBar.xaml`
- `STranslate/Controls/SnackbarContainer.xaml`
- `STranslate/Controls/ImageTranslateOverlay.cs`
- `STranslate/Core/Snackbar.cs`
- `STranslate/Core/ImageTranslateOverlayDocument.cs`
- `STranslate/Helpers/ImageTranslateRenderer.cs`
- `STranslate/Helpers/ModernWindowLifecycle.cs`
- `STranslate/Core/Screenshot.cs`
- `STranslate/Core/PinnedWindowController.cs`
- `STranslate/Views/PinnedImageTranslateWindow.xaml`
- `STranslate/Core/OcrLayoutAnalyzer.cs`
- `STranslate/Core/OcrLayoutBlock.cs`
- `STranslate/Core/ImageTranslateTextOverlayLayout.cs`
- `STranslate/Core/LayoutAnalysisModeJsonConverter.cs`
- `STranslate/Services/OcrService.cs`
- `STranslate/Services/TranslateService.cs`
- `STranslate.Plugin/IOcrPlugin.cs`
- `Tests/STranslate.Tests/OcrLayoutAnalyzerTests.cs`
- `Tests/STranslate.Tests/ImageTranslateTextOverlayLayoutTests.cs`
- `Tests/STranslate.Tests/ImageTranslateOverlayTests.cs`
- `Tests/STranslate.Tests/PinnedImageTranslateTests.cs`
- `Tests/STranslate.Tests/ModernWindowLifecycleTests.cs`
- `Tests/STranslate.Tests/SnackbarLifecycleTests.cs`

## 常见改动任务
- 调整图片翻译分段逻辑或表格/网格误合并：优先改 `OcrLayoutAnalyzer`，并补 `OcrLayoutAnalyzerTests`。
- 调整译文覆盖大小、裁剪、擦除范围或主题颜色：改 `ImageTranslateTextOverlayLayout`，并补 `ImageTranslateTextOverlayLayoutTests`。
- 接入服务商结构化 OCR：插件填充 `OcrResult.Regions`，并确保每个 `OcrContent` 有图片像素坐标 `BoxPoints`。
- 调整图片翻译 OCR 候选服务：改 `OcrService.IsImageTranslateOcrService()` / `GetImageTranslateOcrServices()`。
- 调整图片翻译翻译服务候选：改 `ImageTranslateWindowViewModel.OnTransFilter()` 或 `TranslateService.ImageTranslateService` 相关逻辑。
- 调整图片上选中文本行为：改 `RefreshSelectableOcrWords()`、`OcrWordBuilder` 或 `ImageZoom` 的选区逻辑。
- 调整精简窗口定位或关闭行为：改 `ImageTranslateCompactWindow` 的 `PlaceForCapture` / `PlaceOnPhysicalWindowBounds`，选区物理坐标由 `Screenshot.GetScreenshotCaptureAsync` 经 ScreenGrab `CaptureWithRegionAsync` 直接回传。
- 调整精简窗口布局/定位/按钮条翻向逻辑：改 `ImageTranslateCompactWindowPlacement.CreateLayout`，并补 `ImageTranslateCompactWindowPlacementTests` 对应场景；按钮条尺寸/间距常量在 `ImageTranslateCompactWindow`（`ToolbarWidth`/`GapH`/`GapV`/`WindowMargin`）。
- 排查/修复图片翻译窗口内存泄漏：优先看"内存与生命周期"章节。精简窗口关闭走 `ImageTranslateCompactWindow.OnClosed` → `DetachVisualTree` + `_serviceScope.Dispose`；独立窗口关闭走 `DetachModernWindowStyle` → `DetachVisualTree` → `_serviceScope.Dispose`；消息提示生命周期由 `Snackbar`、`SnackbarContainer` 和 `NoticeBar` 负责，禁止重新引入未配对 `DependencyPropertyDescriptor.AddValueChanged` 的控件。
