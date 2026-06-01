using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using iNKORE.UI.WPF.Modern;
using Serilog.Core;
using Serilog.Events;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Views;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace STranslate.Core;

public partial class Settings : ObservableObject
{
    private AppStorage<Settings> Storage { get; set; } = null!;

    #region Setting Items

    [ObservableProperty] public partial bool AutoStartup { get; set; } = false;
    [ObservableProperty] public partial StartMode StartMode { get; set; } = StartMode.Normal;

    [ObservableProperty] public partial string FontFamily { get; set; } = Win32Helper.GetSystemDefaultFont();

    /// <summary>
    /// 界面字体大小
    ///     * MenuItem Icon & TextBlock 除外
    /// </summary>
    [ObservableProperty] public partial double FontSize { get; set; } = 14;

    [ObservableProperty] public partial string Language { get; set; } = Constant.SystemLanguageCode;

    [ObservableProperty] public partial bool HideOnStartup { get; set; } = false;

    [ObservableProperty] public partial bool HideWhenDeactivated { get; set; } = true;

    [ObservableProperty] public partial bool DisableGlobalHotkeys { get; set; } = false;

    [ObservableProperty] public partial bool IgnoreHotkeysOnFullscreen { get; set; } = false;

    [ObservableProperty] public partial List<string> ExcludedGlobalTriggerProcesses { get; set; } = [];

    [ObservableProperty] public partial bool HideNotifyIcon { get; set; } = false;

    /// <summary>
    /// 是否启用自动检查更新
    /// </summary>
    [ObservableProperty] public partial bool AutoCheckUpdate { get; set; } = true;

    [ObservableProperty] public partial ElementTheme ColorScheme { get; set; }

    [ObservableProperty] public partial HistoryLimit HistoryLimit { get; set; } = HistoryLimit.Limit1000;

    [ObservableProperty] public partial bool IsColorSchemeVisible { get; set; } = true;

    [ObservableProperty] public partial bool IsScreenshotTranslateVisible { get; set; } = true;
    [ObservableProperty] public partial bool IsImageTranslateVisible { get; set; } = true;

    /// <summary>
    /// 截图时是否显示辅助线
    /// </summary>
    [ObservableProperty] public partial bool ShowScreenshotAuxiliaryLines { get; set; } = true;

    [ObservableProperty] public partial bool HideInput { get; set; } = false;

    [ObservableProperty] public partial bool HideInputWithLangSelectControl { get; set; } = false;

    [ObservableProperty] public partial bool IsHideInputVisible { get; set; } = true;

    [ObservableProperty] public partial bool IsMouseHookVisible { get; set; } = true;

    [ObservableProperty] public partial bool IsHistoryNavigationVisible { get; set; } = true;

    [ObservableProperty] public partial bool IsOcrVisible { get; set; } = true;

    [ObservableProperty] public partial bool IsClipboardMonitorVisible { get; set; } = true;
    [ObservableProperty] public partial List<string> MainHeaderVisibleActions { get; set; } = [];
    [ObservableProperty] public partial bool IsCloseButtonVisible { get; set; } = false;

    [ObservableProperty] public partial DoubleClickTrayFunction DoubleClickTrayFunction { get; set; }

    [ObservableProperty] public partial CopyAfterTranslation CopyAfterTranslation { get; set; }

    [ObservableProperty] public partial bool CopyAfterTranslationNotAutomatic { get; set; }

    [ObservableProperty] public partial bool CopyAfterOcr { get; set; }

    [ObservableProperty] public partial bool FocusInputAfterScreenshotTranslate { get; set; } = true;

    [ObservableProperty] public partial int HttpTimeout { get; set; } = 30;

    [ObservableProperty] public partial LangEnum SourceLang { get; set; } = LangEnum.Auto;

    [ObservableProperty] public partial LangEnum TargetLang { get; set; } = LangEnum.Auto;

    /// <summary>
    ///     语种识别类型
    /// </summary>
    [ObservableProperty] public partial LanguageDetectorType LanguageDetector { get; set; } = LanguageDetectorType.Local;

