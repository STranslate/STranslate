namespace STranslate.Core;

/// <summary>
/// 主进程退出前通过 Host 写入、下次启动时读取的备份传输状态。
/// 格式为 <c>{BackupType}|{filePath}</c>；旧版本只写路径，按 WebDav 兼容处理。
/// </summary>
public static class BackupTransferState
{
    private const char Separator = '|';

    public static string Format(BackupType type, string filePath) => $"{type}{Separator}{filePath}";

    public static (BackupType Type, string FilePath) Parse(string content)
    {
        var trimmed = content.Trim();
        var separatorIndex = trimmed.IndexOf(Separator);
        if (separatorIndex > 0 &&
            Enum.TryParse<BackupType>(trimmed[..separatorIndex].Trim(), true, out var type) &&
            type is BackupType.WebDav or BackupType.S3)
        {
            return (type, trimmed[(separatorIndex + 1)..].Trim());
        }

        return (BackupType.WebDav, trimmed);
    }
}
