using iNKORE.UI.WPF.Modern.Controls;
using STranslate.ViewModels;

namespace STranslate.Controls;

public partial class TextPreprocessingDialog : ContentDialog
{
    public TextPreprocessingDialog(TextPreprocessingEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        IsPrimaryButtonEnabled = false;
        try
        {
            if (await ((TextPreprocessingEditViewModel)DataContext).ApplyAsync())
                Hide();
        }
        finally { IsPrimaryButtonEnabled = true; }
    }
}
