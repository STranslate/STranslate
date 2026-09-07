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

    private readonly Internationalization _i18n;

    public TranslateViewModel(TranslateService service, Settings settings,
        TextPreprocessingService textPreprocessingService, Internationalization i18n) : base(service)
    {
        _settings = settings;
        _textPreprocessingService = textPreprocessingService;
        _i18n = i18n;
    }

    [RelayCommand]
    private async Task EditTextPreprocessingAsync(System.Windows.FrameworkElement element)
    {
        var owner = System.Windows.Window.GetWindow(element);
        if (owner is null) return;
        using var draft = new STranslate.ViewModels.TextPreprocessingEditViewModel(
            _settings, _textPreprocessingService, _i18n.GetTranslation);
        var dialog = new STranslate.Controls.TextPreprocessingDialog(draft);
        await dialog.ShowAsync(owner);
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
