using STranslate.Core;

namespace STranslate.ViewModels.Pages;

public partial class HotkeyViewModel(
    HotkeySettings settings,
    Internationalization i18n,
    DataProvider dataProvider
    ) : SearchViewModelBase(i18n, "Hotkey_")
{
    public HotkeySettings HotkeySettings { get; } = settings;

    public DataProvider DataProvider { get; } = dataProvider;

    public Internationalization I18n { get; } = i18n;
}