    /// <summary>
    /// 本地识别英文比例阈值
    /// </summary>
    [ObservableProperty] public partial double LocalDetectorRate { get; set; } = 0.8;

    /// <summary>
    ///     原始语言识别为自动时使用该配置
    ///     * 使用在线识别服务出错时使用
    /// </summary>
    [ObservableProperty] public partial LangEnum SourceLangIfAuto { get; set; } = LangEnum.English;

    [ObservableProperty] public partial LangEnum FirstLanguage { get; set; } = LangEnum.ChineseSimplified;

    [ObservableProperty] public partial LangEnum SecondLanguage { get; set; } = LangEnum.English;

    /// <summary>
    /// 文本输出是否使用剪贴板粘贴。
    /// false: 键盘模拟输入（默认）
    /// true: 剪贴板 Ctrl+V
    /// </summary>
    [ObservableProperty] public partial bool UseClipboardOutput { get; set; } = false;

    /// <summary>
    /// 粘贴时自动翻译
    /// </summary>
    [ObservableProperty] public partial bool TranslateOnPaste { get; set; } = true;
    
    /// <summary>
    /// 切换提示词后自动翻译
    /// </summary>
    [ObservableProperty] public partial bool AutoTranslateOnPromptChanged { get; set; } = false;

    [ObservableProperty] public partial bool IsAutoTranslateVisible { get; set; } = true;

    /// <summary>
    /// 自动翻译
    /// </summary>
    [ObservableProperty] public partial bool AutoTranslate { get; set; } = false;

    /// <summary>
    /// 自动翻译延时（毫秒）
    /// </summary>
    [ObservableProperty] public partial int AutoTranslateDelayMs { get; set; } = 500;

    public double PreviousScreenWidth { get; set; }
    public double PreviousScreenHeight { get; set; }
    [ObservableProperty] public partial int CustomScreenNumber { get; set; } = 1;
    [ObservableProperty] public partial WindowScreenType WindowScreen { get; set; } = WindowScreenType.Cursor;
    public bool IsWindowAlignVisible =>
        WindowScreen != WindowScreenType.RememberLastLaunchLocation &&
        WindowScreen != WindowScreenType.FollowMouse;
    partial void OnWindowScreenChanged(WindowScreenType value) => OnPropertyChanged(nameof(IsWindowAlignVisible));
    [ObservableProperty] public partial WindowAlignType WindowAlign { get; set; } = WindowAlignType.Center;
    [ObservableProperty] public partial double MainWindowLeft { get; set; }
    [ObservableProperty] public partial double MainWindowTop { get; set; }
    [ObservableProperty] public partial double CustomWindowLeft { get; set; }
    [ObservableProperty] public partial double CustomWindowTop { get; set; }
    [ObservableProperty] public partial double MainWindowMaxHeightRatio { get; set; } = 0.85;

    private double _mainWindowWidth = 470;
    public double MainWindowWidth
    {
        get => _mainWindowWidth;
        set
        {
            // 隐藏时这个宽度似乎会变化
            if (App.Current.MainWindow != null && !App.Current.MainWindow.IsVisible) return;
            SetProperty(ref _mainWindowWidth, value);
        }
    }
    [ObservableProperty] public partial double MainWindowMaxHeight { get; set; } = 800;

    [ObservableProperty] public partial bool ShowPascalCase { get; set; } = true;
    [ObservableProperty] public partial bool ShowCamelCase { get; set; } = false;
    [ObservableProperty] public partial bool ShowSnakeCase { get; set; } = true;
    [ObservableProperty] public partial bool ShowInsert { get; set; } = true;
    [ObservableProperty] public partial bool ShowBackTranslation { get; set; } = true;

    /// <summary>
    /// 主界面Llm服务是否显示提示词按钮
    /// </summary>
    [ObservableProperty] public partial bool ShowPromptButton { get; set; } = true;

