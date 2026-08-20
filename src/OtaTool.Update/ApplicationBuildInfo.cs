using System.Reflection;

namespace OtaTool.Update;

public sealed record ApplicationBuildInfo(
    ReleaseVersion Version,
    string DisplayVersion,
    DateTimeOffset? BuildTimeUtc,
    string GitCommit,
    string InstallDirectory)
{
    public static ApplicationBuildInfo FromAssembly(
        Assembly assembly,
        string? installDirectory = null)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";
        var versionText = informational.Split('+', 2)[0];
        if (!ReleaseVersion.TryParse(versionText, out var version))
        {
            version = default;
        }

        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        metadata.TryGetValue("BuildTimeUtc", out var buildTimeText);
        metadata.TryGetValue("SourceRevisionId", out var revision);
        if (string.IsNullOrWhiteSpace(revision) && informational.Contains('+'))
        {
            revision = informational.Split('+', 2)[1];
        }

        var gitCommit = string.IsNullOrWhiteSpace(revision)
            ? "未知"
            : revision[..Math.Min(7, revision.Length)];
        return new ApplicationBuildInfo(
            version,
            $"v{version}",
            DateTimeOffset.TryParse(buildTimeText, out var buildTime)
                ? buildTime.ToUniversalTime()
                : null,
            gitCommit,
            Path.GetFullPath(installDirectory ?? AppContext.BaseDirectory));
    }
}
