using STranslate.Plugin;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STranslate.Core;

public class StorageBase<T> : ISavable where T : new()
{
    public const string FileSuffix = ".json";

    public bool IsDefaultData { get; private set; }

    public bool Exists() => File.Exists(FilePath);

    protected StorageBase()
    {
    }

    public StorageBase(string filePath)
    {
        FilePath = filePath;
        DirectoryPath = Path.GetDirectoryName(filePath) ?? throw new ArgumentException("Invalid file path");

        EnsureDirectoryExists();
    }

    protected T? Data;

    protected string FilePath { get; init; } = null!;

    private string TempFilePath => $"{FilePath}.tmp";

    private string BackupFilePath => $"{FilePath}.bak";

    protected string DirectoryPath { get; init; } = null!;

    public virtual void Save()
    {
        EnsureDirectoryExists();

        var serialized = JsonSerializer.Serialize(Data, JsonOption);

        File.WriteAllText(TempFilePath, serialized);

        AtomicWriteSetting();
    }

    public void Delete()
    {
        foreach (var path in new[] { FilePath, BackupFilePath, TempFilePath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public async Task<T> LoadAsync()
    {
        if (Data != null)
        {
            return Data;
        }

        string? serialized = null;

        if (File.Exists(FilePath))
        {
            serialized = await File.ReadAllTextAsync(FilePath);
        }

        if (!string.IsNullOrEmpty(serialized))
        {
            try
            {
                Data = JsonSerializer.Deserialize<T>(serialized, JsonOption) ?? await LoadBackupOrDefaultAsync();
            }
            catch (JsonException)
            {
                Data = await LoadBackupOrDefaultAsync();
            }
        }
        else
        {
            Data = await LoadBackupOrDefaultAsync();
        }

        return Data.NonNull();
    }

    private async ValueTask<T> LoadBackupOrDefaultAsync()
    {
        var backup = await TryLoadBackupAsync();

        return backup ?? LoadDefault();
    }

    private async ValueTask<T?> TryLoadBackupAsync()
    {
        if (!File.Exists(BackupFilePath))
        {
            return default;
        }

        try
        {
            T? data;
            await using (var source = File.OpenRead(BackupFilePath))
                data = await JsonSerializer.DeserializeAsync<T>(source, JsonOption) ?? default;

            if (data != null)
            {
                RestoreBackup();
            }

            return data;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private void RestoreBackup()
    {
        //Log.Info(ClassName, $"Failed to load settings.json, {BackupFilePath} restored successfully");

        if (File.Exists(FilePath))
        {
            File.Replace(BackupFilePath, FilePath, null);
        }
        else
        {
            File.Move(BackupFilePath, FilePath);
        }
    }

    public T Load()
    {
        string? serialized = null;

        if (File.Exists(FilePath))
        {
            serialized = File.ReadAllText(FilePath);
        }

        if (!string.IsNullOrEmpty(serialized))
        {
            try
            {
                Data = JsonSerializer.Deserialize<T>(serialized, JsonOption) ?? TryLoadBackup() ?? LoadDefault();
            }
            catch (JsonException)
            {
                Data = TryLoadBackup() ?? LoadDefault();
            }
        }
        else
        {
            Data = TryLoadBackup() ?? LoadDefault();
        }

        return Data.NonNull();
    }

    private T LoadDefault()
    {
        if (File.Exists(FilePath))
        {
            BackupOriginFile();
        }
        else
        {
            IsDefaultData = true;
        }

        return new T();
    }

    private T? TryLoadBackup()
    {
        if (!File.Exists(BackupFilePath))
        {
            return default;
        }

        try
        {
            var data = JsonSerializer.Deserialize<T>(File.ReadAllText(BackupFilePath), JsonOption);

            if (data != null)
            {
                RestoreBackup();
            }

            return data;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private void BackupOriginFile()
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss-fffffff", CultureInfo.CurrentUICulture);
        var directory = Path.GetDirectoryName(FilePath).NonNull();
        var originName = Path.GetFileNameWithoutExtension(FilePath);
        var backupName = $"{originName}-{timestamp}{FileSuffix}";
        var backupPath = Path.Combine(directory, backupName);
        File.Copy(FilePath, backupPath, true);
        // todo give user notification for the backup process
    }

    public virtual async Task SaveAsync()
    {
        EnsureDirectoryExists();

        await using var tempOutput = File.OpenWrite(TempFilePath);
        await JsonSerializer.SerializeAsync(tempOutput, Data, JsonOption);
        AtomicWriteSetting();
    }

    private void AtomicWriteSetting()
    {
        if (!File.Exists(FilePath))
        {
            File.Move(TempFilePath, FilePath);
        }
        else
        {
            var finalFilePath = new FileInfo(FilePath).LinkTarget ?? FilePath;
            File.Replace(TempFilePath, finalFilePath, BackupFilePath);
        }
    }

    protected void EnsureDirectoryExists()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            Directory.CreateDirectory(DirectoryPath);
        }
    }

    private static JsonSerializerOptions JsonOption =>
        new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };
}