    [ObservableProperty] public partial bool ShowScreenshotItemInNotifyIconMenu { get; set; } = false;
    [ObservableProperty] public partial bool ShowImageTranslateItemInNotifyIconMenu { get; set; } = false;
    [ObservableProperty] public partial bool ShowOcrItemInNotifyIconMenu { get; set; } = false;
    [ObservableProperty] public partial bool ShowQrCodeItemInNotifyIconMenu { get; set; } = false;

    /// <summary>
    /// 取词时换行处理
    /// </summary>
    [ObservableProperty] public partial LineBreakHandleType LineBreakHandleType { get; set; } = LineBreakHandleType.RemoveExtraLineBreak;

    /// <summary>
    /// 取词时分隔符处理
    /// </summary>
    [ObservableProperty] public partial TextSeparatorHandleType TextSeparatorHandleType { get; set; } = TextSeparatorHandleType.None;

    /// <summary>
    /// 取词分隔符处理生效范围
    /// </summary>
    [ObservableProperty] public partial TextSeparatorHandleScope TextSeparatorHandleScopes { get; set; } = TextSeparatorHandleScope.Crossword;

    /// <summary>
    /// 划词后等待剪贴板写入文本的最长时间（毫秒）。
    /// </summary>
    [ObservableProperty] public partial int SelectedTextFetchTimeoutMs { get; set; } = 500;

    /// <summary>
    /// 划词取词失败时的回退目标。
    /// </summary>
    [ObservableProperty] public partial CrosswordFetchFailedFallbackTarget CrosswordFetchFailedFallbackTarget { get; set; } = CrosswordFetchFailedFallbackTarget.InputTranslate;

    [ObservableProperty] public partial ImageQuality ImageQuality { get; set; } = ImageQuality.Medium;

    #region Layout Analysis
    /* 版面分析参数配置
     *
     * 1.全部合并
     * private const double VerticalThresholdRatio = 6.0;
     * private const double HorizontalThresholdRatio = 6.0;
     * private const double LineSpacingThresholdRatio = 0.3;
     * private const double WordSpacingThresholdRatio = 0.2;
     *
     * 2.严格段落分析（保持原始结构）
     * private const double VerticalThresholdRatio = 0.3;
     * private const double HorizontalThresholdRatio = 0.2;
     * private const double LineSpacingThresholdRatio = 0.2;
     * private const double WordSpacingThresholdRatio = 0.1;
     *
     * 3.标准文档分析（推荐默认值）
     * private const double VerticalThresholdRatio = 0.8;
     * private const double HorizontalThresholdRatio = 0.5;
     * private const double LineSpacingThresholdRatio = 0.3;
     * private const double WordSpacingThresholdRatio = 0.2;
     *
     * 4.分栏文档分析（合并左右分栏）
     * private const double VerticalThresholdRatio = 0.6;
     * private const double HorizontalThresholdRatio = 2.0;     // 增大以跨越分栏间距
     * private const double LineSpacingThresholdRatio = 0.3;
     * private const double WordSpacingThresholdRatio = 0.2;
     *
     * 5.不合并任何文本块
     * private const double VerticalThresholdRatio = 0.0;       // 设置为0，不允许垂直合并
     * private const double HorizontalThresholdRatio = 0.0;     // 设置为0，不允许水平合并
     * private const double LineSpacingThresholdRatio = 0.3;    // 这两个参数在不合并时不会被使用
     * private const double WordSpacingThresholdRatio = 0.2;    // 这两个参数在不合并时不会被使用
     */
    [ObservableProperty] public partial LayoutAnalysisMode LayoutAnalysisMode { get; set; } = LayoutAnalysisMode.StandardDocument;

    /// <summary>
    /// 垂直相邻检测阈值比例
    /// 作用: 控制上下相邻文本块的合并敏感度
    /// 计算: 阈值 = Math.Min(rect1.Height, rect2.Height)* VerticalThresholdRatio
    /// 调整建议:
    ///     0.1-0.3: 严格模式，只合并非常接近的行
    ///     0.5-0.8: 标准模式，合并段落内的行
    ///     1.0-3.0: 宽松模式，合并距离较远的文本块
    ///     >5.0: 几乎合并所有垂直分布的文本
    /// </summary>
    [ObservableProperty] public partial double VerticalThresholdRatio { get; set; } = 0.8;

