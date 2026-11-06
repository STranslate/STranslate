using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STranslate.Core;
using System.Collections.ObjectModel;

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
    }

    public ObservableCollection<TextReplacementRule> Rules { get; } = [];
    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial TextReplacementRule? SelectedRule { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsSaving { get; set; }

    [RelayCommand]
    private void AddRule()
    {
        var rule = new TextReplacementRule
        {
            Name = string.Format(_translate("Translate_TextPreprocessingNewRule"), Rules.Count + 1),
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
        if (_disposed || IsSaving) return false;
        IsSaving = true;
        Error = string.Empty;
        var configuration = new TextPreprocessingSettings
        {
            IsEnabled = IsEnabled,
            Rules = Rules.Select(rule => rule.Clone()).ToList()
        };
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
            Error = TextPreprocessingErrorText.Format(ex, _translate);
            SelectedRule = Rules.FirstOrDefault(rule => rule.Id == ex.RuleId) ?? SelectedRule;
            return false;
        }
        finally { IsSaving = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
