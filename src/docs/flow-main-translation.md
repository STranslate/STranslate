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
- `STranslate/Core/TranslationResultCoordinator.cs`
  - `BeginAutomaticOperation()` / `BeginOperation()`：创建翻译操作并分配递增标识。
  - 按界面结果对象维护当前有效操作，拒绝旧操作继续发布状态。
- `STranslate/Core/TranslationOperation.cs`
  - 保存本次执行的输入、源/目标语种和取消令牌快照。
  - 使用独立临时结果执行主翻译、回译和词典查询，并把有效更新发布到界面结果。
- `STranslate/Core/TextPreprocessing.cs`
  - `PrepareAsync()`：捕获规则快照，在后台按顺序替换，生成 `PreparedTranslationInput`。
  - `CompiledTextReplacement` 分段展开捕获组，在分配完整结果之前限制输出长度。
- `STranslate/Core/DirectTranslationRunner.cs`
  - 现有替换翻译和图片分块翻译共用的预处理、空结果检查、语言检测与插件调用入口。
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
2. `TranslateAsync()` 执行前先取消防抖队列，创建 `TranslationOperation` 并重置已启用服务的结果对象。
   - `TranslationOperation` 快照输入文本、源/目标语种和取消令牌，内部持有递增操作标识。
   - 插件只写入本次请求独占的临时结果；主翻译、回译和词典结果仅在当前操作仍有效时发布到界面。
   - 被后续请求替代的旧任务仍可完成取消和资源清理，但不得覆盖新结果、触发自动复制或写入历史。
   - 先登记操作所有权，再 `await operation.PrepareInputAsync()`；处理完成后检查取消、所有权和非空请求文本。
   - `operation.Text` 始终是原文；`RequestText` 是一次性准备好的发送文本，普通、流式和同批词典共用它。
3. 进入 `ExecuteTranslateAsync(checkCacheFirst)`：
   - 获取已启用且 `ExecMode == Automatic` 的服务。
   - 若启用历史缓存：用 `(InputText, SourceLang, TargetLang)` 查询 `SqlService`。
   - 按服务校验实际发送文本、规则指纹和主结果成功状态，通过后才展示缓存；不兼容服务重新翻译。
4. 对未命中服务以 `operation.RequestText` 调用 `LanguageDetector.GetLanguageAsync()` 获取最终 `source/target`。
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
1. `PopulateResultsFromCache()` 遍历目标服务；旧记录缺少元数据时按未预处理解释，并在更新整条历史语种前补齐逐服务快照。
2. 命中缓存时：
   - 翻译服务更新 `TransResult` / `TransBackResult`。
   - 词典服务更新 `DictionaryResult`。
3. 若服务主译有效、需要自动回译但缓存回译缺失或失败，只补做回译；文本和语种取自该主译快照。

### 从入口到结果：手动单服务执行（词典/翻译）
1. `TemporaryTranslate(service)` / 输出区重试先走服务级 `CanExecute`：不同服务可并发，同一服务已有手动任务时继续提示等待。
2. `SingleTranslateAsync(service)` 启动时快照当前输入、规则与源/目标语种；`SingleTransBackAsync(service)` 使用当前主译绑定的 `TranslationResultContext`，不重新处理输入或识别语言。两者用 `PluginID + ServiceID` 登记本次任务的取消令牌。
3. 若是 `IDictionaryPlugin`：用 `operation.LookupAsync()` 执行快照文本，失败即返回；词典手动执行仍保持现有历史语义，不额外落盘。
4. 若是 `ITranslatePlugin`：用快照文本和语种配置识别实际语种后执行 `operation.TranslateAsync()`，按配置追加 `operation.BackTranslateAsync()`。
5. 翻译服务成功后通过串行历史合并重新读取最新历史，只更新当前服务的 `HistoryData`，避免多个手动服务并发完成时互相覆盖。
6. `Settings.CopyAfterTranslationNotAutomatic` 为真时，手动执行完成立即复制结果；多个并发服务仍按完成顺序更新剪贴板。
7. ESC / 关闭窗口 / 清空输入 / 新翻译入口会通过 `CancelAllOperations()` 取消所有已登记的手动单服务任务。

### 翻译前替换的编辑与查看
1. 在“设置 → 翻译 → 翻译前替换”打开 `TextPreprocessingDialog`，显式传入所属窗口。
2. `TextPreprocessingEditViewModel` 使用独立草稿；新增、排序、启停和编辑不会影响正在执行的请求。保存会后台校验全部规则，成功后整体替换 `Settings.TextPreprocessing`，取消即丢弃草稿。
3. 本地预览防抖 180 ms，并取消已过期任务；显示最终请求、逐规则命中数和错误位置，不请求网络。
4. 主窗口出现匹配时显示“已替换 N 处”，旁边的请求详情按钮展示本次准备的发送文本；输入改变或新请求开始时清除旧提示。
5. 单规则限制 100 ms、整次处理限制 500 ms、中间输出上限 1,000,000 个 UTF-16 字符。配置损坏、规则错误、超时、输出超限和空结果都会阻止后续发送。

### 复制与历史策略
- 自动复制：`Settings.CopyAfterTranslation` 支持第 N 个自动服务或最后一个自动服务。
- 历史持久化：`Settings.HistoryLimit > 0` 时使用 SQLite；否则仅使用内存 `_recentTexts` 缓存最近输入。

