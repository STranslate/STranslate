using Dapper;
using Dapper.Contrib.Extensions;
using Microsoft.Data.Sqlite;
using STranslate.Plugin;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STranslate.Core;

public class SqlService
{
    #region Asynchronous method

    /// <summary>
    ///     创建数据库
    /// </summary>
    /// <returns></returns>
    public async Task InitializeDBAsync()
    {
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync();

        // 创建表的 SQL 语句
        var createTableSql =
            @"
                CREATE TABLE IF NOT EXISTS History (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Time TEXT NOT NULL,
                    SourceLang TEXT,
                    TargetLang TEXT,
                    EffectiveSourceLang TEXT,
                    EffectiveTargetLang TEXT,
                    SourceText TEXT,
                    RawData TEXT,
                    Favorite INTEGER,
                    Remark TEXT
                );
            ";
        await connection.ExecuteAsync(createTableSql);
        await EnsureHistoryEffectiveLanguageColumnsAsync(connection);
    }

    /// <summary>
    ///     删除记录
    /// </summary>
    /// <param name="history"></param>
    /// <returns></returns>
    public async Task<bool> DeleteDataAsync(HistoryModel history)
    {
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync();

        return await connection.DeleteAsync(history);
    }

    /// <summary>
    ///     删除所有记录
    /// </summary>
    /// <returns></returns>
    public async Task<bool> DeleteAllDataAsync()
    {
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync();

        return await connection.DeleteAllAsync<HistoryModel>();
    }

    /// <summary>
    ///     插入数据(如果存在则更新)-异步
    /// </summary>
    /// <param name="history"></param>
    /// <param name="count"></param>
    /// <returns></returns>
    public async Task InsertOrUpdateDataAsync(HistoryModel history, long count)
    {
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync();

        var curCount = await connection.QueryFirstOrDefaultAsync<long>("SELECT COUNT(Id) FROM History");
        if (curCount >= count)
        {
            var sql = @"DELETE FROM History WHERE Id IN (SELECT Id FROM History ORDER BY Id ASC LIMIT @Limit)";

            await connection.ExecuteAsync(sql, new { Limit = curCount - count + 1 });
        }

        // 使用 Dapper 的 FirstOrDefault 方法进行查询
        var existingHistory = await connection.QueryFirstOrDefaultAsync<HistoryModel>(
            "SELECT * FROM History WHERE SourceText = @SourceText AND SourceLang = @SourceLang AND TargetLang = @TargetLang",
            new
            {
                history.SourceText,
                history.SourceLang,
                history.TargetLang
            }
        );

        if (existingHistory != null)
        {
            // 使用 Dapper.Contrib 的 Update 方法更新数据
            existingHistory.Time = history.Time;
            existingHistory.Data = history.Data;
            existingHistory.EffectiveSourceLang = history.EffectiveSourceLang;
            existingHistory.EffectiveTargetLang = history.EffectiveTargetLang;
            await connection.UpdateAsync(existingHistory);
            return;
        }

        // 使用 Dapper.Contrib 的 Insert 方法插入数据
        await connection.InsertAsync(history);
    }

    /// <summary>
    ///     更新数据-异步
    /// </summary>
    /// <param name="history"></param>
    /// <returns></returns>
    public async Task UpdateAsync(HistoryModel history)
    {
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync();
        // 使用 Dapper 的 FirstOrDefault 方法进行查询
        var existingHistory = await connection.QueryFirstOrDefaultAsync<HistoryModel>(
            "SELECT * FROM History WHERE SourceText = @SourceText AND SourceLang = @SourceLang AND TargetLang = @TargetLang",
            new
            {
                history.SourceText,
                history.SourceLang,
                history.TargetLang
            }
        );

        if (existingHistory != null)
        {
            // 使用 Dapper.Contrib 的 Update 方法更新数据
            existingHistory.Time = history.Time;
            existingHistory.Data = history.Data;
            existingHistory.EffectiveSourceLang = history.EffectiveSourceLang;
            existingHistory.EffectiveTargetLang = history.EffectiveTargetLang;
            await connection.UpdateAsync(existingHistory);
        }
    }

