using STranslate.Core;
using STranslate.ViewModels;

namespace STranslate.Tests;

public class TextPreprocessingEditorTests
{
    [Fact]
    public async Task DraftChangesDoNotAffectSavedRulesAndDisposeCancelsEditing()
    {
        var settings = CreateSettings();
        var saved = settings.TextPreprocessing;
        using (var editor = new TextPreprocessingEditViewModel(settings, new(), Translate))
        {
            editor.Rules[0].Replacement = "draft";
            editor.IsEnabled = false;
            editor.AddRuleCommand.Execute(null);
            Assert.Same(saved, settings.TextPreprocessing);
            Assert.True(saved.IsEnabled);
            Assert.Equal("public", saved.Rules[0].Replacement);
            await editor.PreviewTask;
        }
        Assert.Single(settings.TextPreprocessing.Rules);
    }

    [Fact]
    public async Task ApplyValidatesEveryRuleAndReplacesSettingsOnce()
    {
        var settings = CreateSettings();
        var changes = 0;
        settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Settings.TextPreprocessing)) changes++; };
        using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
        editor.Rules[0].Replacement = "saved";
        editor.AddRuleCommand.Execute(null);
        editor.SelectedRule!.IsEnabled = false;
        Assert.False(await editor.ApplyAsync());
        Assert.Equal(0, changes);
        editor.SelectedRule.Pattern = "valid";
        Assert.True(await editor.ApplyAsync());
        Assert.Equal(1, changes);
        Assert.Equal("saved", settings.TextPreprocessing.Rules[0].Replacement);
        editor.Rules[0].Replacement = "later draft";
        Assert.Equal("saved", settings.TextPreprocessing.Rules[0].Replacement);
    }

    [Fact]
    public async Task PreviewTracksOrderRemovalAndGlobalSwitchWithoutSaving()
    {
        var settings = CreateSettings();
        using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
        editor.PreviewInput = "secret";
        editor.AddRuleCommand.Execute(null);
        editor.SelectedRule!.Pattern = "public";
        editor.SelectedRule.Replacement = "final";
        await editor.PreviewTask;
        Assert.Equal("final", editor.PreviewText);
        Assert.Equal([1, 1], editor.PreviewMatches.Select(match => match.MatchCount));

        editor.MoveRuleUpCommand.Execute(null);
        await editor.PreviewTask;
        Assert.Equal("public", editor.PreviewText);
        Assert.Equal([0, 1], editor.PreviewMatches.Select(match => match.MatchCount));
        editor.IsEnabled = false;
        await editor.PreviewTask;
        Assert.Equal("secret", editor.PreviewText);
        Assert.Equal(0, editor.PreviewMatchCount);
        Assert.True(settings.TextPreprocessing.IsEnabled);
        editor.IsEnabled = true;
        editor.SelectedRule = editor.Rules[1];
        editor.RemoveRuleCommand.Execute(null);
        await editor.PreviewTask;
        Assert.Equal("secret", editor.PreviewText);
    }

    [Fact]
    public async Task PreviewAndExecutionReportSameFailureCategory()
    {
        var settings = CreateSettings();
        using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
        editor.Rules[0].Pattern = "(";
        await editor.PreviewTask;
        Assert.Contains("TranslatePreprocessingFailed", editor.PreviewError);
        Assert.Contains("Position", editor.PreviewError);
        Assert.Empty(editor.PreviewText);
        Assert.False(await editor.ApplyAsync());
    }

    [Fact]
    public async Task RapidEditsCannotPublishAnObsoletePreview()
    {
        using var editor = new TextPreprocessingEditViewModel(CreateSettings(), new(), Translate);
        editor.PreviewInput = "secret";
        editor.Rules[0].Replacement = "old";
        var oldPreview = editor.PreviewTask;
        editor.Rules[0].Replacement = "new";
        await Task.WhenAll(oldPreview, editor.PreviewTask);
        Assert.Equal("new", editor.PreviewText);
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