    /// <summary>
    /// 水平相邻检测阈值比例
    /// 作用: 控制左右相邻文本块的合并敏感度
    /// 计算: 阈值 = Math.Min(rect1.Width, rect2.Width)* HorizontalThresholdRatio
    /// 调整建议:
    ///     0.1-0.3: 严格模式，只合并非常接近的词
    ///     0.5-0.8: 标准模式，合并同行内的词组
    ///     1.0-2.0: 宽松模式，合并分栏文本
    ///     >5.0: 几乎合并所有水平分布的文本
    /// </summary>
    [ObservableProperty] public partial double HorizontalThresholdRatio { get; set; } = 0.5;

    /// <summary>
    /// 换行检测阈值比例
    /// 作用: 控制合并文本时是否添加换行符
    /// 计算: 阈值 = lastRect.Height* LineSpacingThresholdRatio
    /// 调整建议:
    ///     0.1-0.2: 严格换行，即使很小的距离也换行
    ///     0.3-0.5: 标准换行，适合大多数文档
    ///     0.8-1.0: 宽松换行，只有距离很大才换行
    ///     >2.0: 几乎不换行，所有文本连在一起
    [ObservableProperty] public partial double LineSpacingThresholdRatio { get; set; } = 0.3;

    /// <summary>
    /// 词间距检测阈值比例
    /// 作用: 控制合并文本时是否添加空格
    /// 计算: 阈值 = lastRect.Width* WordSpacingThresholdRatio
    /// 调整建议:
    ///     0.1-0.2: 标准空格间距
    ///     0.3-0.5: 宽松空格间距
    ///     >1.0: 几乎不添加空格
    /// </summary>
    [ObservableProperty] public partial double WordSpacingThresholdRatio { get; set; } = 0.2;
    #endregion

    #region OCR Settings

    [ObservableProperty] public partial LangEnum OcrLanguage { get; set; } = LangEnum.Auto;
    [ObservableProperty] public partial bool IsOcrShowingAnnotated { get; set; } = false;
    [ObservableProperty] public partial bool IsOcrShowingTextControl { get; set; } = false;
    [ObservableProperty] public partial double OcrWindowWidth { get; set; } = 600;
    [ObservableProperty] public partial double OcrWindowHeight { get; set; } = 600;
    [ObservableProperty] public partial OcrResultShowingType OcrResultShowingType { get; set; } = OcrResultShowingType.Original;

    #endregion

    #region Plugin Market Settings

    /// <summary>
    /// 插件市场CDN源
    /// </summary>
    [ObservableProperty] public partial PluginMarketCdnSourceType PluginMarketCdnSource { get; set; } = PluginMarketCdnSourceType.JsDelivr;

    /// <summary>
    /// 自定义插件市场CDN URL模板
    /// 可用占位符: {author}, {repo}, {branch}, {path}
    /// </summary>
    [ObservableProperty] public partial string CustomPluginMarketCdnUrl { get; set; } = "https://fastly.jsdelivr.net/gh/{author}/{repo}@{branch}/{path}";

    /// <summary>
    /// 插件下载代理类型
    /// </summary>
    [ObservableProperty] public partial PluginDownloadProxyType PluginDownloadProxy { get; set; } = PluginDownloadProxyType.GitHub;

    /// <summary>
    /// 自定义下载代理URL
    /// </summary>
    [ObservableProperty] public partial string CustomDownloadProxyUrl { get; set; } = string.Empty;

    #endregion

    #region Image Translate Settings

    [ObservableProperty] public partial bool IsImTranShowingAnnotated { get; set; } = false;
    [ObservableProperty] public partial bool IsImTranShowingTextControl { get; set; } = false;
    [ObservableProperty] public partial LangEnum ImageTranslateSourceLang { get; set; } = LangEnum.Auto;
    [ObservableProperty] public partial LangEnum ImageTranslateTargetLang { get; set; } = LangEnum.Auto;
    [ObservableProperty] public partial double ImTranWindowWidth { get; set; } = 600;
    [ObservableProperty] public partial double ImTranWindowHeight { get; set; } = 600;