    /// <summary>
    ///     查询数据
    /// </summary>
    /// <param name="content"></param>
    /// <param name="source"></param>
    /// <param name="target"></param>
    /// <returns></returns>
    public async Task<HistoryModel?> GetDataAsync(string content, string source, string target)
    {
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync();

        // 使用 Dapper 执行查询数据的 SQL 语句
        return await connection.QueryFirstOrDefaultAsync<HistoryModel>(
            "SELECT * FROM History WHERE SourceText = @SourceText AND SourceLang = @SourceLang AND TargetLang = @TargetLang",
            new
            {
                SourceText = content,
                SourceLang = source,
                TargetLang = target
            }
        );
    }

    /// <summary>
    ///     模糊查询内容相关的结果
    /// </summary>
    /// <param name="content"></param>
    /// <returns></returns>
    public async Task<IEnumerable<HistoryModel>?> GetDataAsync(string content, CancellationToken? token = null)
    {
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync(token ?? CancellationToken.None);

        // 构造查询语句
        var query = $"SELECT * FROM History WHERE LOWER(SourceText) LIKE '%{content.ToLower()}%'";
        // 使用 Dapper 执行查询数据的 SQL 语句
        // https://stackoverflow.com/questions/25540793/cancellationtoken-with-async-dapper-methods
        return await connection.QueryAsync<HistoryModel>(new CommandDefinition(query,
            cancellationToken: token ?? CancellationToken.None));
    }

    /// <summary>
    ///     计算总数
    /// </summary>
    /// <returns></returns>
    public async Task<int> GetCountAsync()
    {
        // 可能会存在溢出的情况，不瞎搞出现不了，就酱，逃，欸，还是一开始没定义好
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync();

        // 使用 Dapper 执行查询数据的 SQL 语句
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(Id) FROM History");
    }

    /// <summary>
    ///     查询所有数据
    /// </summary>
    /// <returns></returns>
    public async Task<IEnumerable<HistoryModel>> GetDataAsync()
    {
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync();

        // 使用 Dapper 执行查询数据的 SQL 语句
        return await connection.GetAllAsync<HistoryModel>();
    }

    /// <summary>
    ///     分页查询
    /// </summary>
    /// <param name="pageNum"></param>
    /// <param name="pageSize"></param>
    /// <returns></returns>
    public async Task<IEnumerable<HistoryModel>?> GetDataAsync(int pageNum, int pageSize)
    {
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync();

        // 计算起始行号
        var startRow = (pageNum - 1) * pageSize + 1;

        // 使用 Dapper 进行分页查询
        const string query =
            @"SELECT * FROM (SELECT ROW_NUMBER() OVER (ORDER BY Time DESC) AS RowNum, * FROM History) AS p WHERE RowNum BETWEEN @StartRow AND @EndRow";

        return await connection.QueryAsync<HistoryModel>(query,
            new { StartRow = startRow, EndRow = startRow + pageSize - 1 });
    }

    /// <summary>
    ///     游标分页
    /// </summary>
    /// <param name="pageSize"></param>
    /// <param name="cursor"></param>
    /// <returns></returns>
    public async Task<IEnumerable<HistoryModel>> GetDataCursorPagedAsync(int pageSize, DateTime cursor)
    {
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync();

        // 使用 Dapper 进行分页查询
        const string query = @"SELECT * FROM History WHERE Time < @Cursor ORDER BY Time DESC LIMIT @PageSize OFFSET 0";

        // 查询原始数据
        return await connection.QueryAsync<HistoryModel>(query, new { PageSize = pageSize, Cursor = cursor });
    }

    /// <summary>
    ///     获取上一条记录
    /// </summary>
    /// <param name="history"></param>
    /// <returns></returns>
    public async Task<HistoryModel?> GetPreviousAsync(HistoryModel history)
    {
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync();
        const string query = "SELECT * FROM History WHERE Id < @Id ORDER BY Id DESC LIMIT 1";
        return await connection.QueryFirstOrDefaultAsync<HistoryModel>(query, new { history.Id });
    }

