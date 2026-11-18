using Microsoft.Extensions.Logging;
using STranslate.Helpers;
using STranslate.Plugin;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Web;

namespace STranslate.Core;

public class HttpService : IHttpService
{
    private readonly Settings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public HttpService(IHttpClientFactory httpClientFactory, ILogger<HttpService> logger, Settings settings)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _settings = settings;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }

    #region GET 请求

    public async Task<string> GetAsync(string url, CancellationToken cancellationToken = default)
    {
        return await GetAsync(Constant.HttpClientName, url, null, cancellationToken);
    }

    public async Task<string> GetAsync(string url, Options? options = null, CancellationToken cancellationToken = default)
    {
        return await GetAsync(Constant.HttpClientName, url, options, cancellationToken);
    }

    public async Task<T?> GetAsync<T>(string url, Options? options = null, CancellationToken cancellationToken = default)
    {
        var json = await GetAsync(url, options, cancellationToken);
        if (string.IsNullOrEmpty(json)) return default;
        return JsonSerializer.Deserialize<T>(json, _jsonOptions);
    }

    public async Task<byte[]> GetAsBytesAsync(string url, CancellationToken cancellationToken = default)
    {
        return await GetAsBytesAsync(Constant.HttpClientName, url, null, cancellationToken);
    }

    public async Task<byte[]> GetAsBytesAsync(string url, Options? options = null, CancellationToken cancellationToken = default)
    {
        return await GetAsBytesAsync(Constant.HttpClientName, url, options, cancellationToken);
    }
    public async Task<Stream> GetAsStreamAsync(string url, CancellationToken cancellationToken = default)
    {
        return await GetAsStreamAsync(Constant.HttpClientName, url, null, cancellationToken);
    }

    public async Task<Stream> GetAsStreamAsync(string url, Options? options = null, CancellationToken cancellationToken = default)
    {
        return await GetAsStreamAsync(Constant.HttpClientName, url, options, cancellationToken);
    }

    public async Task<string> GetAsync(string serviceName, string url, Options? options = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = new HttpLogRedactor(url, options);
        try
        {
            using var client = _httpClientFactory.CreateClient(serviceName);
            ConfigureClientTimeout(client, options?.Timeout);

            var finalUrl = BuildUrlWithQuery(url, options?.QueryParams);
            using var request = new HttpRequestMessage(HttpMethod.Get, finalUrl);

            AddHeaders(request, options?.Headers);

            LogRequest(diagnostics, request, serviceName, null);

            using var response = await client.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            LogResponse(diagnostics, request, response, serviceName, responseContent, response.Content.Headers.ContentType?.MediaType);
            await EnsureSuccessStatusCodeAsync(response, diagnostics, cancellationToken);

            return responseContent;
        }
        catch (Exception ex)
        {
            LogFailure(diagnostics, serviceName, "GET request failed", ex);
            throw;
        }
    }

    public async Task<byte[]> GetAsBytesAsync(string serviceName, string url, Options? options = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = new HttpLogRedactor(url, options);
        try
        {
            using var client = _httpClientFactory.CreateClient(serviceName);
            ConfigureClientTimeout(client, options?.Timeout);

            var finalUrl = BuildUrlWithQuery(url, options?.QueryParams);
            using var request = new HttpRequestMessage(HttpMethod.Get, finalUrl);

            AddHeaders(request, options?.Headers);

            LogRequest(diagnostics, request, serviceName, null);

            using var response = await client.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            LogResponse(diagnostics, request, response, serviceName, $"<binary: {responseContent.Length} bytes>");
            await EnsureSuccessStatusCodeAsync(response, diagnostics, cancellationToken);

            return responseContent;
        }
        catch (Exception ex)
        {
            LogFailure(diagnostics, serviceName, "GET request for bytes failed", ex);
            throw;
        }
    }

    public async Task<Stream> GetAsStreamAsync(string serviceName, string url, Options? options = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = new HttpLogRedactor(url, options);
        try
        {
            var client = _httpClientFactory.CreateClient(serviceName);
            ConfigureClientTimeout(client, options?.Timeout);

            var finalUrl = BuildUrlWithQuery(url, options?.QueryParams);
            var request = new HttpRequestMessage(HttpMethod.Get, finalUrl);

            AddHeaders(request, options?.Headers);

            LogRequest(diagnostics, request, serviceName, null);

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            LogResponse(diagnostics, request, response, serviceName, "<stream>");
            await EnsureSuccessStatusCodeAsync(response, diagnostics, cancellationToken);

            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            LogFailure(diagnostics, serviceName, "GET stream request failed", ex);
            throw;
        }
    }

    #endregion

    #region POST 请求

    public async Task<string> PostAsync(string url, object content, Options? options = null, CancellationToken cancellationToken = default)
    {
        var json = content is string str ? str : JsonSerializer.Serialize(content, _jsonOptions);
        return await PostAsync(Constant.HttpClientName, url, json, options, cancellationToken);
    }

    public async Task<T?> PostAsync<T>(string url, object content, Options? options = null, CancellationToken cancellationToken = default)
    {
        var json = await PostAsync(url, content, options, cancellationToken);
        if (string.IsNullOrEmpty(json)) return default;
        return JsonSerializer.Deserialize<T>(json, _jsonOptions);
    }

    public async Task<byte[]> PostAsBytesAsync(string url, object content, Options? options = null, CancellationToken cancellationToken = default)
    {
        return await PostAsBytesAsync(Constant.HttpClientName, url, content, options, cancellationToken);
    }

    public async Task<string> PostAsync(string serviceName, string url, object content, Options? options = null, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(content, _jsonOptions);
        return await PostAsync(serviceName, url, json, options, cancellationToken);
    }

    private async Task<string> PostAsync(string serviceName, string url, string? content, Options? options = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = new HttpLogRedactor(url, options);
        try
        {
            using var client = _httpClientFactory.CreateClient(serviceName);
            ConfigureClientTimeout(client, options?.Timeout);

            var finalUrl = BuildUrlWithQuery(url, options?.QueryParams);
            using var request = new HttpRequestMessage(HttpMethod.Post, finalUrl);
            if (content != null)
            {
                request.Content = new StringContent(content, Encoding.UTF8);

                // 创建不带 charset 的 Content-Type
                var contentType = options?.ContentType ?? "application/json";
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }

            AddHeaders(request, options?.Headers);

            LogRequest(diagnostics, request, serviceName, content);

            using var response = await client.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            LogResponse(diagnostics, request, response, serviceName, responseContent, response.Content.Headers.ContentType?.MediaType);
            await EnsureSuccessStatusCodeAsync(response, diagnostics, cancellationToken);

            return responseContent;
        }
        catch (Exception ex)
        {
            LogFailure(diagnostics, serviceName, "POST request failed", ex);
            throw;
        }
    }

    public async Task<byte[]> PostAsBytesAsync(string serviceName, string url, object content, Options? options = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = new HttpLogRedactor(url, options);
        try
        {
            using var client = _httpClientFactory.CreateClient(serviceName);
            ConfigureClientTimeout(client, options?.Timeout);

            var finalUrl = BuildUrlWithQuery(url, options?.QueryParams);
            using var request = new HttpRequestMessage(HttpMethod.Post, finalUrl);

            string requestContent;
            if (content is byte[] bytes)
            {
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(options?.ContentType ?? "application/octet-stream");
                requestContent = $"<binary: {bytes.Length} bytes>";
            }
            else
            {
                var json = content is string str ? str : JsonSerializer.Serialize(content, _jsonOptions);
                request.Content = new StringContent(json, Encoding.UTF8, options?.ContentType ?? "application/json");
                requestContent = json;
            }

            AddHeaders(request, options?.Headers);

            LogRequest(diagnostics, request, serviceName, requestContent);

            using var response = await client.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            LogResponse(diagnostics, request, response, serviceName, $"<binary: {responseContent.Length} bytes>");
            await EnsureSuccessStatusCodeAsync(response, diagnostics, cancellationToken);

            return responseContent;
        }
        catch (Exception ex)
        {
            LogFailure(diagnostics, serviceName, "POST request for bytes failed", ex);
            throw;
        }
    }

    #endregion

    #region Form 请求

    public async Task<string> PostFormAsync(string url, Dictionary<string, string> formData, Options? options = null, CancellationToken cancellationToken = default)
    {
        return await PostFormAsync(Constant.HttpClientName, url, formData, options, cancellationToken);
    }

    public async Task<string> PostFormAsync(string serviceName, string url, Dictionary<string, string> formData, Options? options = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = new HttpLogRedactor(url, options);
        try
        {
            using var client = _httpClientFactory.CreateClient(serviceName);
            ConfigureClientTimeout(client, options?.Timeout);

            var finalUrl = BuildUrlWithQuery(url, options?.QueryParams);
            using var request = new HttpRequestMessage(HttpMethod.Post, finalUrl);
            request.Content = new FormUrlEncodedContent(formData);
            diagnostics.RegisterFormCredentials(formData);

            AddHeaders(request, options?.Headers);

            LogRequest(diagnostics, request, serviceName, _logger.IsEnabled(LogLevel.Trace)
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null);

            using var response = await client.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            LogResponse(diagnostics, request, response, serviceName, responseContent, response.Content.Headers.ContentType?.MediaType);
            await EnsureSuccessStatusCodeAsync(response, diagnostics, cancellationToken);

            return responseContent;
        }
        catch (Exception ex)
        {
            LogFailure(diagnostics, serviceName, "POST form request failed", ex);
            throw;
        }
    }

    #endregion

    #region 流式请求

    public async Task StreamPostAsync(string url, object content, Action<string> onDataReceived, Options? options = null, CancellationToken cancellationToken = default)
    {
        await StreamPostAsync(Constant.HttpClientName, url, content, onDataReceived, options, cancellationToken);
    }

    public async Task StreamPostAsync(string serviceName, string url, object content, Action<string> onDataReceived, Options? options = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = new HttpLogRedactor(url, options);
        try
        {
            using var client = _httpClientFactory.CreateClient(serviceName);
            ConfigureClientTimeout(client, options?.Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            var jsonContent = content is string str ? str : JsonSerializer.Serialize(content, _jsonOptions);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, options?.ContentType ?? "application/json");

            AddHeaders(request, options?.Headers);

            LogRequest(diagnostics, request, serviceName, jsonContent);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            LogResponse(diagnostics, request, response, serviceName, "<stream started>");
            await EnsureSuccessStatusCodeAsync(response, diagnostics, cancellationToken);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            // 使用 ReadLineAsync 的返回值来判断流是否结束，而不是 EndOfStream 属性
            string? line;
            while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                LogStreamResponse(diagnostics, request, response, serviceName, line);
                if (!string.IsNullOrEmpty(line))
                    onDataReceived(line);
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Stream POST request was canceled for URL: {Url}, Service: {ServiceName}", diagnostics.Url, serviceName);
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(diagnostics, serviceName, "Stream POST request failed", ex);
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamPostAsyncEnumerable(string url, object content, Options? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var line in StreamPostAsyncEnumerable(Constant.HttpClientName, url, content, options, cancellationToken))
        {
            yield return line;
        }
    }

    public async IAsyncEnumerable<string> StreamPostAsyncEnumerable(string serviceName, string url, object content, Options? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var diagnostics = new HttpLogRedactor(url, options);
        using var client = _httpClientFactory.CreateClient(serviceName);
        ConfigureClientTimeout(client, options?.Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var jsonContent = content is string str ? str : JsonSerializer.Serialize(content, _jsonOptions);
        request.Content = new StringContent(jsonContent, Encoding.UTF8, options?.ContentType ?? "application/json");

        AddHeaders(request, options?.Headers);

        LogRequest(diagnostics, request, serviceName, jsonContent);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        LogResponse(diagnostics, request, response, serviceName, "<stream started>");
        await EnsureSuccessStatusCodeAsync(response, diagnostics, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // 保持与回调模式一致：逐行读取并忽略空行，避免把心跳空包暴露给调用方。
        string? line;
        while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            LogStreamResponse(diagnostics, request, response, serviceName, line);
            if (!string.IsNullOrEmpty(line))
                yield return line;
        }
    }

    #endregion

    #region 文件下载

    public async Task<string> DownloadFileAsync(string url, string savePath, string fileName, Options? options = null, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return await DownloadFileAsync(Constant.HttpClientName, url, savePath, fileName, options, progress, cancellationToken);
    }

    public async Task<string> DownloadFileAsync(string url, object content, string savePath, string fileName, Options? options = null, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return await DownloadFileAsync(Constant.HttpClientName, url, content, savePath, fileName, options, progress, cancellationToken);
    }

    public async Task<string> DownloadFileAsync(string serviceName, string url, string savePath, string fileName, Options? options = null, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = new HttpLogRedactor(url, options);
        try
        {
            if (!Directory.Exists(savePath))
                Directory.CreateDirectory(savePath);

            var fullPath = Path.Combine(savePath, fileName);

            using var client = _httpClientFactory.CreateClient(serviceName);
            ConfigureClientTimeout(client, options?.Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(request, options?.Headers);

            LogRequest(diagnostics, request, serviceName, null);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            LogResponse(diagnostics, request, response, serviceName, $"<file: {response.Content.Headers.ContentLength?.ToString() ?? "unknown"} bytes>");
            await EnsureSuccessStatusCodeAsync(response, diagnostics, cancellationToken);

            var totalBytes = response.Content.Headers.ContentLength ?? -1;

            await using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 8192, useAsync: true);
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            if (totalBytes > 0 && progress != null)
            {
                await CopyToWithProgressAsync(contentStream, fileStream, totalBytes, progress, cancellationToken);
            }
            else
            {
                await contentStream.CopyToAsync(fileStream, cancellationToken);
            }

            _logger?.LogInformation("File downloaded successfully: {Path}", fullPath);
            return fullPath;
        }
        catch (Exception ex)
        {
            LogFailure(diagnostics, serviceName, "File download failed", ex);
            throw;
        }
    }

    public async Task<string> DownloadFileAsync(string serviceName, string url, object content, string savePath, string fileName, Options? options = null, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = new HttpLogRedactor(url, options);
        try
        {
            if (!Directory.Exists(savePath))
                Directory.CreateDirectory(savePath);

            var fullPath = Path.Combine(savePath, fileName);

            using var client = _httpClientFactory.CreateClient(serviceName);
            ConfigureClientTimeout(client, options?.Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            var jsonContent = content is string str ? str : JsonSerializer.Serialize(content, _jsonOptions);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, options?.ContentType ?? "application/json");

            AddHeaders(request, options?.Headers);

            LogRequest(diagnostics, request, serviceName, jsonContent);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            LogResponse(diagnostics, request, response, serviceName, $"<file: {response.Content.Headers.ContentLength?.ToString() ?? "unknown"} bytes>");
            await EnsureSuccessStatusCodeAsync(response, diagnostics, cancellationToken);

            var totalBytes = response.Content.Headers.ContentLength ?? -1;

            await using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 8192, useAsync: true);
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            if (totalBytes > 0 && progress != null)
            {
                await CopyToWithProgressAsync(contentStream, fileStream, totalBytes, progress, cancellationToken);
            }
            else
            {
                await contentStream.CopyToAsync(fileStream, cancellationToken);
            }

            _logger?.LogInformation("POST file downloaded successfully: {Path}", fullPath);
            return fullPath;
        }
        catch (Exception ex)
        {
            LogFailure(diagnostics, serviceName, "POST file download failed", ex);
            throw;
        }
    }

    #endregion

    #region 代理测试

    /// <summary>
    /// 测试代理连接
    /// </summary>
    public async Task<bool> TestProxyAsync(CancellationToken cancellationToken = default) =>
        await ProxyHelper.TestProxyConnectionAsync(_settings.Proxy, cancellationToken);

    /// <summary>
    /// 获取当前IP（用于测试代理）
    /// </summary>
    public async Task<string> GetCurrentIpAsync(CancellationToken cancellationToken = default) =>
        await ProxyHelper.GetCurrentIpAsync(_settings.Proxy, cancellationToken);

    #endregion

    #region 私有方法

    private void LogRequest(HttpLogRedactor diagnostics, HttpRequestMessage request, string serviceName, string? content)
    {
        diagnostics.SetRequestUri(request.RequestUri);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace(
                "HTTP request: {Method} {Url}, Service: {ServiceName}, Body: {Body}",
                request.Method,
                diagnostics.Url,
                serviceName,
                diagnostics.SanitizeBody(content, request.Content?.Headers.ContentType?.MediaType));
        }
    }

    private void LogResponse(
        HttpLogRedactor diagnostics,
        HttpRequestMessage request,
        HttpResponseMessage response,
        string serviceName,
        string content,
        string? mediaType = null)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace(
                "HTTP response: {StatusCode} {Method} {Url}, Service: {ServiceName}, Body: {Body}",
                (int)response.StatusCode,
                request.Method,
                diagnostics.Url,
                serviceName,
                diagnostics.SanitizeBody(content, mediaType));
        }
    }

    private void LogStreamResponse(
        HttpLogRedactor diagnostics,
        HttpRequestMessage request,
        HttpResponseMessage response,
        string serviceName,
        string content)
    {
        if (!_logger.IsEnabled(LogLevel.Trace))
        {
            diagnostics.SkipStreamLine(content);
            return;
        }

        var data = diagnostics.SanitizeStreamLine(content, response.Content.Headers.ContentType?.MediaType);
        if (data.Length > 0)
        {
            _logger.LogTrace(
                "HTTP stream response: {StatusCode} {Method} {Url}, Service: {ServiceName}, Data: {Data}",
                (int)response.StatusCode,
                request.Method,
                diagnostics.Url,
                serviceName,
                data);
        }
    }

    private void LogFailure(HttpLogRedactor diagnostics, string serviceName, string operation, Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.LogError(
                "{Operation} for URL: {Url}, Service: {ServiceName}{NewLine}{Error}",
                operation,
                diagnostics.Url,
                serviceName,
                Environment.NewLine,
                diagnostics.SanitizeText(exception.ToString()));
        }
    }

    private void ConfigureClientTimeout(HttpClient client, TimeSpan? timeout)
    {
        // 如果传入了超时时间，则使用传入的值，否则使用软件配置超时时间
        if (timeout.HasValue)
        {
            client.Timeout = timeout.Value;
        }
        else
        {
            client.Timeout = TimeSpan.FromSeconds(_settings.HttpTimeout);
        }
    }

    private static void AddHeaders(HttpRequestMessage request, Dictionary<string, string>? headers)
    {
        if (headers == null) return;

        foreach (var header in headers)
        {
            // 对于一些特殊的头部，使用 TryAddWithoutValidation 避免验证问题
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ||
                header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            else
            {
                try
                {
                    request.Headers.Add(header.Key, header.Value);
                }
                catch
                {
                    // 如果添加失败，尝试使用无验证的方式
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }
    }

    private static string BuildUrlWithQuery(string url, Dictionary<string, string>? queryParams)
    {
        if (queryParams == null || !queryParams.Any()) return url;

        var uriBuilder = new UriBuilder(url);
        var query = HttpUtility.ParseQueryString(uriBuilder.Query);

        foreach (var param in queryParams)
        {
            query[param.Key] = param.Value;
        }

        uriBuilder.Query = query.ToString();
        return uriBuilder.ToString();
    }

    private static async Task EnsureSuccessStatusCodeAsync(
        HttpResponseMessage response, HttpLogRedactor diagnostics, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var outerMsg = diagnostics.SanitizeText(response.ReasonPhrase ?? "HTTP request failed");
        var innerMsg = string.Empty;

        try
        {
            innerMsg = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            // 忽略读取响应内容的异常
        }

        if (innerMsg.Length > 0)
            innerMsg = diagnostics.SanitizeBody(innerMsg, response.Content.Headers.ContentType?.MediaType);

        var errorMessage = $"{outerMsg}: {(int)response.StatusCode} ({response.StatusCode})";
        if (innerMsg.Length > 0)
            errorMessage = $"{errorMessage}, Details: {innerMsg}";

        throw new HttpRequestException(errorMessage, null, response.StatusCode);
    }

    private static async Task CopyToWithProgressAsync(Stream source, Stream destination, long totalBytes,
        IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        long totalBytesRead = 0;
        int bytesRead;
        var stopwatch = Stopwatch.StartNew();

        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalBytesRead += bytesRead;

            var downloadProgress = new DownloadProgress
            {
                TotalBytes = totalBytes,
                DownloadedBytes = totalBytesRead,
                ElapsedTime = stopwatch.Elapsed,
                Speed = stopwatch.Elapsed.TotalSeconds > 0 ? totalBytesRead / stopwatch.Elapsed.TotalSeconds : 0
            };

            progress.Report(downloadProgress);
        }
    }

    #endregion
}