    #endregion

    [ObservableProperty] public partial LogEventLevel LogLevel { get; set; } = LogEventLevel.Information;

    [ObservableProperty] public partial bool EnableExternalCall { get; set; } = false;

    [ObservableProperty] public partial int ExternalCallPort { get; set; } = 50020;

    /// <summary>
    /// 将属性变更通知冒泡到Settings的订阅者
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void OnSubPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(e);

    [ObservableProperty] public partial ProxySettings Proxy { get; set; } = new();

    partial void OnProxyChanged(ProxySettings? oldValue, ProxySettings? newValue)
    {
        if (oldValue != null)
        {
            oldValue.PropertyChanged -= OnSubPropertyChanged;
        }

        if (newValue != null)
        {
            newValue.PropertyChanged += OnSubPropertyChanged;
        }
    }

    [ObservableProperty] public partial BackupSettings Backup { get; set; } = new();

    partial void OnBackupChanged(BackupSettings? oldValue, BackupSettings? newValue)
    {
        if (oldValue != null)
        {
            oldValue.PropertyChanged -= OnSubPropertyChanged;
        }

        if (newValue != null)
        {
            newValue.PropertyChanged += OnSubPropertyChanged;
        }
    }

    partial void OnMainWindowMaxHeightRatioChanged(double value)
    {
        var normalized = Math.Clamp(Math.Round(value, 2), 0.6, 1.0);
        if (Math.Abs(normalized - value) > double.Epsilon)
        {
            MainWindowMaxHeightRatio = normalized;
        }
    }

    partial void OnSelectedTextFetchTimeoutMsChanged(int value)
    {
        var normalized = Math.Clamp(value, 50, 5000);
        if (normalized != value)
        {
            SelectedTextFetchTimeoutMs = normalized;
        }
    }

    #endregion

    #region Public Methods

    public void SetStorage(AppStorage<Settings> storage)
    {
        Storage = storage;

        // 属性更改时自动保存设置
        PropertyChanged += (s, e) =>
        {
            HandlePropertyChanged(e.PropertyName);

            if (e.PropertyName == nameof(MainWindowTop) ||
                e.PropertyName == nameof(MainWindowLeft) ||
                e.PropertyName == nameof(MainWindowWidth) ||
                e.PropertyName == nameof(MainWindowMaxHeightRatio) ||
                e.PropertyName == nameof(AutoTranslateDelayMs) ||
                e.PropertyName == nameof(SelectedTextFetchTimeoutMs))
                SaveWithDebounce();
            else
                Save();
        };
    }

    internal void Save() => Storage?.Save();

    public void EnsureMainHeaderVisibleActionsInitialized()
    {
        var normalizedActions = MainHeaderActions.Normalize(MainHeaderVisibleActions);
        if (normalizedActions.Count > 0)
        {
            if (!MainHeaderVisibleActions.SequenceEqual(normalizedActions))
            {
                MainHeaderVisibleActions = [.. normalizedActions];
            }

            SyncLegacyMainHeaderVisibility(normalizedActions);
            return;
        }

        var migratedActions = new List<string>();
        if (IsClipboardMonitorVisible) migratedActions.Add(MainHeaderActions.ClipboardMonitor);
        if (IsAutoTranslateVisible) migratedActions.Add(MainHeaderActions.AutoTranslate);
        if (IsOcrVisible) migratedActions.Add(MainHeaderActions.Ocr);
        if (IsImageTranslateVisible) migratedActions.Add(MainHeaderActions.ImageTranslate);
        if (IsScreenshotTranslateVisible) migratedActions.Add(MainHeaderActions.ScreenshotTranslate);
        if (IsMouseHookVisible) migratedActions.Add(MainHeaderActions.MouseHook);
        if (IsColorSchemeVisible) migratedActions.Add(MainHeaderActions.ColorScheme);
        if (IsHideInputVisible) migratedActions.Add(MainHeaderActions.HideInput);
        if (IsHistoryNavigationVisible) migratedActions.Add(MainHeaderActions.HistoryNavigation);

        ApplyMainHeaderVisibleActions(migratedActions);
    }

