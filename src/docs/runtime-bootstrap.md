# 运行时启动与窗口生命周期

## 模块职责
- 定义应用从 `Main()` 到主窗口可用的完整启动链路。
- 管理单实例行为、依赖注入容器初始化、运行时异常处理与退出释放。
- 管理主窗口与设置窗口的基础生命周期行为（显示、隐藏、导航、释放）。

## 关键入口
- `STranslate/App.xaml.cs`
  - `Main()`：单实例判定、Velopack 回调、管理员模式启动判定。
  - `App()` 构造函数：读取三类设置并注册 DI。
  - `OnStartup()`：插件/服务/数据库初始化，必要时先显示首次向导，再创建主窗口并执行启动后延迟初始化。
  - `Register*Exception()`、`RegisterExitEvents()`、`Dispose()`：异常与退出收敛。
- `STranslate/Core/ISingleInstanceApp.cs`
  - `SingleInstance<TApplication>.InitializeAsFirstInstance()`：Mutex + NamedPipe 单实例通知。
- `STranslate/Views/MainWindow.xaml.cs`
  - `OnLoaded()`、`OnContentRendered()`、`OnDeactivated()`、`Dispose()`。
- `STranslate/Views/SettingsWindow.xaml.cs`
  - `Navigate()`：设置页导航入口与导航状态同步。
- `STranslate/Helpers/Win32Helper.cs`
  - `ActivateForegroundWindow()`：统一的窗口置前入口，根据 `WindowActivationContext` 选择普通或强制激活。
  - `SetForegroundWindow()`：只调用 Win32 `SetForegroundWindow`，遵循系统前台权限规则，不自动升级为线程挂接。
  - `ForceSetForegroundWindow()`：临时通过 `AttachThreadInput` 挂接前台线程后强制置前，仅供强制激活上下文使用。
- `STranslate/Helpers/WindowActivationContext.cs`
  - 使用 `AsyncLocal` 保存当前激活模式；默认普通，HTTP 外部调用和 `Ctrl+C+C` UI 回调期间切换为强制模式。
- `STranslate/Helpers/SingletonWindowOpener.cs`
  - `Open()` / `OpenAsync()` / `OpenPreparedAsync()`：新建或复用单实例窗口，并统一恢复、显示、置前和聚焦。
- `STranslate/Views/WelcomeSetupWindow.xaml.cs`
  - 首次欢迎/快速配置向导：仅当启动前 `Settings.json`、`HotkeySettings.json`、`ServiceSettings.json` 三个配置文件都不存在时自动打开；也可从关于页手动打开。窗口使用小尺寸纵向分页，第 1 页提供紧凑欢迎说明和语言选择。
- `STranslate/Core/AppMessageBox.cs`
  - 应用级 MessageBox 入口：优先使用当前活动窗口作为 owner，缺省时用主屏中心的临时透明 owner 承载 iNKORE 弹窗。
- `STranslate/Helpers/UACHelper.cs`
  - `Run()`：通过 Host 进程按普通/提权/计划任务模式拉起应用。
- `STranslate.Host/src/commands/start.rs`
  - `start` 命令：延迟启动、等待旧进程退出、必要时清理旧进程。

## 核心流程
### 从入口到结果：应用启动到主窗口可用
1. `App.Main()` 先调用 `SingleInstance<App>.InitializeAsFirstInstance()`。
2. 若是第二实例：通过命名管道通知首实例触发 `OnSecondAppStarted()` 并退出；首实例执行 `MainWindowViewModel.Show()`。
3. 首实例继续执行 Velopack `OnAfterUpdateFastCallback`，把临时配置目录回迁到便携数据目录。
4. 执行 `NeedAdmin()`：若启动模式要求提权，先清理单实例资源，再交给 `UACHelper` 重启进程后当前实例返回。
   - `UACHelper.Run(mode, waitPid: Environment.ProcessId, waitTimeoutSec: 6)` 会把当前进程 ID 传给 Host。
   - Host 启动前等待旧进程退出；超时后尝试 `taskkill /PID <pid> /T /F`，并输出 `handover ...` 报告。
