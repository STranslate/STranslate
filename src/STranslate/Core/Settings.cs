using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using iNKORE.UI.WPF.Modern;
using Serilog.Core;
using Serilog.Events;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Services;
using STranslate.Views;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Text.Json.Serialization;
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

    [ObservableProperty] public partial bool IsMouseSelectionTranslationVisible { get; set; } = true;

    [ObservableProperty] public partial bool IsMouseSelectionTranslationEnabled { get; set; } = false;

    [ObservableProperty] public partial bool IsMouseSelectionIconEnabled { get; set; } = false;

    [ObservableProperty] public partial bool IsHistoryNavigationVisible { get; set; } = true;

    [ObservableProperty] public partial bool IsOcrVisible { get; set; } = true;

    [ObservableProperty] public partial bool IsClipboardMonitorVisible { get; set; } = true;
    [ObservableProperty] public partial List<string> MainHeaderVisibleActions { get; set; } = [];
    [ObservableProperty] public partial bool IsServiceSwitcherVisible { get; set; } = true;
    [ObservableProperty] public partial bool IsCloseButtonVisible { get; set; } = false;

    [ObservableProperty] public partial DoubleClickTrayFunction DoubleClickTrayFunction { get; set; }

    [ObservableProperty] public partial CopyAfterTranslation CopyAfterTranslation { get; set; }

    [ObservableProperty] public partial bool CopyAfterTranslationNotAutomatic { get; set; }

    [ObservableProperty] public partial bool CopyAfterOcr { get; set; }

    [ObservableProperty] public partial bool FocusInputAfterScreenshotTranslate { get; set; } = true;

    [ObservableProperty] public partial LangEnum ScreenshotOcrLanguage { get; set; } = LangEnum.Auto;

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
    /// 增量翻译触发时清空原本内容（默认 true）。
    /// true：按下增量翻译键时清空输入框，本次会话内选中文本仍累积追加；
    /// false：保留旧逻辑，不清空原有内容，直接追加。
    /// </summary>
    [ObservableProperty] public partial bool IncrementalClearInput { get; set; } = true;

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

    #region 灵动岛

    /// <summary>
    /// 是否启用灵动岛（替代原翻译窗口展示翻译结果）
    /// </summary>
    [ObservableProperty] public partial bool DynamicIslandEnabled { get; set; } = false;

    /// <summary>
    /// 灵动岛最小宽度（文本较短时收缩到的宽度）
    /// </summary>
    [ObservableProperty] public partial double DynamicIslandMinWidth { get; set; } = 180;

    /// <summary>
    /// 灵动岛最大宽度（文本过长时截断显示的宽度）
    /// </summary>
    [ObservableProperty] public partial double DynamicIslandMaxWidth { get; set; } = 520;

    /// <summary>
    /// 灵动岛高度（胶囊高度）
    /// </summary>
    [ObservableProperty] public partial double DynamicIslandHeight { get; set; } = 54;

    /// <summary>
    /// 灵动岛自动隐藏时长（秒），显示翻译结果后无操作自动隐藏
    /// </summary>
    [ObservableProperty] public partial double DynamicIslandDurationSeconds { get; set; } = 10;

    /// <summary>
    /// 灵动岛内容字体大小
    /// </summary>
    [ObservableProperty] public partial double DynamicIslandFontSize { get; set; } = 15;

    /// <summary>
    /// 灵动岛距屏幕工作区顶部的间距
    /// </summary>
    [ObservableProperty] public partial double DynamicIslandTopMargin { get; set; } = 16;

    #endregion

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
    [JsonConverter(typeof(LayoutAnalysisModeJsonConverter))]
    [ObservableProperty] public partial LayoutAnalysisMode LayoutAnalysisMode { get; set; } = LayoutAnalysisMode.Auto;
    #endregion

    #region OCR Settings

    [ObservableProperty] public partial LangEnum OcrWindowOcrLanguage { get; set; } = LangEnum.Auto;
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

    [ObservableProperty] public partial ImageTranslateWindowMode ImageTranslateWindowMode { get; set; } = ImageTranslateWindowMode.Standalone;
    [ObservableProperty] public partial bool IsImTranShowingAnnotated { get; set; } = false;
    [ObservableProperty] public partial bool IsImTranShowingTextControl { get; set; } = false;
    [ObservableProperty] public partial LangEnum ImageTranslateOcrLanguage { get; set; } = LangEnum.Auto;
    [ObservableProperty] public partial bool IsImageTranslateCompactOcrLanguageVisible { get; set; } = false;
    [ObservableProperty] public partial LangEnum ImageTranslateSourceLang { get; set; } = LangEnum.Auto;
    [ObservableProperty] public partial LangEnum ImageTranslateTargetLang { get; set; } = LangEnum.Auto;

    /// <summary>
    ///     图片翻译独立的语种识别引擎
    /// </summary>
    [ObservableProperty] public partial LanguageDetectorType ImageTranslateLanguageDetector { get; set; } = LanguageDetectorType.Local;

    /// <summary>
    ///     图片翻译独立的本地识别中英字符比例阈值
    /// </summary>
    [ObservableProperty] public partial double ImageTranslateLocalDetectorRate { get; set; } = 0.8;

    /// <summary>
    ///     图片翻译：原始语言识别为自动且在线识别出错时使用的源语种
    /// </summary>
    [ObservableProperty] public partial LangEnum ImageTranslateSourceLangIfAuto { get; set; } = LangEnum.English;

    /// <summary>
    ///     图片翻译 Auto 目标解析：第一语言
    /// </summary>
    [ObservableProperty] public partial LangEnum ImageTranslateFirstLanguage { get; set; } = LangEnum.ChineseSimplified;

    /// <summary>
    ///     图片翻译 Auto 目标解析：第二语言
    /// </summary>
    [ObservableProperty] public partial LangEnum ImageTranslateSecondLanguage { get; set; } = LangEnum.English;

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
            // 旧版本配置中不存在服务快捷开关；仅在兼容可见状态为 true 时自动加入一次。
            // 用户后续从标题栏移除后，SyncLegacyMainHeaderVisibility 会持久化 false。
            if (IsServiceSwitcherVisible && !normalizedActions.Contains(MainHeaderActions.ServiceSwitcher))
            {
                normalizedActions.Add(MainHeaderActions.ServiceSwitcher);
            }

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
        if (IsMouseSelectionTranslationVisible) migratedActions.Add(MainHeaderActions.MouseSelectionTranslation);
        if (IsColorSchemeVisible) migratedActions.Add(MainHeaderActions.ColorScheme);
        if (IsHideInputVisible) migratedActions.Add(MainHeaderActions.HideInput);
        if (IsServiceSwitcherVisible) migratedActions.Add(MainHeaderActions.ServiceSwitcher);
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

        NormalizeLayoutAnalysisMode();
        EnsureMainHeaderVisibleActionsInitialized();

        ApplyLogLevel();
        ApplyStartup();
        ApplyStartMode();
    }

    public void LazyInitialize(bool initializeLanguage = true)
    {
        ApplyFontFamily(true);
        if (initializeLanguage)
            ApplyLanguage(true);
        ApplyFontSize();
        ApplyTheme();
        ApplyDeactived();
        ApplyExternalCall();
        ApplyMouseSelectionFeatures();
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

    internal void NormalizeLayoutAnalysisMode()
    {
        if (LayoutAnalysisMode is not (LayoutAnalysisMode.Auto or LayoutAnalysisMode.Provider or LayoutAnalysisMode.Smart or LayoutAnalysisMode.NoMerge))
            LayoutAnalysisMode = LayoutAnalysisMode.Auto;
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
            case nameof(IsMouseSelectionTranslationEnabled):
            case nameof(IsMouseSelectionIconEnabled):
                ApplyMouseSelectionFeatures();
                break;
            case nameof(LocalDetectorRate):
                LocalDetectorRate = Math.Round(LocalDetectorRate, 2);
                break;
            case nameof(ImageTranslateLocalDetectorRate):
                ImageTranslateLocalDetectorRate = Math.Round(ImageTranslateLocalDetectorRate, 2);
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
        IsMouseSelectionTranslationVisible = actionSet.Contains(MainHeaderActions.MouseSelectionTranslation);
        IsColorSchemeVisible = actionSet.Contains(MainHeaderActions.ColorScheme);
        IsHideInputVisible = actionSet.Contains(MainHeaderActions.HideInput);
        IsServiceSwitcherVisible = actionSet.Contains(MainHeaderActions.ServiceSwitcher);
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

    private bool _isApplyingMouseSelectionFeatures;

    private void ApplyMouseSelectionFeatures()
    {
        if (_isApplyingMouseSelectionFeatures)
            return;

        var mouseSelectionService = Ioc.Default.GetRequiredService<MouseSelectionService>();
        if (mouseSelectionService.ApplyPersistentFeatures(
                IsMouseSelectionTranslationEnabled,
                IsMouseSelectionIconEnabled))
            return;

        try
        {
            _isApplyingMouseSelectionFeatures = true;
            IsMouseSelectionTranslationEnabled = false;
            IsMouseSelectionIconEnabled = false;
        }
        finally
        {
            _isApplyingMouseSelectionFeatures = false;
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
    MouseSelection = 1,
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
    Auto,
    Provider,
    Smart,
    NoMerge,
}

public enum ImageTranslateWindowMode
{
    Standalone,
    Compact,
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
    ToggleMouseSelectionTranslation,
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
