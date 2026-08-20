namespace OtaTool.Update;

public enum UpdateCheckStatus
{
    Skipped,
    NoUpdate,
    UpdateAvailable,
    Failed,
}

public sealed record UpdateAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string Digest);

public sealed record UpdateReleaseInfo(
    ReleaseVersion Version,
    string TagName,
    string ReleaseNotes,
    DateTimeOffset? PublishedAt,
    Uri ReleasePageUri,
    UpdateAsset PackageAsset,
    UpdateAsset ChecksumAsset);

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    ReleaseVersion CurrentVersion,
    UpdateReleaseInfo? Release = null,
    string? ErrorMessage = null)
{
    public static UpdateCheckResult Skipped(ReleaseVersion current) =>
        new(UpdateCheckStatus.Skipped, current);

    public static UpdateCheckResult NoUpdate(ReleaseVersion current) =>
        new(UpdateCheckStatus.NoUpdate, current);

    public static UpdateCheckResult Available(
        ReleaseVersion current,
        UpdateReleaseInfo release) =>
        new(UpdateCheckStatus.UpdateAvailable, current, release);

    public static UpdateCheckResult Failed(ReleaseVersion current, string message) =>
        new(UpdateCheckStatus.Failed, current, ErrorMessage: message);
}

public sealed record UpdateDownloadProgress(
    string Stage,
    long BytesReceived,
    long? TotalBytes,
    double BytesPerSecond)
{
    public double? Percent => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100.0 / TotalBytes.Value, 0, 100)
        : null;
}

public sealed record PreparedUpdate(
    string StagingDirectory,
    string InstallDirectory,
    string UpdaterExecutablePath,
    string JobFilePath);

public sealed class UpdateState
{
    public DateTimeOffset? LastSuccessfulCheckUtc { get; set; }

    public DateTimeOffset? LastFailedCheckUtc { get; set; }

    public string? LastPromptedVersion { get; set; }

    public PendingUpdateState? PendingUpdate { get; set; }
}

public sealed class PendingUpdateState
{
    public string TargetVersion { get; set; } = string.Empty;

    public string JobFilePath { get; set; } = string.Empty;

    public DateTimeOffset PreparedAtUtc { get; set; }
}

public readonly record struct ReleaseVersion(
    int Major,
    int Minor,
    int Patch) : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var parts = normalized.Split('.');
        if (parts.Length != 3 ||
            !TryParsePart(parts[0], out var major) ||
            !TryParsePart(parts[1], out var minor) ||
            !TryParsePart(parts[2], out var patch))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch);
        return true;
    }

    public static bool TryParseTag(string? value, out ReleaseVersion version)
    {
        version = default;
        return value is not null &&
               value.StartsWith('v') &&
               TryParse(value, out version) &&
               string.Equals(value, $"v{version}", StringComparison.Ordinal);
    }

    public int CompareTo(ReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    private static bool TryParsePart(string value, out int result)
    {
        result = 0;
        return value.Length > 0 &&
               (value.Length == 1 || value[0] != '0') &&
               int.TryParse(value, out result) &&
               result >= 0;
    }
}

public interface IUpdateService
{
    Uri ReleasesPageUri { get; }

    UpdateState GetState();

    Task<UpdateCheckResult> CheckForUpdatesAsync(
        bool force,
        CancellationToken cancellationToken = default);

    bool ShouldPrompt(UpdateReleaseInfo release);

    void MarkPrompted(UpdateReleaseInfo release);

    bool CanInstallInPlace(string installDirectory, out string reason);

    Task<PreparedUpdate> DownloadAndPrepareAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken = default);
}
