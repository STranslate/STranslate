# 主翻译执行链路

## 模块职责
- 承接主窗口输入文本，驱动自动或手动翻译。
- 协调翻译插件与词典插件并发执行、缓存命中与历史写入。
- 处理自动回译、翻译后复制、历史前后导航等行为。
- 维护输入区识别状态、手动纠正源语种与历史有效语种快照。

## 关键入口
- `STranslate/ViewModels/MainWindowViewModel.cs`
  - `OnInputTextChanged()`：自动翻译防抖入口。
  - `InputClear()`：输入翻译入口，清空输入并进入临时输入翻译模式。
  - `TranslateAsync()`：主翻译命令。
  - `SingleTranslateAsync()` / `SingleTransBackAsync()`：单服务执行。
  - `ExecuteTranslateAsync()`：缓存优先 + 实时翻译编排。
  - `ResolveTranslationLanguageContextAsync()`：解析实际参与翻译的源/目标语种。
  - `HandleCapturedText()`：统一处理取词入口文本的换行与分隔符。
- `STranslate/Helpers/LanguageDetector.cs`
  - `GetLanguageAsync()`：源语种判定与目标语种推导。
  - `GetTargetLanguage()`：根据实际源语种推导目标语种。
- `STranslate/Core/SqlService.cs`
  - `GetDataAsync()` / `InsertOrUpdateDataAsync()`：历史缓存读取与落盘。
- `STranslate/Core/Utilities.cs`
  - `CapturedTextHandler()`：取词文本后处理。

## 核心流程
### 从入口到结果：输入文本到多服务翻译完成
1. 输入变化触发 `OnInputTextChanged(value)`：
   - 若 `Settings.AutoTranslate == false` 直接返回。
   - 空文本时取消防抖任务。
   - 非空时通过 `DebounceExecutor` 按 `Settings.AutoTranslateDelayMs` 延迟执行 `TranslateCommand`。
2. `TranslateAsync()` 执行前先取消防抖队列，重置已启用服务的结果对象。
3. 进入 `ExecuteTranslateAsync(checkCacheFirst)`：
   - 获取已启用且 `ExecMode == Automatic` 的服务。
   - 若启用历史缓存：用 `(InputText, SourceLang, TargetLang)` 查询 `SqlService`。
   - 命中缓存后把结果注入各插件结果对象，仅保留未命中服务继续实时执行。
4. 对未命中服务调用 `LanguageDetector.GetLanguageAsync()` 获取最终 `source/target`。
   - 当用户在“识别为”快捷入口切换语言检测器时，会更新 `Settings.LanguageDetector` 并跳过缓存重新翻译；识别状态仍由本次翻译流程按实际检测结果刷新。
   - 当用户在“识别为”后面的识别结果上下拉选择语种时，本次主翻译会跳过缓存并强制以所选语种作为 `source` 执行一次。
   - 腾讯语言检测官方服务已停止，`Tencent` 不再作为可选检测器参与运行。
5. 使用 `SemaphoreSlim` 并发执行服务：
   - `ITranslatePlugin` 走主翻译，按需执行自动回译。
   - `IDictionaryPlugin` 走词典查询路径。
6. 翻译完成后按设置执行复制逻辑，并将结果按服务顺序排序写回历史。
   - 写入前记录 `EffectiveSourceLang` / `EffectiveTargetLang`，用于历史页和 CSV 导出展示自动识别后的实际语种。
   - 每个 `HistoryData` 记录 `ServiceDisplayName`，避免服务重命名后历史结果只剩服务 ID。

### 从入口到结果：输入区显隐与输入翻译
1. 普通主窗口展示按 `Settings.HideInput` 和 `Settings.HideInputWithLangSelectControl` 控制输入框和语言选择控件。
2. 输入翻译进入 `InputClear()` 后会启用临时输入翻译模式，让输入框和语言选择控件在当前会话内可见并获得焦点。
3. 临时输入翻译模式不修改持久设置；用户关闭/取消窗口、触发直接文本翻译或手动切换输入框显隐时退出该模式。
4. `ExecuteTranslate(text, ...)` 用于已有文本直接翻译，会退出临时输入翻译模式并继续按用户隐藏设置展示结果。

### 从入口到结果：取词文本进入主翻译
1. 截图翻译、鼠标划词、划词翻译 / `Ctrl+C+C`、增量翻译、剪贴板监听等入口取得文本后，不直接进入翻译。
2. `MainWindowViewModel.HandleCapturedText(text, scope)` 先按 `Settings.LineBreakHandleType` 处理换行。
3. 若 `Settings.TextSeparatorHandleType != None` 且当前 `scope` 包含在 `Settings.TextSeparatorHandleScopes` 中，再把英文/数字标识符内部的 `_` 或 `-` 转为空格。
4. 处理后的文本再进入 `ExecuteTranslate()` 或追加到输入框；普通键盘输入不走该取词后处理。

### 从入口到结果：缓存命中与增量补全
1. `PopulateResultsFromCacheAsync()` 遍历目标服务。
2. 命中缓存时：
   - 翻译服务更新 `TransResult` / `TransBackResult`。
   - 词典服务更新 `DictionaryResult`。
3. 若服务需要自动回译但缓存无回译结果，只补做回译，不重做主翻译。

