using STranslate.ViewModels.Pages;
using System.Windows.Controls;

namespace STranslate.Views.Pages;

public partial class AboutPage
{
    public AboutPage(AboutViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;

        InitializeComponent();
    }

    public AboutViewModel ViewModel { get; }

    private void S3ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 仅在用户打开下拉框并主动切换时应用预设，避免绑定初始化或重复打开时覆盖已保存配置。
        if (sender is ComboBox { IsDropDownOpen: true })
            ViewModel.Settings.Backup.ApplyS3ProviderPreset();
    }
}