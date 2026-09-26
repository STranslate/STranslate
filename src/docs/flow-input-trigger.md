# 输入触发与热键系统

## 模块职责
- 管理全局热键、软件内热键、低级键盘钩子、Ctrl+CC、鼠标划词与剪贴板监听。
- 将触发事件统一路由到 `MainWindowViewModel` 命令。
- 根据热键可用状态与全屏策略同步托盘图标状态。
- 统一模拟复制后的取词超时、取词失败回退与取词文本分隔符处理。

## 关键入口
- `STranslate/Core/HotkeySettings.cs`
  - `LazyInitialize()`：启动时应用 Ctrl+CC、增量翻译键、全局热键注册。
  - `HandleGlobalLogic()`：热键到命令的映射中心。
- `STranslate/Helpers/HotkeyMapper.cs`
  - `SetHotkey()`：NHotkey/ChefKeys 注册。
  - `StartGlobalKeyboardMonitoring()`：低级键盘钩子（WH_KEYBOARD_LL）。
  - `RegisterHoldKey()`：按住键增量翻译。
  - `IsReservedGlobalHotkey()`：阻止把系统复制热键注册为全局热键。
- `STranslate/Helpers/CtrlSameCHelper.cs`
  - 监听 Ctrl+C 双击（500ms 窗口）。
- `STranslate/Services/MouseHookService.cs`
  - 在专用消息线程通过 `WH_MOUSE_LL` 监听鼠标拖选与双击选词，Hook 回调只投递事件，不执行剪贴板或 UI 操作。
- `STranslate/Services/MouseSelectionService.cs`
  - 协调直接翻译、悬浮图标与增量翻译共享 Hook，并按优先级分发划词结果。
- `STranslate/Helpers/ClipboardMonitor.cs`
  - `AddClipboardFormatListener` 监听剪贴板变更。
- `STranslate/ViewModels/MainWindowViewModel.cs`
  - `ExecuteTranslate()` / `InputClear()` / `Show()`：完成文本入口收敛、窗口显示与统一置前。
- `STranslate/Core/Settings.cs`
  - `SelectedTextFetchTimeoutMs`、`TextSeparatorHandleType`、`TextSeparatorHandleScopes`、`CrosswordFetchFailedFallbackTarget`。
- `STranslate/Views/MainWindow.xaml`
  - `Window.InputBindings`：软件内热键（设置、历史、置顶、自动翻译等）。
  - 输入区显隐绑定：使用 `IsInputActuallyHidden` / `IsInputBoxVisible` / `IsLanguageSelectControlVisible`，避免输入翻译入口被持久隐藏设置阻断。

## 核心流程
### 从入口到结果：全局热键触发命令
1. `HotkeySettings.RegisterHotkeys()` 对每个全局热键调用 `HandleGlobalLogic(propertyName)`。
2. `HandleGlobalLogic()` 通过 `HotkeyMapper.SetHotkey()` 注册系统热键并绑定命令回调。
3. `ForegroundFullscreenMonitor` 监听前台窗口与窗口尺寸变化；启用全屏忽略时，进入全屏会注销普通全局热键，退出全屏会重新注册，避免按键仍被系统级热键占用。
4. 回调执行前仍经 `WithFullscreenCheck()` 兜底：
   - `DisableGlobalHotkeys == true` 时禁用。
   - `IgnoreHotkeysOnFullscreen == true` 且前台全屏时跳过。
5. 命令进入 `MainWindowViewModel`（例如截图翻译、图片翻译、静默 OCR、替换翻译、剪贴板监听切换）。
6. 需要显示窗口的命令统一走 `MainWindowViewModel.Show()` 或 `SingletonWindowOpener`；触发来源不改变窗口激活策略。

