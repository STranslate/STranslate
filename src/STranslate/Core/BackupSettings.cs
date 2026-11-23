using CommunityToolkit.Mvvm.ComponentModel;

namespace STranslate.Core;

public partial class BackupSettings : ObservableObject
{
    [ObservableProperty] public partial BackupType Type { get; set; }

    [ObservableProperty] public partial string Address { get; set; } = string.Empty;

    [ObservableProperty] public partial string Username { get; set; } = string.Empty;

    [ObservableProperty] public partial string Password { get; set; } = string.Empty;

    [ObservableProperty] public partial S3ProviderType S3Provider { get; set; } = S3ProviderType.AwsS3;

    [ObservableProperty] public partial string S3Endpoint { get; set; } = string.Empty;

    [ObservableProperty] public partial string S3Region { get; set; } = "us-east-1";

    [ObservableProperty] public partial string S3Bucket { get; set; } = string.Empty;

    [ObservableProperty] public partial string S3AccessKey { get; set; } = string.Empty;

    [ObservableProperty] public partial string S3SecretKey { get; set; } = string.Empty;

    [ObservableProperty] public partial bool S3ForcePathStyle { get; set; }

    /// <summary>
    /// 由 UI 在用户主动切换预设后调用，避免反序列化或绑定初始化时覆盖已保存的配置。
    /// </summary>
    public void ApplyS3ProviderPreset()
    {
        switch (S3Provider)
        {
            case S3ProviderType.AwsS3:
                S3Endpoint = string.Empty;
                S3Region = "us-east-1";
                S3ForcePathStyle = false;
                break;
            case S3ProviderType.CloudflareR2:
                S3Region = "auto";
                S3ForcePathStyle = false;
                break;
            case S3ProviderType.Minio:
                S3Region = "us-east-1";
                S3ForcePathStyle = true;
                break;
            case S3ProviderType.Custom:
                break;
        }
    }
}

public enum BackupType
{
    Local,
    WebDav,
    S3,
}

public enum S3ProviderType
{
    AwsS3,
    CloudflareR2,
    Minio,
    Custom,
}
