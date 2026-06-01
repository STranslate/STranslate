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
  - `SetHotkey()`：将常规全局热键注册到自研输入引擎。
  - `RegisterHoldKey()`：注册按住键增量翻译。
  - `RegisterSequence()`：注册 `Ctrl+C+C` 等序列触发。
  - `RegisterModifierDoubleTap()`：注册修饰键双击触发。
  - `IsReservedGlobalHotkey()`：阻止把系统复制热键注册为全局热键。
- `STranslate/Helpers/GlobalInputEngine.cs`
  - 使用 `WH_KEYBOARD_LL` 统一处理常规全局热键、按住键、序列键与修饰键双击。
- `STranslate/Helpers/GlobalTriggerGuard.cs`
  - 统一处理全局禁用、全屏忽略、排除应用进程等过滤策略。
- `STranslate/Helpers/MouseKeyHelper.cs`
  - 鼠标拖拽结束后读取选中文本并触发事件。
- `STranslate/Helpers/ClipboardMonitor.cs`
  - `AddClipboardFormatListener` 监听剪贴板变更。
- `STranslate/Core/Settings.cs`
  - `SelectedTextFetchTimeoutMs`、`TextSeparatorHandleType`、`TextSeparatorHandleScopes`、`CrosswordFetchFailedFallbackTarget`。
- `STranslate/Views/MainWindow.xaml`
  - `Window.InputBindings`：软件内热键（设置、历史、置顶、自动翻译等）。
  - 输入区显隐绑定：使用 `IsInputActuallyHidden` / `IsInputBoxVisible` / `IsLanguageSelectControlVisible`，避免输入翻译入口被持久隐藏设置阻断。

## 核心流程
### 从入口到结果：全局热键触发命令
1. `HotkeySettings.RegisterHotkeys()` 对每个全局热键调用 `HandleGlobalLogic(propertyName)`。
2. `HandleGlobalLogic()` 通过 `HotkeyMapper.SetHotkey()` 注册到 `GlobalInputEngine` 并绑定命令回调。
3. 低级钩子命中规则后先经过 `GlobalTriggerGuard`：
   - `DisableGlobalHotkeys == true` 时禁用。
   - `IgnoreHotkeysOnFullscreen == true` 且前台全屏时跳过。
   - 前台进程在 `ExcludedGlobalTriggerProcesses` 中时跳过。
4. guard 跳过时直接放行原始输入；允许触发时按规则的 `SuppressionMode` 决定是否吞键。
5. 命令进入 `MainWindowViewModel`（例如截图翻译、图片翻译、静默 OCR、替换翻译、剪贴板监听切换）。

### 从入口到结果：输入翻译
1. 输入翻译全局热键、外部调用 `translate_input`、托盘双击输入翻译、划词失败回退到输入翻译都会进入 `MainWindowViewModel.InputClear()`。
2. `InputClear()` 取消当前任务、重置识别状态、清空输入、重置服务结果，并进入临时输入翻译模式。
3. 临时输入翻译模式只影响当前窗口会话：即使用户启用了 `Settings.HideInput`，主窗口也会显示输入框并聚焦，方便立即键入。
4. 临时显示不会写回 `Settings.HideInput`；关闭/取消窗口、直接文本翻译、用户手动显示/隐藏输入框后退出该模式。
5. `Settings.HideInputWithLangSelectControl` 仍作为普通显示偏好生效；输入翻译临时显示输入框时，语言选择控件也会同步显示。

### 从入口到结果：增量翻译（按住键）
1. `IncrementalTranslateKey` 变化触发 `ApplyIncrementalTranslate()`。
2. 注册 `HotkeyMapper.RegisterHoldKey(id, key, OnIncKeyPressed, OnIncKeyReleased)`。
3. 按下时 `OnIncKeyPressed()`：置顶窗口 + 开启鼠标划词监听 + 缓存旧文本。
4. 松开时 `OnIncKeyReleased()`：关闭划词监听，若文本有变化则执行翻译。

### 从入口到结果：Ctrl+CC、鼠标划词、剪贴板监听
- Ctrl+CC：`GlobalInputEngine` 监听 `Ctrl+C, Ctrl+C` 序列，500ms 内完成时触发 `CrosswordTranslateByCtrlSameCHandler()`，不吞掉复制按键。
- 鼠标划词：`MouseKeyHelper` 在拖拽完成后读选中文本，触发 `ExecuteTranslate()`。
- 剪贴板监听：`ClipboardMonitor` 收到 `WM_CLIPBOARDUPDATE` 后读取文本，触发 `OnClipboardTextChanged -> ExecuteTranslate()`。