### 从入口到结果：手动单服务执行（词典/翻译）
1. `SingleTranslateAsync(service)` 先查当前输入历史。
2. 若是 `IDictionaryPlugin`：执行 `ExecuteDictAsync()`，失败即返回。
3. 若是 `ITranslatePlugin`：识别语种后执行 `ExecuteAsync()`，按配置追加 `ExecuteBackAsync()`。
4. `Settings.CopyAfterTranslationNotAutomatic` 为真时，手动执行完成立即复制结果。

### 复制与历史策略
- 自动复制：`Settings.CopyAfterTranslation` 支持第 N 个自动服务或最后一个自动服务。
- 历史持久化：`Settings.HistoryLimit > 0` 时使用 SQLite；否则仅使用内存 `_recentTexts` 缓存最近输入。

## 错误处理与通知策略

### 服务未配置（阻断性错误）
当替换翻译 / TTS / 生词本等核心服务未配置或全部禁用时，使用 `Helper.PromptConfigureService` 弹出 MessageBox（OK/Cancel）。弹窗底层统一走 `AppMessageBox`：优先挂到当前活动窗口，没有活动窗口时才通过主屏中心的临时透明 owner 显示：
- 用户点击 **确定** → 自动打开设置窗口并定位到对应配置页。
- 用户点击 **取消** → 仅关闭弹窗，不跳转。

具体映射：
| 功能 | 未配置服务 | 跳转页面 | 涉及 ViewModel 方法 |
|---|---|---|---|
| 替换翻译 | 替换翻译服务 | `TranslatePage` | `ReplaceTranslateAsync` |
| 朗读 | TTS | `TtsPage` | `PlayAudioAsync` / `SilentTtsHandlerAsync` |
| 保存生词本 | 生词本服务 | `VocabularyPage` | `SaveToVocabularyAsync` / `SaveToVocabularyWithNoteAsync` |

### 运行时失败
执行过程中抛出异常或识别失败时，使用当前窗口内的 **Snackbar** 提示：
- 替换翻译语言检测失败：`_snackbar.ShowWarning("LanguageDetectionFailed")`。
- 取词后未识别到文本（如截图翻译无结果）：`_snackbar.ShowWarning("NoTextRecognizedMessage")`。
- TTS 播放失败 / 取消：`_snackbar.ShowError("TtsFailed")` / `_snackbar.ShowInfo("TtsCancelled")`。
- 生词本保存失败：`_snackbar.ShowError("OperationFailed")` 或显示服务端返回的错误信息。

## 关键数据结构/配置
- `Service.Options`
  - `ExecMode`：自动/手动执行。
  - `AutoBackTranslation`：自动回译开关。
- `HistoryModel` / `HistoryData`
  - `RawData` 序列化所有服务结果（翻译、回译、词典）。
  - `EffectiveSourceLang` / `EffectiveTargetLang` 保存实际参与翻译的语言。
  - `ServiceDisplayName` 保存服务显示名快照。
- `TranslateResult` / `DictionaryResult`
  - 承载执行状态、耗时、文本与结构化词典结果。
- 输入区识别状态
   - `None`：当前不显示识别语种标签。
   - `Cache`：当前翻译结果来自缓存命中；若缓存里记录了 `EffectiveSourceLang`，识别状态仍可显示该语言。
   - `Detected`：显示最近一次主翻译实际使用的源语种，可能来自自动识别成功或识别失败后的回退语言。
   - “识别为”文字快捷入口：在主界面切换语言检测器，并复用跳过缓存的翻译路径立即刷新结果。
   - “识别为”后的识别结果：保留语种下拉，用于手动纠正本次翻译的源语种。
- 关键设置项
  - `AutoTranslate`、`AutoTranslateDelayMs`
  - `CopyAfterTranslation`、`CopyAfterTranslationNotAutomatic`
  - `HistoryLimit`
  - `TextSeparatorHandleType`、`TextSeparatorHandleScopes`
- 输入区显隐状态
   - `HideInput`：用户持久隐藏输入框偏好。
   - `HideInputWithLangSelectControl`：隐藏输入框时是否一并隐藏语言选择控件。
   - `IsInputActuallyHidden`：叠加临时输入翻译模式后的实际隐藏状态。
   - `IsInputBoxVisible` / `IsLanguageSelectControlVisible`：主窗口 XAML 直接绑定的实际可见状态。

## 关键文件
- `STranslate/ViewModels/MainWindowViewModel.cs`
- `STranslate/Helpers/LanguageDetector.cs`
- `STranslate/Core/SqlService.cs`
- `STranslate/Core/Utilities.cs`
- `STranslate/Services/TranslateService.cs`
- `STranslate.Plugin/ITranslatePlugin.cs`

## 常见改动任务
- 新增翻译结果后处理（如术语替换）：优先在 `ExecuteAsync` 返回后、历史入库前处理。
- 调整自动翻译触发体验：修改 `OnInputTextChanged` 与 `Settings.AutoTranslateDelayMs`。
- 调整输入框显隐体验：修改 `InputClear()`、输入区有效显隐属性和 `MainWindow.xaml` 绑定，避免直接改 `Settings.HideInput` 破坏用户偏好。
- 修改缓存命中规则：改 `HistoryModel.HasData()` 与 `PopulateResultsFromCacheAsync()`，避免只改 UI 层。
- 增加复制策略：改 `TranslateAsync()` 内 `CopyAfterTranslation` 分支，并同步枚举定义与设置页。
- 新增取词入口：为入口分配 `TextSeparatorHandleScope` 并复用 `HandleCapturedText()`，避免各入口文本清洗行为不一致。
