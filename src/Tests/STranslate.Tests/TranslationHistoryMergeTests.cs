using STranslate.Core;
using STranslate.Plugin;
using STranslate.Services;
using STranslate.ViewModels;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace STranslate.Tests;

public class TranslationHistoryMergeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticEntryKeepsFailedServiceHistoryWithAndWithoutCacheReuse(bool force)
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.Sql.InsertOrUpdateDataAsync(Model(Primary("A", "old"), Primary("B", "old")), 10);
        var a = CreateService("A", new FlowPlugin("A"));
        var b = CreateService("B", new FlowPlugin("B", fail: true));
        var flow = new TranslationFlow(db.Sql, a, b);
        await flow.InvokeAsync("ExecuteAutomaticTranslationAsync", force ? "force" : null, CancellationToken.None);
        var stored = (await db.ReadAsync())!;
        Assert.Equal("new", stored.Data.Single(data => data.ServiceID == "A").RequestText);
        Assert.Equal("old", stored.Data.Single(data => data.ServiceID == "B").RequestText);
        Assert.Equal("new", Assert.Single(((FlowPlugin)a.Plugin).Requests).Text);
        Assert.Equal("new", Assert.Single(((FlowPlugin)b.Plugin).Requests).Text);
    }

    [Fact]
    public async Task AutomaticEntryPreservesAConcurrentManualWriteAndBothInputRequestSnapshots()
    {
        await using var db = await TestDatabase.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = CreateService("A", new FlowPlugin("A", started: started, release: release));
        var c = CreateService("C", new FlowPlugin("C"));
        c.Options!.ExecMode = ExecutionMode.Manual;
        var flow = new TranslationFlow(db.Sql, a, c);
        var automatic = flow.InvokeAsync("ExecuteAutomaticTranslationAsync", null, CancellationToken.None);
        await started.Task;
        flow.Settings.TextPreprocessing = Rules("manual");
        var manual = flow.Coordinator.BeginOperation("secret", LangEnum.English, LangEnum.ChineseSimplified, CancellationToken.None);
        try
        {
            await flow.InvokeAsync("ExecuteSingleTranslateAsync", c, manual);
            Assert.Equal("manual", Assert.Single((await db.ReadAsync())!.Data).RequestText);
        }
        finally { release.SetResult(); }
        await automatic;
        var stored = (await db.ReadAsync())!;
        Assert.Equal("new", stored.Data.Single(data => data.ServiceID == "A").RequestText);
        Assert.Equal("manual", stored.Data.Single(data => data.ServiceID == "C").RequestText);
        Assert.Equal([new("new", "A"), new TextReplacementRequest("manual", "C")],
            flow.Coordinator.GetPreparedRequests([a, c], "secret"));
    }

    [Fact]
    public async Task ManualBackEntrySavesTheBoundMainBeforeItsAutomaticBatchAndKeepsThePair()
    {
        await using var db = await TestDatabase.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = CreateService("A", new FlowPlugin("A"));
        var b = CreateService("B", new FlowPlugin("B", started: started, release: release));
        var flow = new TranslationFlow(db.Sql, a, b);
        var automatic = flow.InvokeAsync("ExecuteAutomaticTranslationAsync", null, CancellationToken.None);
        await started.Task;
        var mainContext = flow.Coordinator.GetResultContext(((ITranslatePlugin)a.Plugin).TransResult)!;
        var back = flow.Coordinator.BeginBackOperation(mainContext, CancellationToken.None);
        try
        {
            await flow.InvokeAsync("ExecuteSingleTransBackAsync", a, back, mainContext);
            Assert.True(Assert.Single((await db.ReadAsync())!.Data).HasSuccessfulBackResult);
        }
        finally { release.SetResult(); }
        await automatic;
        Assert.True((await db.ReadAsync())!.Data.Single(data => data.ServiceID == "A").HasSuccessfulBackResult);
        Assert.Equal(["new", mainContext.TranslatedText], ((FlowPlugin)a.Plugin).Requests.Select(request => request.Text));
    }

    [Fact]
    public async Task MainLanguageResolutionUsesTheTargetPreferenceCapturedBeforePreparation()
    {
        await using var db = await TestDatabase.CreateAsync();
        var flow = new TranslationFlow(db.Sql);
        flow.Settings.FirstLanguage = LangEnum.ChineseSimplified;
        var operation = flow.Coordinator.BeginOperation("secret", LangEnum.English, LangEnum.Auto, CancellationToken.None);
        await operation.PrepareInputAsync();
        flow.Settings.FirstLanguage = LangEnum.Japanese;
        var execution = flow.InvokeAsync("ResolveTranslationLanguageContextAsync", operation, null);
        await execution;
        var context = execution.GetType().GetProperty("Result")!.GetValue(execution)!;
        Assert.Equal(LangEnum.ChineseSimplified, context.GetType().GetProperty("EffectiveTarget")!.GetValue(context));
    }

    [Fact]
    public async Task LateAutomaticBatchPreservesManualServiceFailedServicesAndUserMetadata()
    {
        await using var db = await TestDatabase.CreateAsync();
        var original = Model(Primary("A", "old"), Primary("B", "old"));
        original.Favorite = true;
        original.Remark = "保留备注";
        await db.Sql.InsertOrUpdateDataAsync(original, 10);
        var staleBatch = await db.ReadAsync();
        await db.MergeAsync(Model(Primary("C", "manual")));
        // 自动批次仅提交本次成功的 A，不带回其读取时的 B、C 快照。
        staleBatch!.Data = [Primary("A", "automatic")];
        await db.MergeAsync(staleBatch);
        var stored = (await db.ReadAsync())!;
        Assert.Equal(["A", "B", "C"], stored.Data.Select(data => data.ServiceID));
        Assert.Equal(["automatic", "old", "manual"], stored.Data.Select(data => data.RequestText));
        Assert.True(stored.Favorite);
        Assert.Equal("保留备注", stored.Remark);
    }

    [Fact]
    public async Task ConcurrentServicesMergeIntoOneHistoryWithoutLosingAnyRequestSnapshot()
    {
        await using var db = await TestDatabase.CreateAsync();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            db.MergeAsync(Model(Primary(index.ToString(), "request " + index))))));
        var stored = (await db.ReadAsync())!;
        Assert.Equal(12, stored.Data.Count);
        Assert.Equal(12, stored.Data.Select(data => data.ServiceID).Distinct().Count());
        Assert.All(stored.Data, data => Assert.Equal("request " + data.ServiceID, data.RequestText));
    }

    [Fact]
    public async Task SupersededServiceRollsBackWhileTheOtherServiceStillCommits()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.MergeAsync(Model(Primary("A", "old"), Primary("B", "old")));
        var checks = 0;
        await db.Sql.MergeTranslationDataAsync(Model(Primary("A", "stale"), Primary("B", "new")), 10,
            data => data.ServiceID != "A" || Interlocked.Increment(ref checks) == 1);
        var stored = (await db.ReadAsync())!;
        Assert.Equal("old", stored.Data.Single(data => data.ServiceID == "A").RequestText);
        Assert.Equal("new", stored.Data.Single(data => data.ServiceID == "B").RequestText);
    }

    [Fact]
    public async Task CancellationAtCommitRollsBackTheEntireUpdate()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.MergeAsync(Model(Primary("A", "old")));
        using var cancellation = new CancellationTokenSource();
        var checks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Sql.MergeTranslationDataAsync(
            Model(Primary("A", "new")), 10, _ =>
            {
                if (++checks == 2) cancellation.Cancel();
                return true;
            }, cancellation.Token));
        Assert.Equal("old", Assert.Single((await db.ReadAsync())!.Data).RequestText);
    }

    [Fact]
    public async Task FailedAndEmptyPrimaryResultsCannotReplaceSuccessfulHistory()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.MergeAsync(Model(Primary("A", "old"), Primary("B", "old")));
        var failed = Primary("A", "new");
        failed.TransResult!.IsSuccess = false;
        var empty = Primary("B", "new");
        empty.TransResult!.Text = "";
        await db.MergeAsync(Model(failed, empty));
        Assert.All((await db.ReadAsync())!.Data, data => Assert.Equal("old", data.RequestText));
    }

    [Fact]
    public async Task UpdatingAtTheHistoryLimitDoesNotEvictAnotherRecord()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.MergeAsync(Model(Primary("A", "first")), 2);
        var another = Model(Primary("B", "second"));
        another.SourceText = "another";
        await db.MergeAsync(another, 2);
        await db.MergeAsync(Model(Primary("A", "updated")), 2);
        Assert.Equal("updated", Assert.Single((await db.ReadAsync())!.Data).RequestText);
        Assert.NotNull(await db.Sql.GetDataAsync("another", "English", "ChineseSimplified"));
    }

    [Fact]
    public async Task BackTranslationSavedBeforeItsBatchSurvivesTheLaterIdenticalMain()
    {
        await using var db = await TestDatabase.CreateAsync();
        var pair = Primary("A", "same");
        pair.TransBackResult = new() { Text = "back" };
        pair.BackTranslationSourceText = pair.TransResult!.Text;
        await db.MergeAsync(Model(pair));
        await db.MergeAsync(Model(Primary("A", "same")));
        var stored = Assert.Single((await db.ReadAsync())!.Data);
        Assert.True(stored.HasSuccessfulBackResult);
        Assert.Equal("back", stored.TransBackResult!.Text);
    }

    [Fact]
    public async Task BackOnlyUpdateCannotCreateAnOrphanOrAttachToADifferentMain()
    {
        await using var db = await TestDatabase.CreateAsync();
        var back = Primary("A", "old");
        back.BackTranslationSourceText = back.TransResult!.Text;
        back.TransResult = null;
        back.TransBackResult = new() { Text = "back" };
        await db.MergeAsync(Model(back));
        Assert.Null(await db.ReadAsync());
        await db.MergeAsync(Model(Primary("A", "new")));
        await db.MergeAsync(Model(back));
        var stored = Assert.Single((await db.ReadAsync())!.Data);
        Assert.Equal("new", stored.RequestText);
        Assert.Null(stored.TransBackResult);
    }

    [Fact]
    public async Task LegacyServiceKeepsItsLanguagesWhenAnotherServiceUsesNewRulesAndLanguages()
    {
        await using var db = await TestDatabase.CreateAsync();
        var legacy = Primary("A", "old");
        legacy.RequestText = legacy.PreprocessingFingerprint = null;
        legacy.EffectiveSourceLang = legacy.EffectiveTargetLang = null;
        var original = Model(legacy);
        original.EffectiveSourceLang = "English";
        original.EffectiveTargetLang = "ChineseSimplified";
        await db.Sql.InsertOrUpdateDataAsync(original, 10);
        var incoming = Primary("B", "new");
        incoming.EffectiveSourceLang = "French";
        incoming.EffectiveTargetLang = "Japanese";
        await db.MergeAsync(Model(incoming));
        var stored = (await db.ReadAsync())!;
        Assert.Equal("English", stored.Data[0].EffectiveSourceLang);
        Assert.Equal("secret", stored.Data[0].RequestText);
        Assert.Equal("French", stored.Data[1].EffectiveSourceLang);
        Assert.Equal("French", stored.EffectiveSourceLang);
    }

    private static HistoryData Primary(string service, string request) => new()
    {
        PluginID = "test", ServiceID = service, ServiceDisplayName = service,
        RequestText = request, PreprocessingFingerprint = "rules-" + request,
        EffectiveSourceLang = "English", EffectiveTargetLang = "ChineseSimplified",
        TransResult = new() { Text = "translation " + request }
    };

    private static HistoryModel Model(params HistoryData[] data) => new()
    {
        SourceText = "secret", SourceLang = "English", TargetLang = "ChineseSimplified",
        Time = DateTime.Now, Data = data.ToList()
    };

    private static TextPreprocessingSettings Rules(string replacement) => new()
    { IsEnabled = true, Rules = [new() { Pattern = "secret", Replacement = replacement }] };

    private static Service CreateService(string name, ITranslatePlugin plugin) => new()
    { ServiceID = name, DisplayName = name, MetaData = new() { PluginID = "test" }, Options = new(), Plugin = plugin };

    private sealed class TranslationFlow
    {
        internal Settings Settings { get; } = new() { SourceLang = LangEnum.English, TargetLang = LangEnum.ChineseSimplified,
            AutoTranslate = false, CopyAfterTranslation = CopyAfterTranslation.NoAction, CopyAfterTranslationNotAutomatic = false,
            HistoryLimit = (HistoryLimit)10, TextPreprocessing = Rules("new") };
        internal TranslationResultCoordinator Coordinator { get; }
        private readonly MainWindowViewModel _model;
        internal TranslationFlow(SqlService sql, params Service[] services)
        {
            Coordinator = new(key => key, new(), Settings);
            var translateService = (TranslateService)RuntimeHelpers.GetUninitializedObject(typeof(TranslateService));
            translateService.Services = new ObservableCollection<Service>(services);
            _model = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
            Set("<Settings>k__BackingField", Settings);
            Set("<TranslateService>k__BackingField", translateService);
            Set("_translationCoordinator", Coordinator);
            Set("_sqlService", sql);
            Set("_debounceExecutor", new DebounceExecutor());
            _model.InputText = "secret";
        }
        private void Set(string field, object value) => typeof(MainWindowViewModel)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_model, value);
        internal Task InvokeAsync(string method, params object?[] arguments) => (Task)typeof(MainWindowViewModel)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_model, arguments)!;
    }

    private sealed class FlowPlugin(string name, bool fail = false, TaskCompletionSource? started = null,
        TaskCompletionSource? release = null) : TranslatePluginBase
    {
        internal List<TranslateRequest> Requests { get; } = [];
        public override async Task TranslateAsync(TranslateRequest request, TranslateResult result, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (started is not null) { started.SetResult(); await release!.Task; }
            result.IsSuccess = !fail;
            result.Text = fail ? "failed" : "translation " + name;
        }
        public override string? GetSourceLanguage(LangEnum language) => language.ToString();
        public override string? GetTargetLanguage(LangEnum language) => language.ToString();
        public override System.Windows.Controls.Control GetSettingUI() => null!;
        public override void Init(IPluginContext context) { }
        public override void Dispose() { }
    }

    private sealed class TestDatabase(string directory, SqlService sql) : IAsyncDisposable
    {
        internal SqlService Sql { get; } = sql;
        internal static async Task<TestDatabase> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "STranslate-history-merge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var sql = new SqlService($"Data Source={Path.Combine(directory, "history.db")};Pooling=False");
            await sql.InitializeDBAsync();
            return new(directory, sql);
        }
        internal Task MergeAsync(HistoryModel model, long limit = 10) => Sql.MergeTranslationDataAsync(model, limit, _ => true);
        internal Task<HistoryModel?> ReadAsync() => Sql.GetDataAsync("secret", "English", "ChineseSimplified");
        public ValueTask DisposeAsync()
        {
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
            return ValueTask.CompletedTask;
        }
    }
}
