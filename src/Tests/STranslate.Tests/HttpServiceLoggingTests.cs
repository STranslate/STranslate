using Microsoft.Extensions.Logging;
using STranslate.Core;
using STranslate.Plugin;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web;

namespace STranslate.Tests;

public class HttpServiceLoggingTests
{
    [Theory]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Trace)]
    public async Task FailedStreamKeepsStatusAndSanitizedDiagnostics(LogLevel minimumLevel)
    {
        const string key = "example/key+for-test";
        const string secondKey = "second-placeholder";
        const string encodedKey = "example%2Fkey%2Bfor-test";
        var url = $"https://name:demo-password@example.test:8443/models/demo?alt=sse&%6B%65%79={encodedKey}&KEY={secondKey}&other=private-filter#private-fragment";
        var logger = new MemoryLogger(minimumLevel);
        var service = CreateService(logger, (request, _) =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            Assert.Equal(new[] { key, secondKey }, query.GetValues("key"));
            Assert.Equal("private-filter", query["other"]);
            Assert.Equal("name:demo-password", request.RequestUri.UserInfo);
            return Respond(HttpStatusCode.ServiceUnavailable, $$"""
                {"error":{"message":"try later {{key}} {{encodedKey}} {{secondKey}}"},"access_token":"returned-placeholder"}
                """, "application/json");
        });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.StreamPostAsync(url, new { text = "request diagnostics" }, _ => { }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Contains("try later", exception.Message);
        Assert.DoesNotContain("returned-placeholder", exception.ToString());
        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("https://example.test:8443/models/demo?alt=sse", error.Message);
        Assert.Contains("try later", error.Message);
        Assert.Contains("503 (ServiceUnavailable)", error.Message);
        foreach (var secret in new[] { key, encodedKey, secondKey, "demo-password", "private-filter", "private-fragment", "returned-placeholder" })
        {
            Assert.DoesNotContain(secret, logger.Text);
            Assert.DoesNotContain(secret, exception.Message);
        }
    }

    [Fact]
    public async Task SuccessfulPostPreservesTransportAndRedactsQueryHeadersAndOauthFields()
    {
        const string secret = "fixture-secret/+";
        const string headerKey = "fixture-header-key";
        const string oauthToken = "fixture-oauth-token";
        const string responseBody = $$"""{"Access_Token":"{{oauthToken}}","description":"normal response"}""";
        var logger = new MemoryLogger(LogLevel.Trace);
        var options = new Options
        {
            QueryParams = new() { ["client_secret"] = secret },
            Headers = new() { ["x-goog-api-key"] = headerKey }
        };
        var service = CreateService(logger, async (request, cancellationToken) =>
        {
            Assert.Equal(secret, HttpUtility.ParseQueryString(request.RequestUri!.Query)["client_secret"]);
            Assert.Equal(headerKey, Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("fixture-json-password", body);
            Assert.Contains(headerKey, body);
            return await Respond(HttpStatusCode.OK, responseBody, "application/json");
        });

        var result = await service.PostAsync("https://example.test/oauth", new
        {
            password = "fixture-json-password",
            text = $"normal request, {headerKey} {HttpUtility.UrlEncode(secret)}"
        }, options);

        Assert.Equal(responseBody, result);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Trace, entry.Level));
        Assert.Contains("normal request", logger.Text);
        Assert.Contains("normal response", logger.Text);
        Assert.Contains("200 POST", logger.Text);
        foreach (var credential in new[] { secret, HttpUtility.UrlEncode(secret), headerKey, oauthToken, "fixture-json-password" })
            Assert.DoesNotContain(credential, logger.Text);
    }

    [Fact]
    public async Task FormCredentialsAreRedactedFromTheRequestAndEchoedResponse()
    {
        const string formSecret = "form fixture+secret";
        var form = new Dictionary<string, string> { ["ClIeNt_SeCrEt"] = formSecret, ["description"] = "normal form" };
        var logger = new MemoryLogger(LogLevel.Trace);
        var service = CreateService(logger, async (request, cancellationToken) =>
        {
            var rawForm = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Equal(formSecret, HttpUtility.ParseQueryString(rawForm)["client_secret"]);
            return await Respond(HttpStatusCode.OK, $"normal reply ({formSecret})", "text/plain");
        });

        await service.PostFormAsync("https://example.test/token", form);

        Assert.Contains("normal form", logger.Text);
        Assert.Contains("normal reply", logger.Text);
        Assert.DoesNotContain(formSecret, logger.Text);
        Assert.DoesNotContain(HttpUtility.UrlEncode(formSecret), logger.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothStreamApisKeepOriginalDataAndLogOnlySanitizedData(bool useEnumerator)
    {
        const string firstLine = """data: {"token":"sse-placeholder","text":"visible"}""";
        const string fragmentStart = """data: {"password":""";
        const string fragmentValue = """data: "fragment-secret-placeholder" """;
        const string fragmentEnd = "data: }";
        const string plainLine = "data: plain diagnostic";
        const string finalLine = "data: [DONE]";
        var logger = new MemoryLogger(LogLevel.Trace);
        var service = CreateService(logger, (_, _) => Respond(
            HttpStatusCode.OK, $"{firstLine}\n\n{fragmentStart}\n{fragmentValue}\n{fragmentEnd}\n\n{plainLine}\n\n{finalLine}\n", "text/event-stream"));

        const string url = "https://example.test/stream?alt=sse&key=legacy-placeholder";
        var lines = new List<string>();
        if (useEnumerator)
        {
            await foreach (var line in service.StreamPostAsyncEnumerable(url, new { text = "hello" }))
                lines.Add(line);
        }
        else
        {
            await service.StreamPostAsync(url, new { text = "hello" }, lines.Add);
        }

        Assert.Equal(new[] { firstLine, fragmentStart, fragmentValue, fragmentEnd, plainLine, finalLine }, lines);
        Assert.Contains("visible", logger.Text);
        Assert.Contains(plainLine, logger.Text);
        Assert.Contains("data: [DONE]", logger.Text);
        Assert.Contains("<invalid JSON:", logger.Text);
        Assert.DoesNotContain("sse-placeholder", logger.Text);
        Assert.DoesNotContain("fragment-secret-placeholder", logger.Text);
        Assert.DoesNotContain("legacy-placeholder", logger.Text);
    }

    [Fact]
    public async Task CanceledStreamLogsSafeAddressAndStillThrowsCancellation()
    {
        using var cancel = new CancellationTokenSource();
        var logger = new MemoryLogger(LogLevel.Information);
        var service = CreateService(logger, (_, cancellationToken) =>
        {
            cancel.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StreamPostAsync(
            "https://example.test/stream?key=cancellation-placeholder&alt=sse",
            "{}", _ => { }, cancellationToken: cancel.Token));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("https://example.test/stream?alt=sse", entry.Message);
        Assert.DoesNotContain("cancellation-placeholder", entry.Message);
    }

    [Fact]
    public async Task ErrorLogScrubsEmbeddedUrlsAndInnerExceptionText()
    {
        const string url = "https://example.test/path?token=error-placeholder&filter=extra-query";
        var logger = new MemoryLogger(LogLevel.Error);
        var service = CreateService(logger, (_, _) => throw new HttpRequestException(
            $"Connection failed near {url}", new InvalidOperationException("internal detail error-placeholder")));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAsync(url, options: null));

        Assert.Contains("Connection failed", logger.Text);
        Assert.Contains("internal detail", logger.Text);
        Assert.DoesNotContain("error-placeholder", logger.Text);
        Assert.DoesNotContain("extra-query", logger.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadErrorsUseSanitizedDiagnostics(bool post)
    {
        const string url = "https://example.test/files?access_token=download-placeholder";
        var logger = new MemoryLogger(LogLevel.Trace);
        var service = CreateService(logger, (request, _) =>
        {
            Assert.Equal(post ? HttpMethod.Post : HttpMethod.Get, request.Method);
            Assert.Equal("download-placeholder", HttpUtility.ParseQueryString(request.RequestUri!.Query)["access_token"]);
            return Respond(HttpStatusCode.Forbidden, "download rejected: download-placeholder", "text/plain");
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => post
            ? service.DownloadFileAsync(url, new { }, System.IO.Path.GetTempPath(), "unused")
            : service.DownloadFileAsync(url, System.IO.Path.GetTempPath(), "unused"));

        Assert.Contains("403", logger.Text);
        Assert.Contains("download rejected", logger.Text);
        Assert.DoesNotContain("download-placeholder", logger.Text);
    }

    private static HttpService CreateService(
        MemoryLogger logger, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new FakeClientFactory(send), logger, new Settings());

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string content, string mediaType) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, mediaType) });

    private sealed class FakeClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FakeHandler(send));
    }

    private sealed class FakeHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class MemoryLogger(LogLevel minimumLevel) : ILogger<HttpService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public string Text => string.Join('\n', Entries.Select(entry => entry.Message));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                Entries.Add((logLevel, formatter(state, exception) + exception?.ToString()));
        }
    }
}
