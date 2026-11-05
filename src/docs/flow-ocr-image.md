# OCR 链路

## 模块职责
- 提供截图翻译、静默 OCR 和 OCR 窗口三条图像文本识别链路。
- 管理普通 OCR 服务选择、截图/静默 OCR 语言、独立 OCR 窗口语言、标注图和文本输出。
- 将截图翻译和静默 OCR 结果接入统一取词文本后处理。
- 图片翻译已拆到独立文档：[flow-image-translation.md](flow-image-translation.md)。

## 关键入口
- `STranslate/ViewModels/MainWindowViewModel.cs`
  - `ScreenshotTranslateAsync()` / `ScreenshotTranslateHandlerAsync()`：截图翻译。
  - `OcrAsync()` / `OcrHandlerAsync()`：OCR 窗口入口。
  - `SilentOcrAsync()` / `SilentOcrHandlerAsync()`：静默 OCR。
  - `HandleCapturedText(string, TextSeparatorHandleScope)`：截图翻译和静默 OCR 的文本后处理。
- `STranslate/ViewModels/OcrWindowViewModel.cs`
  - `ExecuteAsync(Bitmap)`：OCR 窗口主执行命令。
- `STranslate/Core/Screenshot.cs`
  - `GetScreenshotAsync()`：截图前隐藏主窗口，调用 `ScreenGrabber`。
- `STranslate/Core/OcrWordBuilder.cs`
  - 把带坐标的 `OcrContent` 转成图片文本选中所需的 `OcrWord`。
- `STranslate/Core/QrCodeDecoder.cs`
  - 本地二维码解码，并限制可直接打开的链接协议为 `http` / `https`。
- `STranslate/Helpers/QrCodeOverlayBuilder.cs`
  - 根据二维码定位点生成与原图坐标一致的内容覆盖层，并处理边界扩展与长文本截断。
- `STranslate/Core/Utilities.cs`
  - `PrepareOcrResult()`：当结构化 OCR 没有扁平内容时投影出 `OcrContents`。
- `STranslate.Plugin/IOcrPlugin.cs`
  - `IOcrPlugin`、`OcrRequest`、`OcrResult`、`OcrContent`、`BoxPoint`。

## 从入口到结果
### 主窗口截图翻译
1. `MainWindowViewModel.ScreenshotTranslateAsync()` 获取当前可用 OCR 服务。
2. 通过 `IScreenshot.GetScreenshotAsync()` 获取截图位图；主窗口可见且非置顶时会先折叠，避免截到自身。
3. `ScreenshotTranslateHandlerAsync()` 调用 OCR 插件 `RecognizeAsync()`。
4. OCR 成功后，按设置可复制识别文本。
5. 调用 `HandleCapturedText(text, TextSeparatorHandleScope.ScreenshotTranslate)` 处理换行与可选分隔符。
6. 处理后的文本进入 `ExecuteTranslate()`，复用主翻译链路。
7. `Settings.FocusInputAfterScreenshotTranslate` 控制完成后是否显示主窗口并聚焦输入框；关闭且主窗口置顶时只更新结果不抢焦点。

### 静默 OCR
1. `SilentOcrAsync()` 截图后调用当前 OCR 服务识别文本。
2. 识别成功后写入剪贴板。
3. 若启用了 `TextSeparatorHandleScope.SilentOcr`，写入前复用 `HandleCapturedText()` 处理换行与 `_` / `-` 分隔符。
4. 运行失败时会先显示主窗口，再通过 Snackbar 提示，避免静默场景下错误不可见。

### OCR 窗口执行
设置 → 通用 → 立即翻译提供 `OcrOnServiceChanged` 和 `OcrOnLanguageChanged`，分别控制切换 OCR 服务、`OcrWindowOcrLanguage` 后是否使用当前图片立即重新识别。两个开关默认开启；没有图片、正在执行或 ViewModel 已释放时不触发，不影响截图翻译和静默 OCR。

1. `OcrWindowViewModel.ExecuteAsync(bitmap)` 设置执行态并清理旧结果。
2. 并行执行 OCR 服务和本地二维码解码。
3. 调用当前启用的 OCR 服务：
   `RecognizeAsync(new OcrRequest(data, Settings.OcrWindowOcrLanguage, bitmap.Width, bitmap.Height))`。
4. OCR 返回后调用 `Utilities.PrepareOcrResult()`；如果插件只填充结构化 `Regions`，宿主会投影出兼容的 `OcrContents`。
5. 生成原图/标注图、`OcrWords` 和 `Result` 文本；二维码命中不会自动展开右侧结果面板，面板显示仍由 `Settings.IsOcrShowingTextControl` 控制；手动打开后可复制完整内容，`http` / `https` 内容还可直接打开。
   同时会在二维码坐标区域显示随图片缩放的矢量内容覆盖层；覆盖文本可使用图片文本选择交互复制，最多显示 80 个 Unicode 文本元素，完整内容不受影响。