### 从入口到结果：输入翻译
1. 输入翻译全局热键、外部调用 `translate_input`、托盘双击输入翻译、划词失败回退到输入翻译都会进入 `MainWindowViewModel.InputClear()`。
2. `InputClear()` 取消当前任务、重置识别状态、清空输入、重置服务结果，并进入临时输入翻译模式。
3. 临时输入翻译模式只影响当前窗口会话：即使用户启用了 `Settings.HideInput`，主窗口也会显示输入框并聚焦，方便立即键入。
4. 临时显示不会写回 `Settings.HideInput`；关闭/取消窗口、直接文本翻译、用户手动显示/隐藏输入框后退出该模式。
5. `Settings.HideInputWithLangSelectControl` 仍作为普通显示偏好生效；输入翻译临时显示输入框时，语言选择控件也会同步显示。

### 从入口到结果：增量翻译（按住键）
1. `IncrementalTranslateKey` 变化触发 `ApplyIncrementalTranslate()`。
2. 注册 `HotkeyMapper.RegisterHoldKey(key, OnIncKeyPressed, OnIncKeyReleased)` 并开启低级键盘钩子。
3. 按下时 `OnIncKeyPressed()`：置顶窗口 + 向 `MouseSelectionService` 申请增量取词会话 + 缓存旧文本。若 `Settings.IncrementalClearInput`（默认 true）则先清空输入框，本次会话内选中文本仍累积追加；false 时保留旧逻辑不清空。
4. 松开时 `OnIncKeyReleased()`：释放增量取词会话，若文本有变化则执行翻译；常驻划词仍启用时底层 Hook 不会停止。

### 从入口到结果：Ctrl+CC、鼠标划词、剪贴板监听
- Ctrl+CC：`CtrlSameCHelper` 监听全局按键，500ms 内双击 `Ctrl+C` 触发 `CrosswordTranslateByCtrlSameCHandler()`。
- 鼠标划词：拖选和双击选词共用同一条完成事件链路。`IsMouseSelectionTranslationEnabled` 和 `IsMouseSelectionIconEnabled` 是独立开关，任意一个开启都会维持同一个 Hook。处理优先级为增量翻译、直接翻译、悬浮图标；两者同时开启时直接翻译，不显示图标。
- 剪贴板监听：`ClipboardMonitor` 收到 `WM_CLIPBOARDUPDATE` 后读取文本，触发 `OnClipboardTextChanged -> ExecuteTranslate()`。

### Acrobat 自定义文字光标兼容
- `MouseHookService.IsIBeamCursor()` 保留系统 `IDC_IBEAM` 句柄匹配；不匹配时，`AcrobatCursorCompatibility` 仅对前台进程 `Acrobat` / `AcroRd32` 检查已知文字光标位图指纹。
- 当前样本覆盖同一 Acrobat 文字光标的 37×37 与 56×56 版本。后者来自与主程序相同的 PerMonitorV2 环境下实际拖选；不能把不同 DPI 上下文采集的指纹视为相同。其他版本、主题和缩放仍需实测，不对任意自定义光标放宽限制。
- 进程名和光标结果缓存最多 2 秒，前台进程或光标改变时刷新；进程退出、读取失败及未知位图均返回不匹配。`GetIconInfo` 创建的位图在 `finally` 中释放。
- 仍使用原有拖选/双击阈值、取词超时与服务分发。不修改 Acrobat 首选项，也不采集或记录文档正文。
- 回归验证：开启鼠标划词，在可复制 PDF 中拖选/双击；确认 Acrobat 文字光标触发、其他拖动不误触发，并检查 Foxit/浏览器的标准光标路径。自动测试同时覆盖进程限制、DPI 样本、缓存失效和读取异常。

