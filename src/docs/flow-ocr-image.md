# OCR 与图片翻译链路

## 模块职责
- 提供截图 OCR、OCR 窗口、图片翻译窗口三条图像文本处理链路。
- 管理 OCR 服务与图片翻译专用 OCR/翻译服务绑定。
- 在图片翻译中完成版面分析、文本块合并、翻译回写与结果图生成。
- 将截图翻译和静默 OCR 结果接入统一取词文本后处理。

## 关键入口
- `STranslate/ViewModels/MainWindowViewModel.cs`
  - `ScreenshotTranslateAsync()` / `ScreenshotTranslateHandlerAsync()`
  - `OcrAsync()` / `OcrHandlerAsync()`
  - `ImageTranslateAsync()` / `ImageTranslateHandlerAsync()`
- `STranslate/ViewModels/OcrWindowViewModel.cs`
  - `ExecuteAsync(Bitmap)`：OCR 窗口主执行命令。
- `STranslate/ViewModels/ImageTranslateWindowViewModel.cs`
  - `ExecuteAsync(Bitmap)`：图片翻译窗口主执行命令。
  - `ApplyLayoutAnalysis(OcrResult)`：OCR 文本块空间合并。
- `STranslate/Core/Screenshot.cs`
  - `GetScreenshotAsync()`：截图前隐藏主窗口，调用 `ScreenGrabber`。
- `STranslate/Services/OcrService.cs`
  - `ImageTranslateOcrService`：图片翻译专用 OCR 服务选择与持久化。

## 核心流程
### 从入口到结果：主窗口截图翻译
1. `MainWindowViewModel.ScreenshotTranslateAsync()` 先取可用 OCR 服务。
2. 通过 `IScreenshot.GetScreenshotAsync()` 获取截图位图（主窗口可见且非置顶时先折叠，避免截到自身）。
3. `ScreenshotTranslateHandlerAsync()` 调 OCR `RecognizeAsync()`。
4. OCR 成功后：按设置可复制识别文本，再通过 `HandleCapturedText(text, TextSeparatorHandleScope.ScreenshotTranslate)` 处理换行与可选分隔符。
5. 处理后的文本调用 `ExecuteTranslate()` 进入主翻译链路。
6. `Settings.FocusInputAfterScreenshotTranslate` 控制截图翻译完成后是否显示主窗口并聚焦输入框；关闭且主窗口置顶时只更新结果不抢焦点，非置顶时仍会恢复主窗口。

### 从入口到结果：静默 OCR
1. `SilentOcrAsync()` 截图后调用当前 OCR 服务识别文本。
2. 识别成功后写入剪贴板。
3. 若启用了 `TextSeparatorHandleScope.SilentOcr`，写入前会复用 `HandleCapturedText()` 处理换行与 `_` / `-` 分隔符。

### 从入口到结果：OCR 窗口执行
1. `OcrWindowViewModel.ExecuteAsync(bitmap)` 设置执行态并清理旧结果。
2. 调用当前启用的 OCR 服务 `RecognizeAsync(new OcrRequest(data, Settings.OcrLanguage))`。
3. 生成两类展示数据：
   - 原图/标注图（边框）
   - `OcrWords` 与 `Result` 文本
4. 根据 `Settings.IsOcrShowingAnnotated` 决定显示原图还是标注图。

### 从入口到结果：图片翻译窗口执行
1. `ImageTranslateWindowViewModel.ExecuteAsync(bitmap)` 获取图片翻译专用 OCR 服务（无则回退启用 OCR 服务）。
2. OCR 后执行 `ApplyLayoutAnalysis()`：按空间相邻关系分组合并文本块。
3. 获取 `TranslateService.ImageTranslateService`（必须是 `ITranslatePlugin`，词典服务不支持）。
4. 并发翻译每个 `OcrContent.Text`，再把译文覆盖回 `OcrResult.OcrContents`。
5. 生成两类图：
   - `_annotatedImage`：合并后边框图
   - `_resultImage`：在原图覆盖译文
6. `Settings.IsImTranShowingAnnotated` 控制最终显示哪种图。

### 图片翻译专用服务绑定
- OCR 专用绑定：`OcrService.ImageTranslateOcrService`，由 `OnSelectedOcrEngineChanged` 写入 `ServiceSettings.ImageTranslateOcrSvcID`。
- 翻译专用绑定：`TranslateService.ImageTranslateService`，由 `OnSelectedTranslateEngineChanged` 写入 `ServiceSettings.ImageTranslateSvcID`。