### 从入口到结果：取词超时、后处理与失败回退
1. 需要模拟复制读取选中文本的入口会调用 `ClipboardHelper.GetSelectedTextAsync(Settings.SelectedTextFetchTimeoutMs)`。
2. `SelectedTextFetchTimeoutMs` 在 `Settings` 中限制为 50~5000 毫秒；鼠标划词监听通过委托实时读取当前配置。
3. 取到文本后统一进入 `MainWindowViewModel.HandleCapturedText(text, scope)`：
   - 先执行 `LineBreakHandleType` 换行处理。
   - 再按 `TextSeparatorHandleType` 与 `TextSeparatorHandleScopes` 对 `_` / `-` 做可选分隔符处理。
4. 当前取词作用域包括：
   - `MouseHook`：监听鼠标划词。
   - `Crossword`：划词翻译与 `Ctrl+C+C`。
   - `Incremental`：按住键增量翻译。
   - `ClipboardMonitor`：剪贴板监听翻译。
   - `ScreenshotTranslate`：截图翻译 OCR 结果。
   - `SilentOcr`：静默 OCR 写入剪贴板结果。
5. 划词翻译取词失败时按 `CrosswordFetchFailedFallbackTarget` 分支：
   - `InputTranslate`：清空输入并显示主窗口，回退到输入翻译；输入框会临时显示，不改写隐藏输入框设置。
   - `ShowWindow`：仅显示主窗口，保留当前输入和结果。

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
- `GlobalHotkey.IsConflict`：应用内部规则冲突/不可用状态，不再表示系统注册失败。
- `GlobalTriggerBinding`：自研全局触发规则，支持 `Chord/Sequence/ModifierDoubleTap/Hold`。
- `SuppressionMode`：命中规则后的吞键策略。
- 触发策略配置：
  - `DisableGlobalHotkeys`
  - `IgnoreHotkeysOnFullscreen`
  - `ExcludedGlobalTriggerProcesses`
  - `CrosswordTranslateByCtrlSameC`
  - `IncrementalTranslateKey`
  - `ModifierDoubleTapKey`、`ModifierDoubleTapAction`
  - `SelectedTextFetchTimeoutMs`
  - `TextSeparatorHandleType`
  - `TextSeparatorHandleScopes`
  - `CrosswordFetchFailedFallbackTarget`
  - `HideInput`、`HideInputWithLangSelectControl`
- 输入区有效显隐状态：
  - `IsInputActuallyHidden`：持久隐藏设置叠加输入翻译临时显示后的实际隐藏状态。
  - `IsInputBoxVisible`：主输入框实际可见状态。
  - `IsLanguageSelectControlVisible`：语言选择控件实际可见状态。

## 关键文件
- `STranslate/Core/HotkeySettings.cs`
- `STranslate/Core/GlobalTrigger.cs`
- `STranslate/Helpers/GlobalInputEngine.cs`
- `STranslate/Helpers/GlobalTriggerGuard.cs`
- `STranslate/Helpers/HotkeyMapper.cs`
- `STranslate/Helpers/MouseKeyHelper.cs`
- `STranslate/Helpers/ClipboardMonitor.cs`
- `STranslate/ViewModels/MainWindowViewModel.cs`
- `STranslate/Views/MainWindow.xaml`

## 常见改动任务
- 新增全局热键：在 `HotkeySettings` 增加字段、`RegisteredHotkeys` 声明、`HandleGlobalLogic` 映射。
- 新增软件内热键：在对应窗口 XAML `InputBindings` 绑定 `HotkeySettings` 键值。
- 解决热键冲突：优先查看 `GlobalHotkey.IsConflict` 与 `GlobalInputEngine.HasConflict()`。
- 调整全局跳过策略：统一改 `GlobalTriggerGuard.ShouldSkipGlobalTrigger()`。
- 新增模拟复制类取词入口：必须接入 `SelectedTextFetchTimeoutMs` 并明确 `TextSeparatorHandleScope`，避免新增入口与现有入口处理不一致。
- 新增输入翻译入口：优先复用 `InputClear()`，确保隐藏输入框时仍会临时显示输入区并正确聚焦。