    public void ApplyMainHeaderVisibleActions(IReadOnlyList<string> actions)
    {
        var normalizedActions = MainHeaderActions.Normalize(actions);
        if (!MainHeaderVisibleActions.SequenceEqual(normalizedActions))
        {
            MainHeaderVisibleActions = [.. normalizedActions];
        }

        SyncLegacyMainHeaderVisibility(normalizedActions);
    }

    public void Initialize()
    {
        if (Storage is null)
        {
            throw new InvalidOperationException("Storage is not set. Please call SetStorage() before Initialize().");
        }

        EnsureMainHeaderVisibleActionsInitialized();

        ApplyLogLevel();
        ApplyStartup();
        ApplyStartMode();
    }

    public void LazyInitialize()
    {
        ApplyFontFamily(true);
        ApplyLanguage(true);
        ApplyFontSize();
        ApplyTheme();
        ApplyDeactived();
        ApplyExternalCall();
    }

    internal ImageFormat GetImageFormat() =>
        ImageQuality switch
        {
            ImageQuality.Low => ImageFormat.Jpeg,
            ImageQuality.Medium => ImageFormat.Png,
            ImageQuality.High => ImageFormat.Bmp,
            _ => ImageFormat.Png,
        };

    internal BitmapEncoder GetBitmapEncoder()
    {
        return ImageQuality switch
        {
            ImageQuality.Low => new JpegBitmapEncoder { QualityLevel = 50 },
            ImageQuality.Medium => new PngBitmapEncoder(),
            ImageQuality.High => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
    }

    #endregion

    #region Private Methods

    private readonly DebounceExecutor _debounceExecutor = new();
    private const int DebounceTimeMs = 500; // 防抖时间
    internal void SaveWithDebounce()
    {
        _debounceExecutor.Execute(Save, TimeSpan.FromMilliseconds(DebounceTimeMs));
    }

    private void HandlePropertyChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(AutoStartup):
                ApplyStartup();
                break;
            case nameof(StartMode):
                ApplyStartMode();
                break;
            case nameof(Language):
                ApplyLanguage();
                break;
            case nameof(FontFamily):
                ApplyFontFamily();
                break;
            case nameof(FontSize):
                ApplyFontSize();
                break;
            case nameof(ColorScheme):
                ApplyTheme();
                break;
            case nameof(HideWhenDeactivated):
                ApplyDeactived();
                break;
            case nameof(LogLevel):
                ApplyLogLevel();
                break;
            case nameof(EnableExternalCall):
            case nameof(ExternalCallPort):
                ApplyExternalCall();
                break;
            case nameof(DisableGlobalHotkeys):
                Ioc.Default.GetRequiredService<HotkeySettings>().ApplyGlobalHotkeys();
                break;
            case nameof(IgnoreHotkeysOnFullscreen):
                Ioc.Default.GetRequiredService<HotkeySettings>().ApplyIgnoreOnFullScreen();
                break;
            case nameof(ExcludedGlobalTriggerProcesses):
                GlobalTriggerGuard.InvalidateCache();
                break;
            case nameof(LocalDetectorRate):
                LocalDetectorRate = Math.Round(LocalDetectorRate, 2);
                break;
            default:
                break;
        }
    }

    private void SyncLegacyMainHeaderVisibility(IReadOnlyList<string> actions)
    {
        var actionSet = actions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        IsClipboardMonitorVisible = actionSet.Contains(MainHeaderActions.ClipboardMonitor);
        IsAutoTranslateVisible = actionSet.Contains(MainHeaderActions.AutoTranslate);
        IsOcrVisible = actionSet.Contains(MainHeaderActions.Ocr);
        IsImageTranslateVisible = actionSet.Contains(MainHeaderActions.ImageTranslate);
        IsScreenshotTranslateVisible = actionSet.Contains(MainHeaderActions.ScreenshotTranslate);
        IsMouseHookVisible = actionSet.Contains(MainHeaderActions.MouseHook);
        IsColorSchemeVisible = actionSet.Contains(MainHeaderActions.ColorScheme);
        IsHideInputVisible = actionSet.Contains(MainHeaderActions.HideInput);
        IsHistoryNavigationVisible = actionSet.Contains(MainHeaderActions.HistoryNavigation);
    }

    #endregion

    #region Apply Methods

    private void ApplyStartup()
    {
        if (string.IsNullOrEmpty(DataLocation.StartupPath))
        {
            AutoStartup = false;
            return;
        }

        if (AutoStartup)
        {
            if (!Utilities.IsStartup())
                Utilities.SetStartup();
        }
        else
        {
            Utilities.UnSetStartup();
        }
    }

    private void ApplyStartMode()
    {
        if (StartMode == StartMode.SkipUACAdmin)
        {
            UACHelper.Create();
        }
        else
        {
            if (UACHelper.Exist())
            {
                UACHelper.Delete();
            }
        }
    }

    private void ApplyLanguage(bool initialize = false)
    {
        var i18n = Ioc.Default.GetRequiredService<Internationalization>();
        if (initialize)
            i18n.InitializeLanguage(Language);
        else
            i18n.ChangeLanguage(Language);
    }

    private void ApplyFontFamily(bool initialize = false)
    {
        // 初始化时检查字体有效性
        if (initialize && !Fonts.SystemFontFamilies.Select(x => x.Source).Contains(FontFamily))
        {
            FontFamily = Win32Helper.GetSystemDefaultFont();
            return;
        }

        App.Current.Resources["ContentControlThemeFontFamily"] = new FontFamily(FontFamily);

        // https://github.com/iNKORE-NET/UI.WPF.Modern/releases/tag/v0.10.2
        // https://github.com/iNKORE-NET/UI.WPF.Modern/issues/396
        //App.Current.Resources[System.Windows.SystemFonts.MessageFontFamilyKey] = new FontFamily(FontFamily);
    }

    private void ApplyFontSize()
    {
        // original
        App.Current.Resources["ControlContentThemeFontSize"] = FontSize;    //14
        App.Current.Resources["BodyTextBlockFontSize"] = FontSize;    //14 BodyStrongTextBlockStyle
        App.Current.Resources["CaptionTextBlockFontSize"] = FontSize - 2;   //12
        App.Current.Resources["SubtitleTextBlockFontSize"] = FontSize + 6;  //20

        // custom for stranslate
        App.Current.Resources["STControlFontSize8"] = FontSize - 6;
        App.Current.Resources["STControlFontSize9"] = FontSize - 5;
        App.Current.Resources["STControlFontSize10"] = FontSize - 4;
        App.Current.Resources["STControlFontSize11"] = FontSize - 3;
        App.Current.Resources["STControlFontSize12"] = FontSize - 2;
        App.Current.Resources["STControlFontSize13"] = FontSize - 1;
        App.Current.Resources["STControlFontSize14"] = FontSize;
        App.Current.Resources["STControlFontSize15"] = FontSize + 1;
        App.Current.Resources["STControlFontSize16"] = FontSize + 2;
        App.Current.Resources["STControlFontSize17"] = FontSize + 3;
        App.Current.Resources["STControlFontSize18"] = FontSize + 4;
    }

    private void ApplyTheme()
    {
        // 遍历所有窗口统一应用主题
        foreach (System.Windows.Window window in App.Current.Windows)
        {
            ThemeManager.SetRequestedTheme(window, ColorScheme);
        }

        // 为 TaskbarIcon 的 ContextMenu 应用主题
        if (App.Current.MainWindow is MainWindow mainWindow)
        {
            var notifyIcon = mainWindow.FindName("PART_NotifyIcon") as Hardcodet.Wpf.TaskbarNotification.TaskbarIcon;
            if (notifyIcon?.ContextMenu != null)
            {
                ThemeManager.SetRequestedTheme(notifyIcon.ContextMenu, ColorScheme);
            }
        }
    }

    private void ApplyDeactived()
    {
        if (HideWhenDeactivated)
        {
            Win32Helper.HideFromAltTab(App.Current.MainWindow);
        }
        else
        {
            Win32Helper.ShowInAltTab(App.Current.MainWindow);
        }
    }

    private void ApplyLogLevel()
    {
        var loggingLevelSwitch = Ioc.Default.GetRequiredService<LoggingLevelSwitch>();
        loggingLevelSwitch.MinimumLevel = LogLevel;
    }

    private void ApplyExternalCall()
    {
        var externalCallService = Ioc.Default.GetRequiredService<ExternalCallService>();
        if (EnableExternalCall)
        {
            var result = externalCallService.StartService($"http://127.0.0.1:{ExternalCallPort}/");
            if (!result)
            {
                EnableExternalCall = false;
            }
        }
        else
        {
            externalCallService.StopService();
        }
    }

    #endregion
}

