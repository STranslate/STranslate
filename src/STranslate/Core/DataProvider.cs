using iNKORE.UI.WPF.Modern;
using Serilog.Events;
using STranslate.Plugin;
using STranslate.ViewModels.Pages;
using System.Windows.Input;

namespace STranslate.Core;

/// <summary>
/// 枚举数据提供者，提供下拉选项的数据绑定支持
/// </summary>
public class DataProvider
{
    public DataProvider(Internationalization i18n)
    {
        i18n.OnLanguageChanged += UpdateLanguage;
        UpdateLanguage();
    }

    /// <summary>
    /// 更新语言标签
    /// </summary>
    public void UpdateLanguage()
    {
        DropdownDataGeneric<LangEnum>.UpdateLabels(LangEnums);
        DropdownDataGeneric<ProxyType>.UpdateLabels(ProxyTypes);
        DropdownDataGeneric<LanguageDetectorType>.UpdateLabels(LanguageDetectors);
        DropdownDataGeneric<ElementTheme>.UpdateLabels(ColorSchemes);
        DropdownDataGeneric<LineBreakHandleType>.UpdateLabels(LineBreakHandleTypes);
        DropdownDataGeneric<TextSeparatorHandleType>.UpdateLabels(TextSeparatorHandleTypes);
        DropdownDataGeneric<TextSeparatorHandleScope>.UpdateLabels(TextSeparatorHandleScopes);
        DropdownDataGeneric<CrosswordFetchFailedFallbackTarget>.UpdateLabels(CrosswordFetchFailedFallbackTargets);
        DropdownDataGeneric<PluginType>.UpdateLabels(PluginTypes);
        DropdownDataGeneric<LayoutAnalysisMode>.UpdateLabels(LayoutAnalysisModes);
        DropdownDataGeneric<WindowScreenType>.UpdateLabels(WindowScreenTypes);
        DropdownDataGeneric<WindowAlignType>.UpdateLabels(WindowAlignTypes);
        DropdownDataGeneric<StartMode>.UpdateLabels(StartModes);
        DropdownDataGeneric<LogEventLevel>.UpdateLabels(LogEventLevels);
        DropdownDataGeneric<OcrResultShowingType>.UpdateLabels(OcrResultShowingTypes);
        DropdownDataGeneric<HistoryLimit>.UpdateLabels(HistoryLimits);
        DropdownDataGeneric<CopyAfterTranslation>.UpdateLabels(CopyAfterTranslations);
        DropdownDataGeneric<BackupType>.UpdateLabels(BackupTypes);
        DropdownDataGeneric<ImageQuality>.UpdateLabels(ImageQualities);
        DropdownDataGeneric<DoubleClickTrayFunction>.UpdateLabels(DoubleClickTrayFunctions);
        DropdownDataGeneric<PluginMarketCdnSourceType>.UpdateLabels(PluginMarketCdnSources);
        DropdownDataGeneric<PluginDownloadProxyType>.UpdateLabels(PluginDownloadProxies);
    }

    #region LangEnums

    public class LangEnumData : DropdownDataGeneric<LangEnum> { }

    public List<LangEnumData> LangEnums { get; } =
        DropdownDataGeneric<LangEnum>.GetValues<LangEnumData>("LangEnum");

    #endregion

    #region ProxyTypes

    public class ProxyTypeData : DropdownDataGeneric<ProxyType> { }
    public List<ProxyTypeData> ProxyTypes { get; } =
        DropdownDataGeneric<ProxyType>.GetValues<ProxyTypeData>("ProxyType");

    #endregion

    #region LanguageDetectors

    public class LanguageDetectorData : DropdownDataGeneric<LanguageDetectorType> { }
    public List<LanguageDetectorData> LanguageDetectors { get; } =
        DropdownDataGeneric<LanguageDetectorType>.GetValues<LanguageDetectorData>("LanguageDetectorType");

    #endregion

    #region ColorSchemes

    public class ColorSchemeData : DropdownDataGeneric<ElementTheme> { }
    public List<ColorSchemeData> ColorSchemes { get; } =
        DropdownDataGeneric<ElementTheme>.GetValues<ColorSchemeData>("ColorScheme");

    #endregion

    #region LineBreakHandleTypes

    public class LineBreakHandleData : DropdownDataGeneric<LineBreakHandleType> { }
    public List<LineBreakHandleData> LineBreakHandleTypes { get; } =
        DropdownDataGeneric<LineBreakHandleType>.GetValues<LineBreakHandleData>("LineBreakHandleType");

    #endregion

    #region TextSeparatorHandleTypes

    public class TextSeparatorHandleTypeData : DropdownDataGeneric<TextSeparatorHandleType> { }
    public List<TextSeparatorHandleTypeData> TextSeparatorHandleTypes { get; } =
        DropdownDataGeneric<TextSeparatorHandleType>.GetValues<TextSeparatorHandleTypeData>("TextSeparatorHandleType");

    public class TextSeparatorHandleScopeData : DropdownDataGeneric<TextSeparatorHandleScope> { }
    public List<TextSeparatorHandleScopeData> TextSeparatorHandleScopes { get; } =
        DropdownDataGeneric<TextSeparatorHandleScope>
            .GetValues<TextSeparatorHandleScopeData>("TextSeparatorHandleScope")
            .Where(x => x.Value != TextSeparatorHandleScope.None)
            .ToList();