### 触发后的窗口置前
- 全局热键由 STranslate 接收并不代表 STranslate 已是前台应用；触发时浏览器、编辑器或 Explorer 通常仍持有前台窗口。
- `ExecuteTranslate()`、`InputClear()` 及其他显示入口最终统一调用 `Win32Helper.ActivateForegroundWindow()`，再执行 WPF `Activate()` / `Focus()`。
- 热键、托盘、鼠标划词、剪贴板监听和第二实例唤醒均处于默认 `Normal` 上下文，只调用 Win32 `SetForegroundWindow`；普通调用失败时不会升级为 `AttachThreadInput`，避免打断 Explorer 文件重命名等文本编辑操作。
- `Ctrl+C+C` 在 UI 调度回调内压入 `ForceForeground` 上下文；翻译成功和取词失败回退产生的主窗口显示都会在需要时通过 `AttachThreadInput` 强制置前，确保复制动作完成后结果窗口可见。
- HTTP `ExternalCallService` 会为完整 action 压入 `ForceForeground` 上下文；相同的显示入口会自动改用线程挂接强制置前，无需在 ViewModel、热键回调或窗口打开器之间传递激活参数。
- 主窗口失焦时按 `HideWhenDeactivated` 自动隐藏；置顶窗口不受此逻辑影响。

### 从入口到结果：取词超时、后处理与失败回退
1. 需要模拟复制读取选中文本的入口会调用 `ClipboardHelper.GetSelectedTextAsync(Settings.SelectedTextFetchTimeoutMs)`。
2. `SelectedTextFetchTimeoutMs` 在 `Settings` 中限制为 50~5000 毫秒；鼠标划词监听通过委托实时读取当前配置。
3. 取到文本后统一进入 `MainWindowViewModel.HandleCapturedText(text, scope)`：
   - 先执行 `LineBreakHandleType` 换行处理。
   - 再按 `TextSeparatorHandleType` 与 `TextSeparatorHandleScopes` 对 `_` / `-` 做可选分隔符处理。
4. 当前取词作用域包括：
   - `MouseSelection`：鼠标划词直接翻译与悬浮图标取词。
   - `Crossword`：划词翻译与 `Ctrl+C+C`。
   - `Incremental`：按住键增量翻译。
   - `ClipboardMonitor`：剪贴板监听翻译。
   - `ScreenshotTranslate`：截图翻译 OCR 结果。
   - `SilentOcr`：静默 OCR 写入剪贴板结果。
5. 划词翻译取词失败时按 `CrosswordFetchFailedFallbackTarget` 分支：
   - 划词翻译调用 `GetTextAsync(showFailureFeedback: false)`，避免共用取词方法提前显示主窗口；失败反馈统一由 `HandleCrosswordFetchFailed()` 处理。
   - `InputTranslate`：清空输入并显示主窗口，回退到输入翻译；输入框会临时显示，不改写隐藏输入框设置。
   - `ShowWindow`：仅显示主窗口，保留当前输入和结果。
   - `NotifyOnly`：仅发送托盘通知，不显示或激活主窗口，保留当前输入和结果。

### 触发失败的通知策略
- 启动或退出全屏后恢复全局热键时，若遇到系统占用会进行有限退避重试；瞬时冲突保持静默，重试成功后自动恢复，只有全部重试失败才标记冲突并提示。用户手动修改为冲突热键时仍立即提示；修改快捷键、禁用全局热键、进入全屏暂停状态或退出应用时会取消旧重试。
- 服务未配置（如截图翻译的 OCR 服务、替换翻译服务、TTS 等）：弹出 MessageBox（OK/Cancel），点击确定自动打开设置窗口并定位到对应配置页。业务入口收敛到 `Helper.PromptConfigureService`，弹窗显示收敛到 `AppMessageBox`，活动窗口优先、透明 owner 兜底。
- 运行时失败（如 OCR 识别异常、语言检测失败）：在当前窗口内通过 **Snackbar** 提示；静默类操作（静默 OCR、截图翻译异常）会先 `Show()` 主窗口再弹 Snackbar，确保用户可见。
- 剪贴板监听启停、划词取词失败回退选择“仅通知”时：使用系统 **Toast** 托盘通知，无需显示主窗口。