    /// <summary>
    ///     获取下一条记录
    /// </summary>
    /// <param name="history"></param>
    /// <returns></returns>
    public async Task<HistoryModel?> GetNextAsync(HistoryModel history)
    {
        await using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        await connection.OpenAsync();
        const string query = "SELECT * FROM History WHERE Id > @Id ORDER BY Id ASC LIMIT 1";
        return await connection.QueryFirstOrDefaultAsync<HistoryModel>(query, new { history.Id });
    }

    #endregion Asynchronous method

    #region Synchronous method

    public void InitializeDB()
    {
        using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        connection.Open();

        // 创建表的 SQL 语句
        var createTableSql =
            @"
                CREATE TABLE IF NOT EXISTS History (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Time TEXT NOT NULL,
                    SourceLang TEXT,
                    TargetLang TEXT,
                    EffectiveSourceLang TEXT,
                    EffectiveTargetLang TEXT,
                    SourceText TEXT,
                    RawData TEXT,
                    Favorite INTEGER,
                    Remark TEXT
                );
            ";
        connection.Execute(createTableSql);
        EnsureHistoryEffectiveLanguageColumns(connection);
    }

    /// <summary>
    ///     删除记录
    /// </summary>
    /// <param name="history"></param>
    /// <returns></returns>
    public bool DeleteData(HistoryModel history)
    {
        using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        connection.Open();

        return connection.Delete(history);
    }

    /// <summary>
    ///     删除所有记录
    /// </summary>
    /// <returns></returns>
    public bool DeleteAllData()
    {
        using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        connection.Open();

        return connection.DeleteAll<HistoryModel>();
    }

    /// <summary>
    ///     插入数据(如果存在则更新)
    /// </summary>
    /// <param name="history"></param>
    /// <param name="count"></param>
    /// <param name="forceWrite"></param>
    public void InsertOrUpdateData(HistoryModel history, long count, bool forceWrite = false)
    {
        using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        connection.Open();

        var curCount = connection.QueryFirstOrDefault<long>("SELECT COUNT(*) FROM History");
        if (curCount > count)
        {
            var sql = @"DELETE FROM History WHERE Id IN (SELECT Id FROM History ORDER BY Id ASC LIMIT @Limit)";

            connection.Execute(sql, new { Limit = curCount - count + 1 });
        }

        if (forceWrite)
        {
            // 使用 Dapper 的 FirstOrDefault 方法进行查询
            var existingHistory = connection.QueryFirstOrDefault<HistoryModel>(
                "SELECT * FROM History WHERE SourceText = @SourceText AND SourceLang = @SourceLang AND TargetLang = @TargetLang",
                new
                {
                    history.SourceText,
                    history.SourceLang,
                    history.TargetLang
                }
            );

            if (existingHistory != null)
            {
                // 使用 Dapper.Contrib 的 Update 方法更新数据
                existingHistory.Time = history.Time;
                existingHistory.Data = history.Data;
                existingHistory.EffectiveSourceLang = history.EffectiveSourceLang;
                existingHistory.EffectiveTargetLang = history.EffectiveTargetLang;
                connection.Update(existingHistory);
                return;
            }
        }

        // 使用 Dapper.Contrib 的 Insert 方法插入数据
        connection.Insert(history);
    }

    /// <summary>
    ///     查询所有数据
    /// </summary>
    /// <returns></returns>
    public IEnumerable<HistoryModel> GetData()
    {
        using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        connection.Open();

        // 使用 Dapper 执行查询数据的 SQL 语句
        return connection.GetAll<HistoryModel>();
    }