5. 创建 `App` 实例：从 `AppStorage<T>` 读取 `Settings`、`HotkeySettings`、`ServiceSettings`。
6. 在 `App()` 内配置 DI：注册核心服务（插件/服务管理、翻译链、HTTP、窗口 VM、更新、外部调用、数据库等）。
7. `OnStartup()` 中顺序初始化：
   - `PluginManager.LoadPlugins()`
   - `ServiceManager.LoadServices()`
   - `SqlService.InitializeDB()`
   - 注册全局异常处理
8. 若 `App` 构造阶段记录到三类配置文件启动前都不存在，则在主窗口创建前打开欢迎向导；打开前只初始化语言资源，保证向导首页可切换语言。
9. 向导关闭、跳过或完成时调用现有 `Save()` 生成配置文件，后续启动按文件已存在不再自动弹出。
10. 向导结束后创建主窗口与主 VM；主窗口 `Loaded` 时执行延迟初始化：`Settings.LazyInitialize(initializeLanguage: false)`、`HotkeySettings.LazyInitialize()`、托盘提示、WebDav 后置备份上传，以及自动检查更新服务启动。
11. 非首次向导路径仍按原流程创建主窗口，并在 `Loaded` 时执行完整 `Settings.LazyInitialize()`。

### 从入口到结果：退出与资源回收
1. 应用使用 `ShutdownMode.OnExplicitShutdown`，关闭最后一个窗口不会终止托盘常驻进程。
2. 主动退出统一调用 `App.RequestShutdown(AppShutdownReason)`；托盘菜单、托盘双击、快捷键、更新、备份恢复、便携模式切换和插件变更分别记录退出原因。
3. `ProcessExit`、`Application.Exit`、`SessionEnding` 任一事件触发时统一进入 `Dispose()`；`Application.Exit` 会同时记录已知退出原因，无法归因的退出记录为 `ExternalOrUnknown`。
4. 在主线程释放资源：通知图标卸载、主窗口 VM 释放、主窗口释放、`PluginManager.Dispose()` 清理临时解压目录。
5. 需要重启生效的插件目录清理并不在这里直接删除，而由下次启动时的插件扫描阶段处理（`NeedDelete`/`NeedUpgrade` 标记）。

### 从入口到结果：管理员/后台启动交接
1. 主进程判断需要以其他启动模式重启时，调用 `SingleInstance<App>.Cleanup()` 释放 Mutex 和命名管道。
2. `UACHelper.Run()` 组装 Host `start` 命令参数：
   - `--mode direct/elevated/task`
   - `--delay <seconds>`
   - `--wait-pid <pid>`
   - `--wait-timeout <seconds>`
3. Host 先等待旧 PID 退出，避免新实例注册单实例、热键或托盘资源时撞上旧进程残留。
4. 等待超时才尝试清理旧进程；启动结果和清理结果会写入标准输出/错误，方便排查后台拉起失败。

### 从入口到结果：窗口显示与前台激活
1. 主窗口显示统一进入 `MainWindowViewModel.Show()`；设置、历史、OCR、图片翻译及手动欢迎向导等单实例窗口统一经 `SingletonWindowOpener` 新建或复用；启动期欢迎向导与临时 MessageBox owner 直接显示后也使用同一前台帮助方法。
2. `SingletonWindowOpener` 先恢复最小化窗口，必要时设置主题并调用 `Show()`；主窗口在普通隐藏唤出前准备动画遮蔽，再恢复可见性、布局约束与位置，首帧就绪后播放。
3. 两条路径都统一调用 `Win32Helper.ActivateForegroundWindow()`；主窗口动画播放完并解除遮蔽后才置前和聚焦，避免临时失焦导致自动隐藏。动画回调保留调用时的 `WindowActivationContext` 策略；若期间用户切换到了其他窗口，则不抢回焦点。没有动画时立即执行。
4. 默认上下文使用普通 `SetForegroundWindow()`，适用于热键、托盘、鼠标划词、剪贴板监听和第二实例唤醒；普通调用失败时不会升级为线程挂接，避免强制抢夺其他应用的编辑焦点。
5. `Ctrl+C+C` 的 UI 调度回调和 `ExternalCallService` 的 HTTP action 会在各自触发源边界压入 `ForceForeground` 上下文；作用域内产生的窗口显示都改用 `ForceSetForegroundWindow()`。
6. 强制策略只在前台线程与当前 UI 线程不同时尝试 `AttachThreadInput`，并在 `finally` 中解除成功建立的挂接；同时保留最小化恢复和 `BringWindowToTop` 兜底。
7. Win32 置前后继续调用 WPF `Activate()`；单实例窗口还会调用 `Focus()`，保证新建窗口和已存在窗口使用相同的前台语义。
8. 主窗口 `OnDeactivated()` 按 `HideWhenDeactivated` 决定是否自动隐藏；置顶窗口不受此逻辑影响。