#region Enumeration definition

public enum StartMode
{
    Normal,
    Admin,
    SkipUACAdmin
}

public enum LanguageDetectorType
{
    Local,
    Baidu,

    /// <summary>
    /// 官方停止服务，弃用
    /// </summary>
    //Tencent,

    Niutrans,
    Bing,
    Yandex,
    Google,
    Microsoft,
}

public enum LineBreakHandleType
{
    None,
    RemoveExtraLineBreak,
    RemoveAllLineBreak,
    RemoveAllLineBreakWithoutSpace,
}

public enum TextSeparatorHandleType
{
    None,
    Underscore,
    Hyphen,
    UnderscoreAndHyphen,
}

[Flags]
public enum TextSeparatorHandleScope
{
    None = 0,
    MouseHook = 1,
    Crossword = 2,
    Incremental = 4,
    ClipboardMonitor = 8,
    ScreenshotTranslate = 16,
    SilentOcr = 32,
}

/// <summary>
/// 划词取词失败时，主窗口的回退行为。
/// </summary>
public enum CrosswordFetchFailedFallbackTarget
{
    /// <summary>
    /// 回退到输入翻译（清空输入并显示主窗口）。
    /// </summary>
    InputTranslate,

    /// <summary>
    /// 仅显示主窗口，保留当前输入与输出内容。
    /// </summary>
    ShowWindow,
}