### 软件内热键
- 主窗口、OCR 窗口、图片翻译窗口通过 `InputBindings` 绑定 `HotkeySettings.*Hotkey.Key`。
- 软件内热键不经过系统级注册，焦点窗口内生效。

### 全局热键保留规则
- `Ctrl + C` 是系统复制和划词取词保留热键。
- 全局热键设置对话框会用 `HotkeyMapper.TryGetReservedGlobalHotkeyMessageKey()` 给出提示并禁用保存。
- 注册阶段仍会在 `HotkeyMapper.SetHotkey()` 二次拦截，避免配置文件手工写入导致误注册。

### 托盘状态联动
- `HotkeySettings.UpdateTrayIconWithPriority()` 优先级：
  1. `DisableGlobalHotkeys` -> `NoHotkey` 图标
  2. `IgnoreHotkeysOnFullscreen` -> `IgnoreOnFullScreen` 图标
  3. 默认 -> 正常图标

## 关键数据结构/配置
- `HotkeySettings.RegisteredHotkeys`：统一热键定义清单与适用窗口类型。
- `HotkeyType`：`Global/MainWindow/SettingsWindow/OcrWindow/ImageTransWindow`。
- `GlobalHotkey.IsConflict`：注册冲突状态。
- 触发策略配置：
  - `DisableGlobalHotkeys`
  - `IgnoreHotkeysOnFullscreen`
  - `CrosswordTranslateByCtrlSameC`
  - `IncrementalTranslateKey`
  - `SelectedTextFetchTimeoutMs`
  - `TextSeparatorHandleType`
  - `TextSeparatorHandleScopes`
  - `CrosswordFetchFailedFallbackTarget`
  - `IsMouseSelectionTranslationEnabled`
  - `IsMouseSelectionIconEnabled`
  - `HideInput`、`HideInputWithLangSelectControl`
- 输入区有效显隐状态：
  - `IsInputActuallyHidden`：持久隐藏设置叠加输入翻译临时显示后的实际隐藏状态。
  - `IsInputBoxVisible`：主输入框实际可见状态。
  - `IsLanguageSelectControlVisible`：语言选择控件实际可见状态。

## 关键文件
- `STranslate/Core/HotkeySettings.cs`
- `STranslate/Helpers/HotkeyMapper.cs`
- `STranslate/Helpers/ForegroundFullscreenMonitor.cs`
- `STranslate/Helpers/CtrlSameCHelper.cs`
- `STranslate/Services/MouseHookService.cs`
- `STranslate/Services/MouseSelectionService.cs`
- `STranslate/Helpers/ClipboardMonitor.cs`
- `STranslate/ViewModels/MainWindowViewModel.cs`
- `STranslate/Views/MainWindow.xaml`

## 常见改动任务
- 新增全局热键：在 `HotkeySettings` 增加字段、`RegisteredHotkeys` 声明、`HandleGlobalLogic` 映射。
- 新增软件内热键：在对应窗口 XAML `InputBindings` 绑定 `HotkeySettings` 键值。
- 解决热键冲突：优先查看 `GlobalHotkey.IsConflict` 与 `HotkeyMapper.SetHotkey` 异常日志。
- 调整全屏忽略策略：统一改 `HotkeyMapper.ShouldSkipHotkey()` 与 `HotkeySettings.WithFullscreenCheck()`。
- 新增模拟复制类取词入口：必须接入 `SelectedTextFetchTimeoutMs` 并明确 `TextSeparatorHandleScope`，避免新增入口与现有入口处理不一致。
- 新增输入翻译入口：优先复用 `InputClear()`，确保隐藏输入框时仍会临时显示输入区并正确聚焦。
- 调整触发后的窗口激活：保持所有显示入口调用 `ActivateForegroundWindow()`；普通/强制策略由 `WindowActivationContext` 统一选择，在需要强制置前的触发源边界压入作用域，不在具体窗口显示逻辑中增加来源分支。
