using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STranslate.Core;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace STranslate.ViewModels;

public partial class TextPreprocessingEditViewModel : ObservableObject, IDisposable
{
    private readonly Settings _settings;
    private readonly TextPreprocessingService _service;
    private readonly Func<string, string> _translate;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public TextPreprocessingEditViewModel(Settings settings, TextPreprocessingService service, Func<string, string> translate)
    {
        _settings = settings;
        _service = service;
        _translate = translate;
        IsEnabled = settings.TextPreprocessing?.IsEnabled ?? false;
        foreach (var rule in settings.TextPreprocessing?.Rules ?? [])
            if (rule is not null) Rules.Add(rule.Clone());
        SelectedRule = Rules.FirstOrDefault();
        foreach (var rule in Rules) rule.PropertyChanged += OnRuleChanged;
        Rules.CollectionChanged += OnRulesChanged;
        if (settings.TextPreprocessing?.InvalidConfiguration is not null)
            Error = _translate("TranslatePreprocessingInvalidConfiguration");
    }

    public ObservableCollection<TextReplacementRule> Rules { get; } = [];
    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial TextReplacementRule? SelectedRule { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBusy))] public partial bool IsSaving { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBusy))] public partial bool IsTesting { get; set; }
    public bool IsBusy => IsSaving || IsTesting;
    [ObservableProperty] public partial string TestText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? TestRequestText { get; set; }
    [ObservableProperty] public partial string TestSummary { get; set; } = string.Empty;
    private int _previewVersion;

    partial void OnTestTextChanged(string value) => ClearPreview();
    partial void OnIsEnabledChanged(bool value) => ClearPreview();

    private void ClearPreview()
    {
        _previewVersion++;
        TestRequestText = null;
        TestSummary = string.Empty;
    }

    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e) => ClearPreview();

    private void OnRulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (TextReplacementRule rule in e.OldItems) rule.PropertyChanged -= OnRuleChanged;
        if (e.NewItems is not null)
            foreach (TextReplacementRule rule in e.NewItems) { rule.PropertyChanged -= OnRuleChanged; rule.PropertyChanged += OnRuleChanged; }
        ClearPreview();
    }

    private TextPreprocessingSettings CaptureDraft() => new()
    {
        IsEnabled = IsEnabled,
        Rules = Rules.Select(rule => rule.Clone()).ToList()
    };

    public async Task TestAsync()
    {
        if (_disposed || IsBusy) return;
        IsTesting = true;
        Error = string.Empty;
        ClearPreview();
        var version = _previewVersion;
        var text = TestText;
        try
        {
            var prepared = await _service.PrepareAsync(text, CaptureDraft(), _lifetime.Token);
            if (_disposed || version != _previewVersion) return;
            TestRequestText = prepared.RequestText;
            TestSummary = string.IsNullOrWhiteSpace(prepared.RequestText)
                ? _translate("TranslatePreprocessingEmpty")
                : string.Format(_translate("General_TextPreprocessingTestSummary"), prepared.AppliedRules.Count);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (TextPreprocessingException ex) { ShowError(ex); }
        finally { IsTesting = false; }
    }

    private void ShowError(TextPreprocessingException ex)
    {
        Error = TextPreprocessingErrorText.Format(ex, _translate);
        SelectedRule = Rules.FirstOrDefault(rule => rule.Id == ex.RuleId) ?? SelectedRule;
    }

    [RelayCommand]
    private void AddRule()
    {
        var number = 1;
        while (Rules.Any(rule => rule.Name == string.Format(_translate("Translate_TextPreprocessingNewRule"), number))) number++;
        var rule = new TextReplacementRule
        {
            Name = string.Format(_translate("Translate_TextPreprocessingNewRule"), number),
            Mode = TextReplacementMode.PlainText
        };
        Rules.Add(rule);
        SelectedRule = rule;
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (SelectedRule is not { } rule) return;
        var index = Rules.IndexOf(rule);
        Rules.Remove(rule);
        SelectedRule = Rules.ElementAtOrDefault(Math.Min(index, Rules.Count - 1));
    }

    [RelayCommand] private void MoveRuleUp() => MoveRule(-1);
    [RelayCommand] private void MoveRuleDown() => MoveRule(1);

    private void MoveRule(int offset)
    {
        var index = SelectedRule is null ? -1 : Rules.IndexOf(SelectedRule);
        if (index >= 0 && index + offset >= 0 && index + offset < Rules.Count)
            Rules.Move(index, index + offset);
    }

    public async Task<bool> ApplyAsync()
    {
        if (_disposed || IsBusy) return false;
        IsSaving = true;
        Error = string.Empty;
        var configuration = CaptureDraft();
        try
        {
            await Task.Run(() => _service.Validate(configuration), _lifetime.Token);
            if (_disposed) return false;
            _settings.TextPreprocessing = configuration;
            return true;
        }
        catch (OperationCanceledException) when (_disposed) { return false; }
        catch (TextPreprocessingException ex)
        {
            ShowError(ex);
            return false;
        }
        finally { IsSaving = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Rules.CollectionChanged -= OnRulesChanged;
        foreach (var rule in Rules) rule.PropertyChanged -= OnRuleChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