### 托盘入口
- 主窗口托盘菜单可通过 `Settings.ShowImageTranslateItemInNotifyIconMenu` 控制是否显示“图片翻译”入口。
- 托盘右键触发截图类命令后，需要注意上下文菜单残留；截图入口应在执行前确保菜单状态已收敛。

## 错误处理与通知策略

### 服务未配置（阻断性错误）
当 OCR / TTS 等核心服务未配置或全部禁用时，使用 `Helper.PromptConfigureService` 弹出 MessageBox（OK/Cancel）。弹窗底层统一走 `AppMessageBox`，活动窗口优先、没有活动窗口时通过主屏中心的临时透明 owner 显示：
- 用户点击 **确定** → 自动打开设置窗口并定位到对应配置页。
- 用户点击 **取消** → 仅关闭弹窗，不跳转。

具体映射：
| 功能 | 未配置服务 | 跳转页面 | 涉及 ViewModel |
|---|---|---|---|
| 截图翻译 | OCR | `OcrPage` | `MainWindowViewModel` |
| 图片翻译窗口 | 图片翻译专用 OCR | `OcrPage` | `ImageTranslateWindowViewModel` |
| OCR 窗口 | OCR | `OcrPage` | `OcrWindowViewModel` |
| 朗读（主窗口/OCR窗口/图片翻译窗口） | TTS | `TtsPage` | `MainWindowViewModel` / `OcrWindowViewModel` / `ImageTranslateWindowViewModel` |

### 运行时失败
OCR / 图片翻译执行过程中抛出异常或返回失败结果时，使用当前窗口内的 **Snackbar** 提示：
- `MainWindowViewModel.ScreenshotTranslateHandlerAsync` catch：先 `Show()` 主窗口，再 `_snackbar.ShowError`。
- `MainWindowViewModel.SilentOcrHandlerAsync` catch：先 `Show()` 主窗口，再 `_snackbar.ShowError`（静默场景下没有可见窗口，必须先让窗口出现）。
- `OcrWindowViewModel.ExecuteAsync` catch：`_snackbar.ShowError`。
- `ImageTranslateWindowViewModel.ExecuteAsync` catch：`_snackbar.ShowError`。
- OCR 识别成功但结果为空：`OcrWindowViewModel` / `ImageTranslateWindowViewModel` 使用 `_snackbar.ShowWarning`。

### 其他 Snackbar 提示
- 图片翻译服务未配置（`ImageTranslateService` 不可用）：`_snackbar.ShowWarning("NoTranslateService")`。
- 图片翻译语言检测失败：`_snackbar.ShowWarning("LanguageDetectionFailed")`。

## 关键数据结构/配置
- `OcrResult` / `OcrContent` / `BoxPoint`：OCR 原始与结构化文本块。
- 版面分析参数（`Settings`）：
  - `VerticalThresholdRatio`
  - `HorizontalThresholdRatio`
  - `LineSpacingThresholdRatio`
  - `WordSpacingThresholdRatio`
- 图像展示设置：
  - `IsOcrShowingAnnotated`
  - `IsImTranShowingAnnotated`
  - `IsImTranShowingTextControl`
  - `ImageQuality`
  - `ShowImageTranslateItemInNotifyIconMenu`
- OCR 语言设置：`OcrLanguage`
- 截图翻译焦点设置：`FocusInputAfterScreenshotTranslate`
- 取词后处理设置：`TextSeparatorHandleType`、`TextSeparatorHandleScopes`

## 关键文件
- `STranslate/ViewModels/MainWindowViewModel.cs`
- `STranslate/ViewModels/OcrWindowViewModel.cs`
- `STranslate/ViewModels/ImageTranslateWindowViewModel.cs`
- `STranslate/Core/Screenshot.cs`
- `STranslate/Services/OcrService.cs`
- `STranslate/Services/TranslateService.cs`
- `STranslate.Plugin/IOcrPlugin.cs`

## 常见改动任务
- 调整版面合并效果：优先改 `ApplyLayoutAnalysis()` 与四个阈值参数，不要只改渲染层。
- 新增图片翻译后处理：应在翻译回写 `content.Text` 后、`GenerateTranslatedImage` 前插入。
- 截图行为改造：在 `Screenshot.GetScreenshotAsync()` 处理窗口折叠与等待时机。
- OCR 服务优先级策略调整：修改 `OcrService.GetImageTranslateOcrServiceOrDefault()` 与对应 VM 的选中逻辑。
- OCR 结果进入翻译或剪贴板前的文本清洗：优先复用 `MainWindowViewModel.HandleCapturedText()`，避免截图翻译和静默 OCR 行为分叉。
