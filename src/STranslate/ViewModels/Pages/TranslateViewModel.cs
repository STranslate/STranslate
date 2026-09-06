using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using STranslate.Core;
using STranslate.Services;
using STranslate.Plugin;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace STranslate.ViewModels.Pages;

public partial class TranslateViewModel : ServiceViewModelBase<TranslateService>
{
    private readonly Settings _settings;
    private readonly TextPreprocessingService _textPreprocessingService;

    public TranslateViewModel(
        TranslateService service,
        Settings settings,
        TextPreprocessingService textPreprocessingService)
        : base(service)
    {
        _settings = settings;
        _textPreprocessingService = textPreprocessingService;
        Rules = new ObservableCollection<TextReplacementRule>(
            settings.TextPreprocessing.Rules?.Select(rule => rule.Clone()).ToList() ?? []);
        SubscribeToRules(Rules);
        UpdatePreview();
    }

    public ObservableCollection<TextReplacementRule> Rules { get; }

    [ObservableProperty]
    public partial string PreviewInput { get; set; } = "Please review ACME-123 today.";

    [ObservableProperty]
    public partial string PreviewText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PreviewError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int PreviewMatchCount { get; set; }

    partial void OnPreviewInputChanged(string value) => UpdatePreview();

    public bool IsTextPreprocessingEnabled
    {
        get => _settings.TextPreprocessing.IsEnabled;
        set
        {
            if (value == IsTextPreprocessingEnabled)
                return;

            var configuration = CreateConfiguration();
            configuration.IsEnabled = value;
            _settings.TextPreprocessing = configuration;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void AddTextReplacementRule()
    {
        var rule = new TextReplacementRule
        {
            Name = $"Rule {Rules.Count + 1}",
            Pattern = string.Empty,
            Replacement = string.Empty
        };
        Rules.Add(rule);
        SubscribeToRule(rule);
        PersistRules();
    }

    [RelayCommand]
    private void RemoveTextReplacementRule(TextReplacementRule? rule)
    {
        if (rule is null || !Rules.Remove(rule))
            return;

        rule.PropertyChanged -= OnRulePropertyChanged;
        PersistRules();
    }

    [RelayCommand]
    private void MoveTextReplacementRuleUp(TextReplacementRule? rule)
        => MoveRule(rule, -1);

    [RelayCommand]
    private void MoveTextReplacementRuleDown(TextReplacementRule? rule)
        => MoveRule(rule, 1);

    private void MoveRule(TextReplacementRule? rule, int offset)
    {
        if (rule is null)
            return;

        var currentIndex = Rules.IndexOf(rule);
        var targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= Rules.Count)
            return;

        Rules.Move(currentIndex, targetIndex);
        PersistRules();
    }

    private void SubscribeToRules(IEnumerable<TextReplacementRule> rules)
    {
        foreach (var rule in rules)
            SubscribeToRule(rule);
    }

    private void SubscribeToRule(TextReplacementRule rule)
        => rule.PropertyChanged += OnRulePropertyChanged;

    private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        PersistRules();
        UpdatePreview();
    }

    private void PersistRules()
    {
        var configuration = CreateConfiguration();
        configuration.IsEnabled = IsTextPreprocessingEnabled;
        _settings.TextPreprocessing = configuration;
    }

    private TextPreprocessingSettings CreateConfiguration()
    {
        return new TextPreprocessingSettings
        {
            IsEnabled = _settings.TextPreprocessing.IsEnabled,
            Rules = Rules.Select(rule => rule.Clone()).ToList()
        };
    }

    private void UpdatePreview()
    {
        try
        {
            var prepared = _textPreprocessingService.Prepare(
                PreviewInput ?? string.Empty,
                CreateConfiguration());
            PreviewText = prepared.RequestText;
            PreviewMatchCount = prepared.TotalMatchCount;
            PreviewError = string.Empty;
        }
        catch (TextPreprocessingException ex)
        {
            PreviewText = string.Empty;
            PreviewMatchCount = 0;
            PreviewError = ex.Message;
        }
    }

    // 翻译服务特有的功能
    [RelayCommand]
    private void ActiveReplace(Service svc) => Service.ActiveReplace(svc);

    [RelayCommand]
    private void DeactiveReplace() => Service.DeactiveReplace();

    [RelayCommand]
    private void ActiveImTran(Service svc) => Service.ActiveImTran(svc);

    [RelayCommand]
    private void DeactiveImTran() => Service.DeactiveImTran();

    [RelayCommand]
    private void ReorderEnabledServices()
    {
        var services = Service.Services;
        if (services.Count <= 1)
            return;

        var targetOrder = new List<Service>(services.Count);
        foreach (var svc in services)
        {
            if (svc.IsEnabled)
                targetOrder.Add(svc);
        }

        foreach (var svc in services)
        {
            if (!svc.IsEnabled)
                targetOrder.Add(svc);
        }

        // 按目标顺序执行 Move，复用现有顺序持久化逻辑，且保持组内相对顺序稳定。
        for (var targetIndex = 0; targetIndex < targetOrder.Count; targetIndex++)
        {
            var service = targetOrder[targetIndex];
            var currentIndex = services.IndexOf(service);
            if (currentIndex >= 0 && currentIndex != targetIndex)
                services.Move(currentIndex, targetIndex);
        }
    }
}