### 窗口生命周期要点
- `MainWindow`：
  - 首次显示与普通隐藏唤出使用 `WindowShowAnimation` 直接移动真实主窗口：从下方 18 DIP 开始，180ms 内缓出至目标位置，不再越过目标或回弹。前 140ms 通过原生 HWND 整窗 alpha 从透明渐显，不创建预览窗口，不修改 WPF Opacity 或 AllowsTransparency。淡入期间临时保留 WS_EX_LAYERED，完成或取消时撤销；已有分层窗口跳过淡入，避免干扰其逐像素合成。动画期间暂时解除 Top 绑定，防止中间位置写入设置；完成或取消时恢复绑定并读取最新目标位置，兼容译文高度变化引起的位置调整。已有可见窗口再次唤起不重播，隐藏仍即时完成。
  - 首次显示在 `SourceInitialized` 提前遮蔽，`OnContentRendered()` 完成启动布局后，将真实窗口移至动画起点并解除遮蔽；启动隐藏同样先遮蔽再隐藏，启动策略仅执行一次。输入、拖动、重复唤出、隐藏与退出均清理动画并恢复位置。普通唤出期间暂停贴顶吸附检测，贴顶展开沿用原有动画。遵循系统客户端区域动画开关，不新增配置项。
  - 唤出动画在解除遮蔽前临时置顶且不抢焦点，临时置顶不写回用户开关；完成时先恢复目标位置并激活，再恢复原置顶绑定（读取最新值），避免动画及交接过程被其他窗口遮挡。取消或隐藏同样恢复置顶状态。动画期间忽略临时失焦，结束后恢复正常失焦隐藏。设置窗口位置会同步派发消息，可能重入隐藏/关闭流程；准备过程中每次定位后检查动画是否已释放，取消后不再启动渲染订阅或执行完成回调，也不重新显示窗口。
  - 常规设置 `AutoHideAtTopEdge`（默认开启）启用贴顶收起：主窗口靠近当前显示器工作区顶边 8 DIP 内吸附，鼠标离开后等待 `TopEdgeAutoHideDelayMs`（默认 600ms，可在展开的设置项中调整为 100–10000ms，修改后即时生效）再隐藏主窗口并显示 6 DIP 的主题感应条，悬停展开后恢复主窗口内部输入框焦点，不主动激活其他应用。感应条使用主题强调色、动态描边、圆角和轻微阴影，跟随明暗主题切换。拖离顶边或关闭开关退出贴顶，并恢复原来的置顶绑定；菜单、模态窗口、拖动及键盘输入期间延缓收起。
  - `TopEdgeAutoHideController` 独立管理感应条和临时置顶，不把主窗口移出屏幕，避免影响上方显示器和持久化位置。感应条首次创建时显式继承主窗口主题，避免错过启动初始化后主题画刷为空、整条透明；显式清空窗口 Style，避免全局现代窗口模板的标题栏把内容推到 6 DIP 窗口之外而被裁掉。贴顶期间优先于失焦隐藏，显式隐藏仍隐藏感应条；统一 `Show()` 先展开并保留贴顶位置。
  - 收起和展开使用 `TopEdgeSlideAnimation` 的 180ms 缓出滑动：无激活的临时窗口通过 DWM 实时预览统一承载内容与背景，裁剪并滑动整幅画面；主窗口仅临时 Cloak，位置、布局、视觉树及原生区域不变，收起完成后才隐藏。展开前先 Cloak，再 `Show()`，等首帧布局和渲染完成后从感应条展开，避免背景先出现。反向操作从当前帧接续；关闭开关、主动隐藏、拖动、输入或点击过渡层会取消动画并清理预览、撤销 Cloak。遵循系统的客户端区域动画开关，无法创建系统预览时直接收展。
  - `OnLoaded()` 按 `HideOnStartup` 计算窗口位置，并在现代窗口模板初始化后挂接窗口过程钩子、移除 `WS_MAXIMIZEBOX`；窗口过程过滤 `WM_STYLECHANGING`，防止框架重新加入最大化权限，并拦截 `SC_MAXIMIZE`，禁止标题栏双击、顶部拖拽与系统快捷键最大化，同时保留横向缩放。
  - `OnContentRendered()` 决定首次显示或隐藏。
  - `OnDeactivated()` 可按 `HideWhenDeactivated` 自动隐藏，避免 Alt-Tab 残留。
  - 前台激活与失焦隐藏是两条独立链路；`HideWhenDeactivated` 决定是否隐藏，置顶窗口不会触发隐藏。
  - 除 `Ctrl+C+C` 和 HTTP 外部调用外的入口只使用普通 `SetForegroundWindow()`，即使受 Windows 前台锁限制而置前失败，也不会挂接前台线程强抢焦点；这两个强制显示入口通过独立上下文保证其派生窗口能够强制置前。
