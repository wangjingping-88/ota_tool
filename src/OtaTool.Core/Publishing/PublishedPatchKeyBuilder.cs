using System.Globalization;

namespace OtaTool.Core.Publishing;

/// <summary>
/// 生成已发布 Patch 的目标标识。同一内容使用不同文件名发布时，必须视为不同的远端文件。
/// </summary>
public static class PublishedPatchKeyBuilder
{
    public static string Build(
        string host,
        int port,
        string remoteDirectory,
        string publicBaseUrl,
        string localFilePath,
        string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        var fileName = Path.GetFileName(localFilePath);
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("Patch 文件名不能为空。", nameof(localFilePath));

        return string.Join(
            "|",
            "v2",
            host.Trim(),
            port.ToString(CultureInfo.InvariantCulture),
            remoteDirectory.Trim().TrimEnd('/'),
            publicBaseUrl.Trim().TrimEnd('/'),
            fileName,
            sha256.Trim());
    }
}
