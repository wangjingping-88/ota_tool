using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OtaTool.Core.Http;

public sealed record HttpRangeServerOptions(string RootDirectory, int Port = 8080)
{
    public string NormalizedRootDirectory => Path.GetFullPath(RootDirectory);
}

public sealed class HttpRangeServer : IAsyncDisposable
{
    private WebApplication? _application;

    public bool IsRunning => _application is not null;

    public Uri? BaseAddress { get; private set; }

    public async Task StartAsync(HttpRangeServerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (IsRunning)
        {
            throw new InvalidOperationException("HTTP Range 服务已经启动。");
        }

        var rootDirectory = options.NormalizedRootDirectory;
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"HTTP 根目录不存在：{rootDirectory}");
        }

        if (options.Port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "HTTP 端口必须在 1 到 65535 之间。");
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
        var application = builder.Build();
        application.MapMethods("/{**filePath}", ["GET", "HEAD"], context => HandleFileRequestAsync(context, rootDirectory));

        await application.StartAsync(cancellationToken);
        _application = application;
        BaseAddress = new Uri($"http://127.0.0.1:{options.Port}/", UriKind.Absolute);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_application is null)
        {
            return;
        }

        await _application.StopAsync(cancellationToken);
        await _application.DisposeAsync();
        _application = null;
        BaseAddress = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private static async Task HandleFileRequestAsync(HttpContext context, string rootDirectory)
    {
        var requestedPath = context.Request.RouteValues["filePath"]?.ToString();
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var normalizedRelativePath = requestedPath.Replace('/', Path.DirectorySeparatorChar);
        var filePath = Path.GetFullPath(Path.Combine(rootDirectory, normalizedRelativePath));
        var rootWithSeparator = rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? rootDirectory
            : rootDirectory + Path.DirectorySeparatorChar;
        if (!filePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var fileLength = new FileInfo(filePath).Length;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.ContentType = "application/octet-stream";

        var range = ParseRange(context.Request.Headers.Range, fileLength);
        if (range.IsInvalid)
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            context.Response.Headers.ContentRange = $"bytes */{fileLength}";
            return;
        }

        var start = range.Start;
        var end = range.End;
        var responseLength = end - start + 1;
        var isPartial = range.IsPartial;
        context.Response.StatusCode = isPartial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        context.Response.ContentLength = responseLength;
        if (isPartial)
        {
            context.Response.Headers.ContentRange = $"bytes {start}-{end}/{fileLength}";
        }

        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
        file.Seek(start, SeekOrigin.Begin);
        await CopyRangeAsync(file, context.Response.Body, responseLength, context.RequestAborted);
    }

    private static async Task CopyRangeAsync(Stream input, Stream output, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var bytesToRead = (int)Math.Min(buffer.Length, count);
            var bytesRead = await input.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            count -= bytesRead;
        }
    }

    private static ByteRange ParseRange(string? rangeHeader, long length)
    {
        if (length == 0)
        {
            return ByteRange.Invalid;
        }

        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            return new ByteRange(0, length - 1, false, false);
        }

        if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || rangeHeader.Contains(','))
        {
            return ByteRange.Invalid;
        }

        var values = rangeHeader[6..].Split('-', StringSplitOptions.TrimEntries);
        if (values.Length != 2)
        {
            return ByteRange.Invalid;
        }

        if (string.IsNullOrWhiteSpace(values[0]))
        {
            if (!long.TryParse(values[1], out var suffixLength) || suffixLength <= 0)
            {
                return ByteRange.Invalid;
            }

            var start = Math.Max(0, length - suffixLength);
            return new ByteRange(start, length - 1, true, false);
        }

        if (!long.TryParse(values[0], out var rangeStart) || rangeStart < 0 || rangeStart >= length)
        {
            return ByteRange.Invalid;
        }

        var rangeEnd = length - 1;
        if (!string.IsNullOrWhiteSpace(values[1]) && (!long.TryParse(values[1], out rangeEnd) || rangeEnd < rangeStart))
        {
            return ByteRange.Invalid;
        }

        return new ByteRange(rangeStart, Math.Min(rangeEnd, length - 1), true, false);
    }

    private readonly record struct ByteRange(long Start, long End, bool IsPartial, bool IsInvalid)
    {
        public static ByteRange Invalid => new(0, 0, false, true);
    }
}
