using STranslate.Core;
using STranslate.ViewModels;

namespace STranslate.Tests;

public class TextPreprocessingEditorTests
{
    [Fact]
    public void CancelKeepsSavedRulesAndNewRulesDefaultToPlainText()
    {
        var settings = CreateSettings();
        using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
        editor.Rules[0].Replacement = "draft";
        editor.AddRuleCommand.Execute(null);
        Assert.Equal(TextReplacementMode.PlainText, editor.SelectedRule!.Mode);
        Assert.Single(settings.TextPreprocessing.Rules);
        Assert.Equal("public", settings.TextPreprocessing.Rules[0].Replacement);
    }

    [Fact]
    public async Task SaveValidatesRegexAndPublishesOneIndependentConfiguration()
    {
        var settings = CreateSettings();
        var changes = 0;
        settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Settings.TextPreprocessing)) changes++; };
        using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
        editor.Rules[0].Pattern = "(";
        Assert.False(await editor.ApplyAsync());
        Assert.Contains("TranslatePreprocessingFailed", editor.Error);
        Assert.Equal(0, changes);
        editor.Rules[0].Mode = TextReplacementMode.PlainText;
        Assert.True(await editor.ApplyAsync());
        Assert.Equal(1, changes);
        editor.Rules[0].Replacement = "later draft";
        Assert.Equal("public", settings.TextPreprocessing.Rules[0].Replacement);
    }

    [Fact]
    public async Task ReorderAndRemoveAreSavedInExecutionOrder()
    {
        var settings = CreateSettings();
        using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
        editor.AddRuleCommand.Execute(null);
        editor.SelectedRule!.Pattern = "public";
        editor.SelectedRule.Replacement = "final";
        editor.MoveRuleUpCommand.Execute(null);
        Assert.True(await editor.ApplyAsync());
        Assert.Equal("public", new TextPreprocessingService().Prepare("secret", settings.TextPreprocessing).RequestText);
        editor.RemoveRuleCommand.Execute(null);
        Assert.True(await editor.ApplyAsync());
        Assert.Single(settings.TextPreprocessing.Rules);
    }

    [Fact]
    public async Task GlobalSwitchCanDisableAnInvalidRule()
    {
        var settings = CreateSettings();
        using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
        editor.Rules[0].Pattern = "(";
        editor.IsEnabled = false;
        Assert.True(await editor.ApplyAsync());
        Assert.Equal("secret", new TextPreprocessingService().Prepare("secret", settings.TextPreprocessing).RequestText);
    }

    private static Settings CreateSettings() => new()
    {
        TextPreprocessing = new() { IsEnabled = true, Rules = [new() { Pattern = "secret", Replacement = "public" }] }
    };

    private static string Translate(string key) => key switch
    {
        "Translate_TextPreprocessingNewRule" => "Rule {0}",
        "Translate_TextPreprocessingErrorPosition" => "Position {0}",
        _ => key
    };
}
