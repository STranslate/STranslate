using STranslate.Core;
using System.IO;
using System.Web;
using WebDav;

namespace STranslate.Services;

public sealed class WebDavBackupClient(WebDavClient client, string absolutePath) : IRemoteBackupClient
{
    public bool SupportsRename => true;

    public string AbsolutePath => absolutePath;

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.Propfind(absolutePath);
        if (!response.IsSuccessful)
        {
            var created = await client.Mkcol(absolutePath);
            if (!created.IsSuccessful)
                throw new RemoteBackupException(created.StatusCode, created.Description);

            return [];
        }

        var files = new List<string>();
        foreach (var resource in response.Resources)
        {
            if (resource.IsCollection ||
                resource.Uri == null ||
                !resource.Uri.Contains(Constant.AppName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // html 解码以显示中文
            var fileName = HttpUtility.UrlDecode(resource.Uri)
                .Replace(absolutePath, string.Empty)
                .Trim('/');

            if (!string.IsNullOrWhiteSpace(fileName))
                files.Add(fileName);
        }

        return files;
    }

    public async Task<RemoteBackupResult> UploadAsync(
        string localFilePath,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var response = await client.Propfind(absolutePath);
        if (!response.IsSuccessful)
        {
            var created = await client.Mkcol(absolutePath);
            if (!created.IsSuccessful)
                return RemoteBackupResult.Fail(created.StatusCode, created.Description);
        }

        using var stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await client.PutFile($"{absolutePath}{fileName}", stream);

        return result.IsSuccessful && result.StatusCode == 201
            ? RemoteBackupResult.Ok()
            : RemoteBackupResult.Fail(result.StatusCode, result.Description);
    }

    public async Task<RemoteBackupResult> DownloadAsync(
        string fileName,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.GetRawFile($"{absolutePath}{fileName}");
        if (!response.IsSuccessful || response.StatusCode != 200)
            return RemoteBackupResult.Fail(response.StatusCode, response.Description);

        using var fileStream = File.Create(localFilePath);
        await response.Stream.CopyToAsync(fileStream, cancellationToken);

        return RemoteBackupResult.Ok();
    }

    public async Task<RemoteBackupResult> DeleteAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var response = await client.Delete($"{absolutePath}{fileName}");

        return response.IsSuccessful && response.StatusCode == 204
            ? RemoteBackupResult.Ok()
            : RemoteBackupResult.Fail(response.StatusCode, response.Description);
    }

    public async Task<RemoteBackupResult> RenameAsync(
        string oldName,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var response = await client.Move($"{absolutePath}{oldName}", $"{absolutePath}{newName}");

        return response.IsSuccessful && response.StatusCode == 201
            ? RemoteBackupResult.Ok()
            : RemoteBackupResult.Fail(response.StatusCode, response.Description);
    }

    public void Dispose() => client.Dispose();
}