- `SettingsWindow`：
  - `Navigate(tag)` 根据页面类型从 DI 取页实例并注入到 `RootFrame.Content`；页面及页面 VM 为 `Scoped` 注册，统一从窗口独有的 `IServiceScope` 解析。
  - 页面切换、`OnClosing` 模板拆除和 `OnClosed` 视觉树释放统一通过导航保护执行，确保 WPF 清空 `PasswordBox` 时不会把框架行为误判为用户清空密码；保护状态支持嵌套，并在异常路径恢复。
  - `Ctrl+F` 由 `OnKeyDown` 路由到当前页面的搜索框。
  - 窗口构造时 `Ioc.Default.CreateScope()` 建立独立 scope；`OnClosed` 先解绑导航事件、清空 `RootFrame.Content`，再经 `ModernWindowLifecycle.Release(this, _serviceScope.Dispose)` 拆除视觉树并释放 scope，触发已解析页面 VM（如 `HistoryViewModel`）的 `Dispose()` 取消全局订阅，避免 root scope 长期跟踪导致泄漏。
  - `OnClosing` 通过 `ModernWindowLifecycle.DetachModernWindowStyle()` 解除 iNKORE titlebar 的属性描述符监听；`OnClosed` 清空 `DataContext` 和内容树。
- `WelcomeSetupWindow`：
  - 自动显示只由启动前配置文件是否存在决定，不新增单独的完成标记。
  - 首次自动显示会阻塞主窗口创建，避免新用户先看到主窗口再看到向导；关于页手动打开向导不影响当前主窗口显隐。
  - 向导为 5 页纵向流程：欢迎和语言选择、翻译服务、OCR 服务、图片翻译/替换翻译专用服务、关键快捷键。
  - 上一页/下一页仅切换中间内容区；内容使用 XamlFlair 的轻量入场动画，顶部品牌区、底部按钮和进度文本保持静止。
  - 第 1 页不使用整块强调色背景，只保留紧凑品牌块和语言选择，避免大面积空白。
  - 向导复用现有服务、插件配置 UI 和 `HotkeyControl`；快捷键页只显示打开窗口、输入翻译、划词翻译、截图翻译、OCR、图片翻译、替换翻译。
  - 首次自动显示时主窗口尚未创建，快捷键编辑 `ContentDialog` 必须显式绑定当前向导窗口作为 owner，避免无参 `ShowAsync()` 等待后续应用激活才显示。
  - 关于页中的“向导”按钮可手动重新打开。
