using iNKORE.UI.WPF.Modern.Controls;

namespace STranslate.Controls;

public partial class TranslationRequestDialog : ContentDialog
{
    public TranslationRequestDialog(string requestText, string languages = "")
    {
        InitializeComponent();
        DataContext = new { RequestText = requestText, Languages = languages };
    }
}
