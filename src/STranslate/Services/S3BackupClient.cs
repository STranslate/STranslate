using Amazon.S3;
using Amazon.S3.Model;
using System.IO;

namespace STranslate.Services;

public sealed class S3BackupClient(AmazonS3Client client, string bucket, string prefix) : IRemoteBackupClient
{
    public bool SupportsRename => false;

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var files = new List<string>();
        string? continuationToken = null;

        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = continuationToken,
            }, cancellationToken);

            foreach (var item in response.S3Objects)
            {
                var fileName = item.Key[prefix.Length..];
                if (!string.IsNullOrWhiteSpace(fileName) &&
                    fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(fileName);
                }
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (!string.IsNullOrEmpty(continuationToken));

        return files;
    }

    public async Task<RemoteBackupResult> UploadAsync(
        string localFilePath,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = prefix + fileName,
                FilePath = localFilePath,
                // R2 等兼容实现不支持 AWS SDK 默认的流式分块签名。
                UseChunkEncoding = false,
            }, cancellationToken);

            return (int)response.HttpStatusCode is >= 200 and < 300
                ? RemoteBackupResult.Ok()
                : RemoteBackupResult.Fail((int)response.HttpStatusCode, response.HttpStatusCode.ToString());
        }
        catch (AmazonS3Exception ex)
        {
            return RemoteBackupResult.Fail((int)ex.StatusCode, ex.Message);
        }
    }

    public async Task<RemoteBackupResult> DownloadAsync(
        string fileName,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = prefix + fileName,
            }, cancellationToken);

            await using var fileStream = File.Create(localFilePath);
            await response.ResponseStream.CopyToAsync(fileStream, cancellationToken);

            return RemoteBackupResult.Ok();
        }
        catch (AmazonS3Exception ex)
        {
            return RemoteBackupResult.Fail((int)ex.StatusCode, ex.Message);
        }
    }

    public async Task<RemoteBackupResult> DeleteAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = prefix + fileName,
            }, cancellationToken);

            return (int)response.HttpStatusCode is >= 200 and < 300
                ? RemoteBackupResult.Ok()
                : RemoteBackupResult.Fail((int)response.HttpStatusCode, response.HttpStatusCode.ToString());
        }
        catch (AmazonS3Exception ex)
        {
            return RemoteBackupResult.Fail((int)ex.StatusCode, ex.Message);
        }
    }

    public Task<RemoteBackupResult> RenameAsync(
        string oldName,
        string newName,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("S3 does not support rename.");

    public void Dispose() => client.Dispose();
}