- `AppMessageBox`：
  - 项目内 MessageBox 必须统一走 `AppMessageBox.Show()`，不要直接调用 iNKORE `MessageBox.Show()`。
  - 如果存在活动且可见的应用窗口，优先用该窗口作为 owner，保持 UI 点击触发弹窗的窗口上下文。
  - 如果没有活动窗口，才创建一个 1x1、透明、置顶、不进任务栏的临时 owner，位置固定在主显示器正中心。
  - 弹窗显示前会切回 UI Dispatcher；临时 owner 会被激活并在弹窗关闭后立即清理。
  - 该策略兼顾窗口内点击弹窗和后台/热键触发弹窗，避免主窗口隐藏、设置窗口在后台或没有可见应用窗口时，模态弹窗创建成功但不可见。

## 关键数据结构/配置
- `Settings`：主行为配置（窗口、主题、热键策略、网络、OCR/图像翻译参数等）。
- `AppShutdownReason`：统一标识主动退出、重启以及系统会话结束等退出来源。
- `StartMode`：普通启动、提权启动、计划任务启动。
- `HotkeySettings`：全局热键、软件内热键、增量翻译键、Ctrl+CC 配置。
- `ServiceSettings`：服务实例列表与特殊服务 ID（替换翻译、图片翻译）。
- `WindowActivationMode`：`Normal` / `ForceForeground` 两种窗口激活策略。
- `WindowActivationContext`：基于 `AsyncLocal` 的激活模式作用域，支持嵌套、异步传播和退出恢复。
- `DataLocation`：便携/漫游目录选择、日志/缓存/配置路径、`InfoFilePath` 与 `BackupFilePath`。

## 关键文件
- `STranslate/App.xaml`
- `STranslate/App.xaml.cs`
- `STranslate/Core/AppShutdownReason.cs`
- `STranslate/Core/ISingleInstanceApp.cs`
- `STranslate/Views/MainWindow.xaml.cs`
- `STranslate/Views/SettingsWindow.xaml.cs`
- `STranslate/Helpers/Win32Helper.cs`
- `STranslate/Helpers/WindowActivationContext.cs`
- `STranslate/Helpers/SingletonWindowOpener.cs`
- `STranslate/Helpers/ModernWindowLifecycle.cs`
- `STranslate/Core/AppMessageBox.cs`
- `STranslate/Core/DataLocation.cs`
- `STranslate/Helpers/UACHelper.cs`
- `STranslate.Host/src/commands/start.rs`

## 常见改动任务
- 新增启动期服务：在 `App()` 的 `ConfigureServices` 注册，并在 `OnStartup()` 明确初始化顺序。
- 新增退出或重启入口：统一调用 `App.RequestShutdown()` 并补充明确的 `AppShutdownReason`，不要直接调用 WPF `Shutdown()`。
- 增加全局异常策略：优先放到 `RegisterDispatcherUnhandledException` / `RegisterTaskSchedulerUnhandledException`。
- 调整窗口初始行为：优先改 `MainWindow.OnContentRendered()` 与 `MainWindowViewModel.UpdatePosition()` 配合逻辑。
- 调整窗口置前行为：普通/强制 Win32 细节统一修改 `Win32Helper`；来源边界统一修改 `WindowActivationContext` 与 `ExternalCallService`，不要在具体热键或窗口命令中增加来源分支。
- 调整主窗口失焦自动隐藏：同步检查 `MainWindowViewModel.Show()`、`MainWindow.OnDeactivated()` 与 `HideWhenDeactivated`。
- 调整首次向导：优先改 `App` 中启动前配置文件存在性判断、主窗口创建时机和 `WelcomeSetupWindow`，避免用额外设置项控制是否已完成。
- 设置页新增导航项：同步修改 `SettingsWindow.xaml` 菜单与 `SettingsWindow.xaml.cs` 的 `Navigate` 映射。
- 新增阻断性弹窗：统一调用 `AppMessageBox.Show()`，保持活动窗口优先、透明 owner 兜底的策略。
- 调整管理员或后台启动：同步修改 `UACHelper.Run()` 与 Host `start` 命令参数，确保主进程和 Host 协议一致。