    /// <summary>
    ///     查询数据
    /// </summary>
    /// <param name="content"></param>
    /// <param name="source"></param>
    /// <param name="target"></param>
    /// <returns></returns>
    public HistoryModel? GetData(string content, string source, string target)
    {
        using var connection = new SqliteConnection(DataLocation.DbConnectionString);
        connection.Open();

        // 使用 Dapper 执行查询数据的 SQL 语句
        return connection.QueryFirstOrDefault<HistoryModel>(
            "SELECT * FROM History WHERE SourceText = @SourceText AND SourceLang = @SourceLang AND TargetLang = @TargetLang",
            new
            {
                SourceText = content,
                SourceLang = source,
                TargetLang = target
            }
        );
    }

    private static async Task EnsureHistoryEffectiveLanguageColumnsAsync(SqliteConnection connection)
    {
        var columns = (await connection.QueryAsync<HistoryTableColumnInfo>("PRAGMA table_info(History);"))
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!columns.Contains(nameof(HistoryModel.EffectiveSourceLang)))
            await connection.ExecuteAsync("ALTER TABLE History ADD COLUMN EffectiveSourceLang TEXT;");

        if (!columns.Contains(nameof(HistoryModel.EffectiveTargetLang)))
            await connection.ExecuteAsync("ALTER TABLE History ADD COLUMN EffectiveTargetLang TEXT;");
    }

    private static void EnsureHistoryEffectiveLanguageColumns(SqliteConnection connection)
    {
        var columns = connection.Query<HistoryTableColumnInfo>("PRAGMA table_info(History);")
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!columns.Contains(nameof(HistoryModel.EffectiveSourceLang)))
            connection.Execute("ALTER TABLE History ADD COLUMN EffectiveSourceLang TEXT;");

        if (!columns.Contains(nameof(HistoryModel.EffectiveTargetLang)))
            connection.Execute("ALTER TABLE History ADD COLUMN EffectiveTargetLang TEXT;");
    }

    private sealed class HistoryTableColumnInfo
    {
        public string Name { get; set; } = string.Empty;
    }

    #endregion Synchronous method
}

#region HistoryModel

[Table("History")]
public class HistoryModel
{
    [Key] public long Id { get; set; }

    /// <summary>
    ///     记录时间
    /// </summary>
    public DateTime Time { get; set; }

    /// <summary>
    ///     源语言
    /// </summary>
    public string SourceLang { get; set; } = string.Empty;

    /// <summary>
    ///     目标语言
    /// </summary>
    public string TargetLang { get; set; } = string.Empty;

    /// <summary>
    ///     实际参与翻译的源语言（识别后）
    /// </summary>
    public string? EffectiveSourceLang { get; set; }

    /// <summary>
    ///     实际参与翻译的目标语言（识别后）
    /// </summary>
    public string? EffectiveTargetLang { get; set; }

    /// <summary>
    ///     需翻译内容
    /// </summary>
    public string SourceText { get; set; } = string.Empty;

    /// <summary>
    ///     收藏
    /// </summary>
    public bool Favorite { get; set; }

    /// <summary>
    ///     备注
    /// </summary>
    public string Remark { get; set; } = "";

    /// <summary>
    ///     数据的 JSON 字符串表示(存储到数据库)
    /// </summary>
    public string RawData
    {
        get => JsonSerializer.Serialize(Data, JsonOption);
        set => Data = string.IsNullOrEmpty(value) ? [] : JsonSerializer.Deserialize<List<HistoryData>>(value, JsonOption) ?? [];
    }

    /// <summary>
    ///     数据列表(业务使用,不映射到数据库)
    /// </summary>
    [Write(false)]
    [Computed]
    public List<HistoryData> Data { get; set; } = [];

    public override bool Equals(object? obj)
    {
        if (obj is HistoryModel other)
            return Id == other.Id;  // 仅比较主键
        return false;
    }

    public override int GetHashCode() => Id.GetHashCode();

    internal bool HasData(Service svc)
    {
        var data = GetData(svc);
        return data?.HasSuccessfulMainResult(svc) == true &&
            (svc.Plugin is not ITranslatePlugin || !(svc.Options?.AutoBackTranslation ?? false) || data.HasSuccessfulBackResult);
    }