public enum LayoutAnalysisMode
{
    MergeAll,
    StrictParagraph,
    StandardDocument,
    ColumnDocument,
    NoMerge,
    UserDefined,
}

public enum WindowScreenType
{
    RememberLastLaunchLocation,
    Cursor,
    Focus,
    Primary,
    FollowMouse,
    Custom
}

public enum WindowAlignType
{
    Center,
    CenterTop,
    LeftTop,
    RightTop,
    Custom
}

public enum OcrResultShowingType
{
    Original,
    Markdown,
    Latex
}

public enum HistoryLimit : long
{
    NotSave = 0,
    Limit100 = 100,
    Limit500 = 500,
    Limit1000 = 1000,
    Limit2000 = 2000,
    Limit5000 = 5000,
    Unlimited = long.MaxValue,
}

public enum CopyAfterTranslation
{
    NoAction,
    First,
    Second,
    Third,
    Fourth,
    Fifth,
    Sixth,
    Seventh,
    Eighth,
    Last,
}

public enum DoubleClickTrayFunction
{
    None,
    InputTranslate,
    ScreenshotTranslate,
    OCR,
    OpenSettingsWindow,
    ToggleMouseHook,
    ToggleGlobalHotkeys,
    Exit
}

public enum PluginMarketCdnSourceType
{
    JsDelivr,
    GitHubRaw,
    Custom
}

public enum PluginDownloadProxyType
{
    GitHub,
    GhProxyMirror,
    GhProxyNet,
    Custom
}

#endregion
