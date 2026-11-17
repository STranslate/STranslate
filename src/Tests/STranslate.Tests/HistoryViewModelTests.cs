using STranslate.Core;
using STranslate.ViewModels.Pages;
using System.IO;

namespace STranslate.Tests;

public class HistoryViewModelTests
{
    [Fact]
    public async Task RefreshKeepsTheKeywordAndRequeriesMatchingRecords()
    {
        await using var db = await HistoryDatabase.CreateAsync("needle old", "unrelated");
        // 查询路径不使用通知或语言资源。
        using var model = new HistoryViewModel(db.Sql, null!, null!);
        model.SearchText = "needle";
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("needle old", Assert.Single(model.HistoryItems).SourceText);
        model.SelectedListItem = model.HistoryItems.First();
        model.SelectedItems.Add(model.HistoryItems.First());

        await db.AddAsync("needle new");
        await db.AddAsync("another unrelated");
        await model.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("needle", model.SearchText);
        Assert.Equal(2, model.HistoryItems.Count());
        Assert.All(model.HistoryItems, item => Assert.Contains("needle", item.SourceText));
        Assert.Equal(4, model.TotalCount);
        Assert.Null(model.SelectedItem);
        Assert.Empty(model.SelectedItems);
        Assert.False(model.LoadMoreCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("missing", "unrelated", 0)]
    [InlineData("O'Reilly", "O'Reilly guide", 1)]
    [InlineData("x' OR 1=1 --", "unrelated", 0)]
    public async Task RefreshDoesNotRestoreUnfilteredRowsForEmptyOrQuotedSearchResults(
        string query, string text, int expectedCount)
    {
        await using var db = await HistoryDatabase.CreateAsync(text, "other text");
        using var model = new HistoryViewModel(db.Sql, null!, null!);
        model.SearchText = query;
        await model.RefreshCommand.ExecuteAsync(null);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(query, model.SearchText);
        Assert.Equal(expectedCount, model.HistoryItems.Count());
        Assert.False(model.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClearingTheFilterRestoresNormalPagingWithoutMixingSearchResults()
    {
        await using var db = await HistoryDatabase.CreateAsync(
            Enumerable.Range(0, 45).Select(index => (index < 25 ? "needle " : "other ") + index).ToArray());
        using var model = new HistoryViewModel(db.Sql, null!, null!);
        model.SearchText = "needle";
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(25, model.HistoryItems.Count());
        Assert.False(model.LoadMoreCommand.CanExecute(null));
        await model.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(25, model.HistoryItems.Count());
        Assert.All(model.HistoryItems, item => Assert.Contains("needle", item.SourceText));

        model.SearchText = "";
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(20, model.HistoryItems.Count());
        Assert.True(model.LoadMoreCommand.CanExecute(null));
        await model.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(40, model.HistoryItems.Count());
        await model.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(45, model.HistoryItems.Count());
        Assert.Equal(45, model.HistoryItems.Select(item => item.Id).Distinct().Count());
        Assert.False(model.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshCancelsPendingDebouncedSearchAndKeepsTheLatestKeyword()
    {
        await using var db = await HistoryDatabase.CreateAsync("first", "latest", "other");
        using var model = new HistoryViewModel(db.Sql, null!, null!);
        model.SearchText = "first";
        model.SearchText = "latest";
        await model.RefreshCommand.ExecuteAsync(null);
        var extraPublications = 0;
        model.HistoryItems.CollectionChanged += (_, _) => extraPublications++;
        await Task.Delay(650);
        Assert.Equal("latest", model.SearchText);
        Assert.Equal("latest", Assert.Single(model.HistoryItems).SourceText);
        Assert.Equal(0, extraPublications);
    }

    private sealed class HistoryDatabase(string directory, SqlService sql) : IAsyncDisposable
    {
        private int _index;
        internal SqlService Sql { get; } = sql;
        internal static async Task<HistoryDatabase> CreateAsync(params string[] texts)
        {
            var directory = Path.Combine(Path.GetTempPath(), "STranslate-history-search-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var sql = new SqlService($"Data Source={Path.Combine(directory, "history.db")};Pooling=False");
            await sql.InitializeDBAsync();
            var database = new HistoryDatabase(directory, sql);
            foreach (var text in texts) await database.AddAsync(text);
            return database;
        }
        internal Task AddAsync(string text) => Sql.InsertOrUpdateDataAsync(new HistoryModel
        {
            SourceText = text, SourceLang = "English", TargetLang = "ChineseSimplified",
            Time = DateTime.Now.AddMinutes(-++_index)
        }, 1000);
        public ValueTask DisposeAsync()
        {
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
            return ValueTask.CompletedTask;
        }
    }
}