6. 纯二维码截图同样视为识别成功，不显示误导性的 OCR 失败提示；只有 OCR 和二维码均无结果时才提示失败。
7. `Settings.IsOcrShowingAnnotated` 决定显示原图还是标注图。
8. “保存图片”捕获命令触发时的 `DisplayImage`，因此原图模式保存原图，标注模式保存当前 OCR 标注图；窗口缩放和文字选择高亮不会进入结果。

## OCR 结果模型
- `OcrResult.OcrContents`：兼容旧插件的扁平 OCR 文本块列表。
- `OcrResult.Regions`：结构化区域，层级为 `OcrRegion -> OcrParagraph -> OcrContent`。
- `OcrContent.BoxPoints`：文本坐标框顶点，使用图片像素坐标，普通 OCR 可为空。
- `OcrResult.Text`：优先从 `OcrContents` 聚合；没有扁平内容时从 `Regions` 聚合段落文本。

结构化 OCR 和图片翻译分段逻辑的细节见 [flow-image-translation.md](flow-image-translation.md)。

## 配置
- `Settings.ScreenshotOcrLanguage`：截图翻译和静默 OCR 使用的 OCR 语言。
- `Settings.OcrWindowOcrLanguage`：独立 OCR 窗口使用的 OCR 语言。
- `Settings.CopyAfterOcr`：OCR 后是否复制识别文本。
- `Settings.IsOcrShowingAnnotated`：OCR 窗口默认显示标注图还是原图。
- `Settings.FocusInputAfterScreenshotTranslate`：截图翻译完成后的主窗口焦点策略。
- `Settings.TextSeparatorHandleType` / `TextSeparatorHandleScopes`：截图翻译和静默 OCR 的取词文本后处理。

图片翻译专用 OCR 服务、翻译服务、分段模式和覆盖图设置见 [flow-image-translation.md](flow-image-translation.md)。

## OCR 窗口内存与生命周期
- `OcrWindowViewModel` 是 `Transient + IDisposable`，必须从窗口独立的 `IServiceScope` 解析；窗口关闭时释放 scope，避免 root provider 的 `_disposables` 列表把每次创建的 VM 保留到应用退出。
- `OcrWindow.OnClosing` 先取消 OCR/TTS 操作，再通过 `ModernWindowLifecycle.DetachModernWindowStyle()` 解除 iNKORE `TitleBarControl` 对父窗口 `WindowStyle` / `ResizeMode` 的属性描述符监听。
- `OcrWindow.OnClosed` 清空视觉树、输入绑定和 `DataContext`，随后释放窗口 scope。
- `ModernWindowLifecycleTests` 复用实际 modern window style，验证关闭清理后两个属性描述符 tracker 均被移除。

## 错误处理与通知策略
### 服务未配置
当 OCR 服务未配置或全部禁用时，使用 `Helper.PromptConfigureService` 弹出 MessageBox（OK/Cancel）。弹窗底层统一走 `AppMessageBox`，活动窗口优先、没有活动窗口时通过主屏中心的临时透明 owner 显示：
- 用户点击 **确定**：自动打开设置窗口并定位到 `OcrPage`。
- 用户点击 **取消**：仅关闭弹窗，不跳转。

### 运行时失败
- `MainWindowViewModel.ScreenshotTranslateHandlerAsync` catch：先 `Show()` 主窗口，再 `_snackbar.ShowError`。
- `MainWindowViewModel.SilentOcrHandlerAsync` catch：先 `Show()` 主窗口，再 `_snackbar.ShowError`。
- `OcrWindowViewModel.ExecuteAsync` catch：在 OCR 窗口内 `_snackbar.ShowError`。
- OCR 成功但文本为空：OCR 窗口使用 `_snackbar.ShowWarning`。

图片翻译窗口的服务提示、翻译失败和语言检测失败见 [flow-image-translation.md](flow-image-translation.md)。

## 关键文件
- `STranslate/ViewModels/MainWindowViewModel.cs`
- `STranslate/ViewModels/OcrWindowViewModel.cs`
- `STranslate/Core/Screenshot.cs`
- `STranslate/Core/OcrWordBuilder.cs`
- `STranslate/Core/QrCodeDecoder.cs`
- `STranslate/Helpers/QrCodeOverlayBuilder.cs`
- `STranslate/Core/Utilities.cs`
- `STranslate/Helpers/ModernWindowLifecycle.cs`
- `STranslate.Plugin/IOcrPlugin.cs`
- `Tests/STranslate.Tests/ModernWindowLifecycleTests.cs`

## 常见改动任务
- 截图行为改造：在 `Screenshot.GetScreenshotAsync()` 处理窗口折叠、等待时机和截图工具调用。
- OCR 结果进入翻译或剪贴板前的文本清洗：优先复用 `MainWindowViewModel.HandleCapturedText()`，避免截图翻译和静默 OCR 行为分叉。
- OCR 坐标问题：优先检查插件是否按图片像素坐标返回 `BoxPoints`；如果服务商返回归一化坐标，插件应使用 `OcrRequest.PixelWidth` / `PixelHeight` 自行换算。
- OCR 窗口图片文本选中：检查 `OcrWordBuilder` 与 `ImageZoom`。
- 图片翻译分段、覆盖和插件能力问题：看 [flow-image-translation.md](flow-image-translation.md)。
