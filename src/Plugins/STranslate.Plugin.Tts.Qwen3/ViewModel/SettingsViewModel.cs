using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;

namespace STranslate.Plugin.Tts.Qwen3.ViewModel;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IPluginContext _context;
    private readonly Settings _settings;

    [ObservableProperty]
    public partial string BaseUrl { get; set; }

    [ObservableProperty]
    public partial string Speaker { get; set; }

    [ObservableProperty]
    public partial string Language { get; set; }

    [ObservableProperty]
    public partial string Instruct { get; set; }

    [ObservableProperty]
    public partial int TimeoutSeconds { get; set; }

    public SettingsViewModel(IPluginContext context, Settings settings)
    {
        _context = context;
        _settings = settings;

        BaseUrl = settings.BaseUrl;
        Speaker = settings.Speaker;
        Language = settings.Language;
        Instruct = settings.Instruct;
        TimeoutSeconds = settings.TimeoutSeconds;

        PropertyChanged += OnPropertyChanged;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BaseUrl):
                _settings.BaseUrl = BaseUrl;
                break;
            case nameof(Speaker):
                _settings.Speaker = Speaker;
                break;
            case nameof(Language):
                _settings.Language = Language;
                break;
            case nameof(Instruct):
                _settings.Instruct = Instruct;
                break;
            case nameof(TimeoutSeconds):
                _settings.TimeoutSeconds = TimeoutSeconds;
                break;
        }

        _context.SaveSettingStorage<Settings>();
    }

    public void Dispose()
    {
        PropertyChanged -= OnPropertyChanged;
    }
}
