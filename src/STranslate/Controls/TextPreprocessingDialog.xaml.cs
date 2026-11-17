using iNKORE.UI.WPF.Modern.Controls;
using STranslate.ViewModels;

namespace STranslate.Controls;

public partial class TextPreprocessingDialog : ContentDialog
{
    public TextPreprocessingDialog(TextPreprocessingEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.SelectedRule) ||
                e.PropertyName == nameof(viewModel.Error) && !string.IsNullOrEmpty(viewModel.Error))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (viewModel.SelectedRule is not { } rule) return;
                    RulesGrid.ScrollIntoView(rule);
                    RulesGrid.CurrentCell = new(rule, RulesGrid.Columns[3]);
                });
            }
        };
    }

    private void OnRuleSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RulesGrid.SelectedItem is { } rule) RulesGrid.ScrollIntoView(rule);
    }

    private async void OnTestClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        RulesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        RulesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        await ((TextPreprocessingEditViewModel)DataContext).TestAsync();
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        IsPrimaryButtonEnabled = false;
        try
        {
            RulesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            RulesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            if (await ((TextPreprocessingEditViewModel)DataContext).ApplyAsync())
                Hide();
        }
        finally { IsPrimaryButtonEnabled = true; }
    }
}