    #endregion

    #region CrosswordFetchFailedFallbackTargets

    public class CrosswordFetchFailedFallbackTargetData : DropdownDataGeneric<CrosswordFetchFailedFallbackTarget> { }
    public List<CrosswordFetchFailedFallbackTargetData> CrosswordFetchFailedFallbackTargets { get; } =
        DropdownDataGeneric<CrosswordFetchFailedFallbackTarget>.GetValues<CrosswordFetchFailedFallbackTargetData>("CrosswordFetchFailedFallbackTarget");

    #endregion

    #region PluginTypes

    public class PluginTypeData : DropdownDataGeneric<PluginType> { }
    public List<PluginTypeData> PluginTypes { get; } =
        DropdownDataGeneric<PluginType>.GetValues<PluginTypeData>("PluginType");

    #endregion

    #region LayoutAnalysisMode

    public class LayoutAnalysisModeData : DropdownDataGeneric<LayoutAnalysisMode> { }
    public List<LayoutAnalysisModeData> LayoutAnalysisModes { get; } =
        DropdownDataGeneric<LayoutAnalysisMode>.GetValues<LayoutAnalysisModeData>("LayoutAnalysisMode");

    #endregion

    #region WindowScreenTypes

    public class WindowScreenTypeData : DropdownDataGeneric<WindowScreenType> { }
    public List<WindowScreenTypeData> WindowScreenTypes { get; } =
        DropdownDataGeneric<WindowScreenType>.GetValues<WindowScreenTypeData>("WindowScreenType");

    #endregion

    #region WindowAlignTypes

    public class WindowAlignTypeData : DropdownDataGeneric<WindowAlignType> { }
    public List<WindowAlignTypeData> WindowAlignTypes { get; } =
        DropdownDataGeneric<WindowAlignType>.GetValues<WindowAlignTypeData>("WindowAlignType");

    #endregion

    #region StartModes

    public class StartModeData : DropdownDataGeneric<StartMode> { }
    public List<StartModeData> StartModes { get; } =
        DropdownDataGeneric<StartMode>.GetValues<StartModeData>("StartMode");

    #endregion

    #region LogEventLevels

    public class LogEventLevelData : DropdownDataGeneric<LogEventLevel> { }
    public List<LogEventLevelData> LogEventLevels { get; } =
        DropdownDataGeneric<LogEventLevel>.GetValues<LogEventLevelData>("LogEventLevel");

    #endregion

    #region OcrResultShowingTypes

    public class OcrResultShowingTypeData : DropdownDataGeneric<OcrResultShowingType> { }
    public List<OcrResultShowingTypeData> OcrResultShowingTypes { get; } =
        DropdownDataGeneric<OcrResultShowingType>.GetValues<OcrResultShowingTypeData>("OcrResultShowingType");

    #endregion

    #region HistoryLimits

    public class HistoryLimitData : DropdownDataGeneric<HistoryLimit> { }
    public List<HistoryLimitData> HistoryLimits { get; } =
        DropdownDataGeneric<HistoryLimit>.GetValues<HistoryLimitData>("HistoryLimit");

    #endregion

    #region CopyAfterTranslations

    public class CopyAfterTranslationData : DropdownDataGeneric<CopyAfterTranslation> { }
    public List<CopyAfterTranslationData> CopyAfterTranslations { get; } =
        DropdownDataGeneric<CopyAfterTranslation>.GetValues<CopyAfterTranslationData>("CopyAfterTranslation");

    #endregion

    #region BackupTypes

    public class BackupTypeData : DropdownDataGeneric<BackupType> { }
    public List<BackupTypeData> BackupTypes { get; } =
        DropdownDataGeneric<BackupType>.GetValues<BackupTypeData>("BackupType");

    #endregion

    #region ImageQualities

    public class ImageQualityData : DropdownDataGeneric<ImageQuality> { }
    public List<ImageQualityData> ImageQualities { get; } =
        DropdownDataGeneric<ImageQuality>.GetValues<ImageQualityData>("ImageQuality");

    #endregion

    #region DoubleClickTrayFunctions

    public class DoubleClickTrayFunctionData : DropdownDataGeneric<DoubleClickTrayFunction> { }
    public List<DoubleClickTrayFunctionData> DoubleClickTrayFunctions { get; } =
        DropdownDataGeneric<DoubleClickTrayFunction>.GetValues<DoubleClickTrayFunctionData>("DoubleClickTrayFunction");

    #endregion

    #region PluginMarketCdnSources

    public class PluginMarketCdnSourceTypeData : DropdownDataGeneric<PluginMarketCdnSourceType> { }
    public List<PluginMarketCdnSourceTypeData> PluginMarketCdnSources { get; } =
        DropdownDataGeneric<PluginMarketCdnSourceType>.GetValues<PluginMarketCdnSourceTypeData>("PluginMarketCdnSourceType");

    #endregion

    #region PluginDownloadProxies

    public class PluginDownloadProxyTypeData : DropdownDataGeneric<PluginDownloadProxyType> { }
    public List<PluginDownloadProxyTypeData> PluginDownloadProxies { get; } =
        DropdownDataGeneric<PluginDownloadProxyType>.GetValues<PluginDownloadProxyTypeData>("PluginDownloadProxyType");

    #endregion
}
