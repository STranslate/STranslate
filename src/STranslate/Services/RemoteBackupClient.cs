using CommunityToolkit.Mvvm.ComponentModel;

namespace STranslate.Services;

/// <summary>
/// 远程备份存储的通用操作。上传/下载/删除/重命名失败时优先返回 <see cref="RemoteBackupResult"/>，
/// 网络等异常仍向上抛出，由调用方统一提示。
/// </summary>
public interface IRemoteBackupClient : IDisposable
{
    bool SupportsRename { get; }

    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    Task<RemoteBackupResult> UploadAsync(string localFilePath, string fileName, CancellationToken cancellationToken = default);

    Task<RemoteBackupResult> DownloadAsync(string fileName, string localFilePath, CancellationToken cancellationToken = default);

    Task<RemoteBackupResult> DeleteAsync(string fileName, CancellationToken cancellationToken = default);

    Task<RemoteBackupResult> RenameAsync(string oldName, string newName, CancellationToken cancellationToken = default);
}

public readonly record struct RemoteBackupResult(bool Success, int StatusCode = 0, string? Description = null)
{
    public static RemoteBackupResult Ok() => new(true);

    public static RemoteBackupResult Fail(int statusCode, string? description) => new(false, statusCode, description);
}

public sealed class RemoteBackupException(int statusCode, string? description)
    : Exception($"{statusCode} {description}")
{
    public int StatusCode { get; } = statusCode;

    public string? Description { get; } = description;
}

public partial class RemoteBackupFile : ObservableObject
{
    [ObservableProperty]
    public partial string FullName { get; set; } = string.Empty;

    public RemoteBackupFile()
    {
    }

    public RemoteBackupFile(string fullName) => FullName = fullName;
}
