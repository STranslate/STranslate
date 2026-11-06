using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace STranslate.Plugin.Translate.OpenAI.ViewModel;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IPluginContext _context;
    private readonly Settings _settings;
    private bool _isUpdating = false;
    public Main Main { get; }

    public SettingsViewModel(IPluginContext context, Settings settings, Main main)
    {
        _context = context;
        _settings = settings;
        Main = main;

        ApiMode = _settings.ApiMode;
        Url = _settings.Url;
        ApiKey = _settings.ApiKey;
        Model = _settings.Model;
        Models = new ObservableCollection<string>(_settings.Models);
        Temperature = _settings.Temperature;
        SendTemperature = _settings.SendTemperature;
        AdditionalParametersJson = _settings.AdditionalParametersJson;

        PropertyChanged += OnPropertyChanged;
        Models.CollectionChanged += OnModelsCollectionChanged;
    }

    private void OnModelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or
                       NotifyCollectionChangedAction.Remove or
                       NotifyCollectionChangedAction.Replace)
        {
            _settings.Models = [.. Models];
            _context.SaveSettingStorage<Settings>();
        }
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ApiMode):
                _settings.ApiMode = ApiMode;
                break;
            case nameof(ApiKey):
                _settings.ApiKey = ApiKey;
                break;
            case nameof(Url):
                _settings.Url = Url;
                break;
            case nameof(Model):
                _settings.Model = Model ?? string.Empty;
                break;
            case nameof(SendTemperature):
                _settings.SendTemperature = SendTemperature;
                break;
            case nameof(Temperature):
                // 舍入到一位小数，避免浮点精度问题
                _settings.Temperature = Math.Round(Temperature, 1);
                break;
            case nameof(AdditionalParametersJson):
                _settings.AdditionalParametersJson = AdditionalParametersJson;
                break;
            default:
                return;
        }
        _context.SaveSettingStorage<Settings>();
    }

    [ObservableProperty] public partial string ValidateResult { get; set; } = string.Empty;
    [ObservableProperty] public partial OpenAIApiMode ApiMode { get; set; }
    [ObservableProperty] public partial string Url { get; set; }
    [ObservableProperty] public partial string ApiKey { get; set; }
    [ObservableProperty] public partial string? Model { get; set; }
    [ObservableProperty] public partial ObservableCollection<string> Models { get; set; }
    [ObservableProperty] public partial double Temperature { get; set; }
    [ObservableProperty] public partial bool SendTemperature { get; set; }
    [ObservableProperty] public partial string AdditionalParametersJson { get; set; }

    public string FinalUrl => OpenAIProtocol.BuildFinalUrl(Url, ApiMode);

    partial void OnApiModeChanged(OpenAIApiMode value) => OnPropertyChanged(nameof(FinalUrl));

    partial void OnUrlChanged(string value) => OnPropertyChanged(nameof(FinalUrl));

    [RelayCommand]
    private void AddModel(string model)
    {
        if (_isUpdating || string.IsNullOrWhiteSpace(model) || Models.Contains(model))
            return;

        using var _ = new UpdateGuard(this);

        Models.Add(model);
        Model = model;
    }

    [RelayCommand]
    private void DeleteModel(string model)
    {
        if (_isUpdating || !Models.Contains(model))
            return;

        using var _ = new UpdateGuard(this);

        if (Model == model)
            Model = Models.Count > 1 ? Models.First(m => m != model) : string.Empty;

        Models.Remove(model);
    }

    [RelayCommand]
    private void EditPrompt()
    {
        var dialog = _context.GetPromptEditWindow(Main.Prompts);

        if (dialog.ShowDialog() == true)
        {
            // 保存更新后的 Prompts
            _settings.Prompts = [.. Main.Prompts.Select(p => p.Clone())];
            _context.SaveSettingStorage<Settings>();

            // 更新选中项
            Main.SelectedPrompt = Main.Prompts.FirstOrDefault(p => p.IsEnabled);
        }
    }

    [RelayCommand]
    public async Task ValidateAsync()
    {
        try
        {
            await Main.ValidateApiAsync();

            ValidateResult = _context.GetTranslation("ValidationSuccess");
        }
        catch (Exception ex)
        {
            ValidateResult = _context.GetTranslation("ValidationFailure");
            _context.Logger.LogError(ex, _context.GetTranslation("ValidationFailure"));
        }
    }


    public void Dispose()
    {
        PropertyChanged -= OnPropertyChanged;
        Models.CollectionChanged -= OnModelsCollectionChanged;
    }

    // 辅助类和记录
    private readonly struct UpdateGuard : IDisposable
    {
        private readonly SettingsViewModel _viewModel;

        public UpdateGuard(SettingsViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel._isUpdating = true;
        }

        public void Dispose() => _viewModel._isUpdating = false;
    }
}
