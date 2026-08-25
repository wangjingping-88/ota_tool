using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OtaTool.Core.Http;

public sealed record HttpFileVerificationResult(bool IsSuccess, string Message, long? RemoteLength = null, string? RemoteMd5 = null);

public static class HttpFileVerifier
{
    public static async Task<HttpFileVerificationResult> VerifyAsync(Uri uri, long expectedLength, string expectedMd5, bool verifyFullMd5, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        return await VerifyAsync(client, uri, expectedLength, expectedMd5, verifyFullMd5, cancellationToken);
    }

    public static async Task<HttpFileVerificationResult> VerifyAsync(HttpClient client, Uri uri, long expectedLength, string expectedMd5, bool verifyFullMd5, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(uri);
        if (expectedLength < 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        if (expectedMd5.Length != 32 || !expectedMd5.All(Uri.IsHexDigit)) throw new ArgumentException("MD5 必须为 32 位十六进制字符串。", nameof(expectedMd5));
        using var headResponse = await SendWithTransientRetryAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Head, uri),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (headResponse.StatusCode != HttpStatusCode.OK)
        {
            return new HttpFileVerificationResult(false, $"HTTP HEAD 返回 {(int)headResponse.StatusCode}。", headResponse.Content.Headers.ContentLength);
        }
        if (headResponse.Content.Headers.ContentLength != expectedLength)
        {
            return new HttpFileVerificationResult(false, "HTTP HEAD 文件长度与本地 Patch 不一致。", headResponse.Content.Headers.ContentLength);
        }

        using var rangeResponse = await SendWithTransientRetryAsync(
            client,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (rangeResponse.StatusCode != HttpStatusCode.PartialContent || rangeResponse.Content.Headers.ContentRange?.Length != expectedLength)
        {
            return new HttpFileVerificationResult(false, "HTTP Range 验证失败。", rangeResponse.Content.Headers.ContentRange?.Length);
        }

        if (!verifyFullMd5)
        {
            return new HttpFileVerificationResult(true, "HTTP HEAD 和 Range 验证通过。", expectedLength);
        }

        return await VerifyFullContentWithTransientRetryAsync(
            client,
            uri,
            expectedLength,
            expectedMd5,
            cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendWithTransientRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = requestFactory();
                var response = await client.SendAsync(request, completionOption, cancellationToken);
                if (!IsTransient(response.StatusCode) || attempt == maximumAttempts)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < maximumAttempts && !cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), cancellationToken);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        var numericStatusCode = (int)statusCode;
        return numericStatusCode is 408 or 425 or 429 or 500 or 502 or 503 or 504;
    }

    private static async Task<HttpFileVerificationResult> VerifyFullContentWithTransientRetryAsync(
        HttpClient client,
        Uri uri,
        long expectedLength,
        string expectedMd5,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransient(response.StatusCode) && attempt < maximumAttempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), cancellationToken);
                        continue;
                    }
                    return new HttpFileVerificationResult(
                        false,
                        $"HTTP GET 返回 {(int)response.StatusCode}。",
                        response.Content.Headers.ContentLength);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var md5 = Convert.ToHexString(
                    await MD5.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                return md5.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase)
                    ? new HttpFileVerificationResult(
                        true,
                        "HTTP HEAD、Range 和完整 MD5 验证通过。",
                        expectedLength,
                        md5)
                    : new HttpFileVerificationResult(
                        false,
                        "完整下载后的 MD5 与本地 Patch 不一致。",
                        expectedLength,
                        md5);
            }
            catch (Exception exception) when (
                attempt < maximumAttempts &&
                !cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or IOException or OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), cancellationToken);
            }
        }
    }
}