## 并发隔离维护指南

### 类型职责

| 类型 | 负责 | 不负责 |
| --- | --- | --- |
| `MainWindowViewModel` | 创建操作、缓存和语言识别编排、复制与历史业务 | 生成操作标识、创建插件临时结果、处理流式属性转发 |
| `TranslationResultCoordinator` | 分配操作标识、维护结果通道所有权、拒绝旧操作发布 | 调用翻译插件、决定缓存或历史策略 |
| `TranslationOperation` | 保存不可变执行快照、调用插件、处理取消/异常、发布有效结果 | 读取实时 `InputText` / `Settings`、写历史或剪贴板 |
| 翻译/词典插件 | 将执行结果写入调用方提供的结果对象 | 复用或替换主窗口绑定的结果对象 |

`TranslationResultCoordinator` 和 `TranslationOperation` 都是主程序内部类型，不属于插件 SDK；`ITranslatePlugin`、`IDictionaryPlugin` 和结果类型的公共接口保持不变。

### 标准调用方式

自动翻译入口使用 `BeginAutomaticOperation()`，以便复制、历史和识别语言状态也受“最新自动请求”约束：

```csharp
var operation = _translationCoordinator.BeginAutomaticOperation(
    InputText,
    Settings.SourceLang,
    Settings.TargetLang,
    cancellationToken);

ResetAllServices(operation);
await operation.PrepareInputAsync();
operation.EnsureNonEmptyInput();
var result = await operation.TranslateAsync(plugin, source, target);

if (!result.IsSuccess || !operation.IsCurrent(plugin.TransResult))
    return;
```

手动单服务执行使用 `BeginOperation()`。创建后始终向下传递整个 `TranslationOperation`，不要重新拆成 `operationId + text + language + cancellationToken`：

- 主翻译：先 `operation.TryPrepare(plugin)`，再调用 `operation.TranslateAsync()`。
- 仅回译：从主结果获取 `TranslationResultContext`，用 `BeginBackOperation()` 创建操作，再 `TryPrepareBack()` 和 `BackTranslateAsync()`。
- 词典查询：先 `operation.TryPrepare(dictionaryPlugin)`，再调用 `operation.LookupAsync()`。
- 缓存结果发布：使用 `operation.TryPublish()`；词典集合使用 `operation.PublishDictionary()`。
- 语言识别回调：使用 `operation.PublishIdentifiedLanguage()`，避免旧检测任务更新新请求的识别状态。

### 新增或修改翻译入口 Checklist

1. 在读取 `InputText` 和语言设置时立即创建 `TranslationOperation`，先登记结果通道，再后台准备输入。展示/历史定位用 `Text`，检测/主翻译/词典用 `RequestText`。
2. 调用插件前准备对应结果通道；不要把插件共享的 `TransResult`、`TransBackResult` 或 `DictionaryResult` 直接作为插件执行结果传入。
3. 主翻译与回译都使用本次方法返回的临时结果；回译文本从本次主翻译返回值读取。
4. `TranslationOperation` 会在插件返回后重新检查取消令牌；调用方仍须检查执行结果和 `operation.IsCurrent(...)`，再执行复制、历史、最近文本等非界面副作用。
5. 自动请求的复制和历史入口还需检查 `operation.IsLatestAutomatic`；可见状态发布使用操作对象提供的发布方法。
6. 深层编排可以使用 `ThrowIfCancellationRequested()` 快速终止流程；自动翻译命令入口必须仅捕获自身令牌触发的 `OperationCanceledException`，把用户取消作为正常控制流，不能让取消异常越过 `AsyncRelayCommand` 边界。
7. 不使用 `AsyncLocal`、静态“当前请求”或实时 `InputText` 隐式传递上下文，避免调用链变得不可追踪。
8. 增加并发回归测试，至少覆盖旧请求在新请求完成后才进入流式回调、`catch` 或 `finally` 的情况。

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
- `STranslate/Core/TranslationResultCoordinator.cs`
- `STranslate/Core/TranslationOperation.cs`
- `STranslate/Helpers/LanguageDetector.cs`
- `STranslate/Core/SqlService.cs`
- `STranslate/Core/Utilities.cs`
- `STranslate/Services/TranslateService.cs`
- `STranslate.Plugin/ITranslatePlugin.cs`

## 常见改动任务
- 新增翻译结果后处理（如术语替换）：优先在 `operation.TranslateAsync()` 返回后、历史入库前处理。
- 调整自动翻译触发体验：修改 `OnInputTextChanged` 与 `Settings.AutoTranslateDelayMs`。
- 调整输入框显隐体验：修改 `InputClear()`、输入区有效显隐属性和 `MainWindow.xaml` 绑定，避免直接改 `Settings.HideInput` 破坏用户偏好。
- 修改缓存命中规则：改 `HistoryModel.HasData()` 与 `PopulateResultsFromCache()`，避免只改 UI 层。
- 增加复制策略：改 `TranslateAsync()` 内 `CopyAfterTranslation` 分支，并同步枚举定义与设置页。
- 新增取词入口：为入口分配 `TextSeparatorHandleScope` 并复用 `HandleCapturedText()`，避免各入口文本清洗行为不一致。
