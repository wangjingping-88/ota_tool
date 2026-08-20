using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OtaTool.Core.Http;

public sealed record HttpFileVerificationResult(bool IsSuccess, string Message, long? RemoteLength = null, string? RemoteMd5 = null);

public static class HttpFileVerifier
{
    public static async Task<HttpFileVerificationResult> VerifyAsync(Uri uri, long expectedLength, string expectedMd5, bool verifyFullMd5, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (expectedLength < 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        if (expectedMd5.Length != 32 || !expectedMd5.All(Uri.IsHexDigit)) throw new ArgumentException("MD5 必须为 32 位十六进制字符串。", nameof(expectedMd5));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var head = new HttpRequestMessage(HttpMethod.Head, uri);
        using var headResponse = await client.SendAsync(head, cancellationToken);
        if (headResponse.StatusCode != HttpStatusCode.OK)
        {
            return new HttpFileVerificationResult(false, $"HTTP HEAD 返回 {(int)headResponse.StatusCode}。", headResponse.Content.Headers.ContentLength);
        }
        if (headResponse.Content.Headers.ContentLength != expectedLength)
        {
            return new HttpFileVerificationResult(false, "HTTP HEAD 文件长度与本地 Patch 不一致。", headResponse.Content.Headers.ContentLength);
        }

        using var range = new HttpRequestMessage(HttpMethod.Get, uri);
        range.Headers.Range = new RangeHeaderValue(0, 0);
        using var rangeResponse = await client.SendAsync(range, cancellationToken);
        if (rangeResponse.StatusCode != HttpStatusCode.PartialContent || rangeResponse.Content.Headers.ContentRange?.Length != expectedLength)
        {
            return new HttpFileVerificationResult(false, "HTTP Range 验证失败。", rangeResponse.Content.Headers.ContentRange?.Length);
        }

        if (!verifyFullMd5)
        {
            return new HttpFileVerificationResult(true, "HTTP HEAD 和 Range 验证通过。", expectedLength);
        }

        await using var stream = await client.GetStreamAsync(uri, cancellationToken);
        var md5 = Convert.ToHexString(await MD5.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return md5.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase)
            ? new HttpFileVerificationResult(true, "HTTP HEAD、Range 和完整 MD5 验证通过。", expectedLength, md5)
            : new HttpFileVerificationResult(false, "完整下载后的 MD5 与本地 Patch 不一致。", expectedLength, md5);
    }
}