    internal HistoryData? GetData(Service svc)
    {
        lock (Data)
            return Data.FirstOrDefault(d => d.PluginID == svc.MetaData.PluginID && d.ServiceID == svc.ServiceID);
    }

    internal static JsonSerializerOptions JsonOption =>
        new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };
}

public class HistoryData
{
    public string PluginID { get; set; } = string.Empty;
    public string ServiceID { get; set; } = string.Empty;
    public string? ServiceDisplayName { get; set; }
    /// <summary>
    /// 当时实际发送给服务的文本；旧历史记录没有此字段时回退到原文。
    /// </summary>
    public string? RequestText { get; set; }
    /// <summary>
    /// 生成 RequestText 时使用的规则指纹；空值表示未使用预处理。
    /// </summary>
    public string? PreprocessingFingerprint { get; set; }
    public string? EffectiveSourceLang { get; set; }
    public string? EffectiveTargetLang { get; set; }
    public string? BackTranslationSourceText { get; set; }
    public TranslateResult? TransResult { get; set; }
    public TranslateResult? TransBackResult { get; set; }
    public DictionaryResult? DictResult { get; set; }

    public HistoryData()
    {
    }

    public HistoryData(Service svc)
    {
        PluginID = svc.MetaData.PluginID;
        ServiceID = svc.ServiceID;
        ServiceDisplayName = svc.DisplayName;
    }

    internal void ApplyPreprocessingContext(TranslationOperation operation, LangEnum? source = null, LangEnum? target = null)
    {
        RequestText = operation.RequestText;
        PreprocessingFingerprint = operation.RulesFingerprint;
        EffectiveSourceLang = source?.ToString();
        EffectiveTargetLang = target?.ToString();
    }

    internal bool HasSuccessfulMainResult(Service service) => service.Plugin is ITranslatePlugin
        ? TransResult is { IsSuccess: true } result && !string.IsNullOrWhiteSpace(result.Text)
        : DictResult is { ResultType: not (DictionaryResultType.None or DictionaryResultType.Error) };

    internal bool HasSuccessfulBackResult =>
        TransResult is { IsSuccess: true } main && !string.IsNullOrWhiteSpace(main.Text) &&
        TransBackResult is { IsSuccess: true } back && !string.IsNullOrWhiteSpace(back.Text) &&
        (BackTranslationSourceText is null || BackTranslationSourceText == main.Text);

    internal void MergeFrom(HistoryData incoming)
    {
        if (incoming.TransResult is null && incoming.TransBackResult is not null)
        {
            if (incoming.BackTranslationSourceText != TransResult?.Text ||
                incoming.RequestText != RequestText ||
                incoming.PreprocessingFingerprint != PreprocessingFingerprint ||
                incoming.EffectiveSourceLang != EffectiveSourceLang || incoming.EffectiveTargetLang != EffectiveTargetLang)
                return;
            TransBackResult = incoming.TransBackResult;
            BackTranslationSourceText = incoming.BackTranslationSourceText;
            return;
        }
        ServiceDisplayName = incoming.ServiceDisplayName ?? ServiceDisplayName;
        RequestText = incoming.RequestText;
        PreprocessingFingerprint = incoming.PreprocessingFingerprint;
        EffectiveSourceLang = incoming.EffectiveSourceLang;
        EffectiveTargetLang = incoming.EffectiveTargetLang;
        if (incoming.TransResult is not null)
        {
            TransResult = incoming.TransResult;
            TransBackResult = incoming.TransBackResult;
            BackTranslationSourceText = incoming.BackTranslationSourceText;
        }
        DictResult = incoming.DictResult ?? DictResult;
    }

    internal bool IsCompatible(string originalText, string requestText, string rulesFingerprint)
    {
        var storedFingerprint = PreprocessingFingerprint ?? string.Empty;
        var storedRequestText = RequestText ?? originalText;
        return string.Equals(storedFingerprint, rulesFingerprint, StringComparison.Ordinal) &&
               string.Equals(storedRequestText, requestText, StringComparison.Ordinal);
    }
}

#endregion
