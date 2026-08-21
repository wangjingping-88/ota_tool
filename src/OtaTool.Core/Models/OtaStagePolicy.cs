namespace OtaTool.Core.Models;

public static class OtaStagePolicy
{
    private static readonly IReadOnlySet<string> KnownStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "REQUEST_ACCEPTED",
        "PATCH_DOWNLOAD",
        "PATCH_VERIFY",
        "PREPARE",
        "TRANSFER",
        "REPAIR",
        "VERIFY",
        "PROGRAM",
        "COMMIT",
        "BOOT_VERIFY",
        "FINISHED",
    };

    private static readonly IReadOnlyDictionary<DeviceType, IReadOnlySet<string>> ApplicableStages =
        new Dictionary<DeviceType, IReadOnlySet<string>>
        {
            [DeviceType.Gateway] = CreateSet(
                "REQUEST_ACCEPTED",
                "PATCH_DOWNLOAD",
                "PATCH_VERIFY",
                "PROGRAM",
                "FINISHED"),
            [DeviceType.Sync] = CreateSet(
                "REQUEST_ACCEPTED",
                "PATCH_DOWNLOAD",
                "PATCH_VERIFY",
                "PREPARE",
                "TRANSFER",
                "REPAIR",
                "FINISHED"),
            [DeviceType.Async] = CreateSet(
                "REQUEST_ACCEPTED",
                "PATCH_DOWNLOAD",
                "PATCH_VERIFY",
                "PREPARE",
                "TRANSFER",
                "REPAIR",
                "VERIFY",
                "PROGRAM",
                "BOOT_VERIFY",
                "FINISHED"),
            [DeviceType.Node] = CreateSet(
                "REQUEST_ACCEPTED",
                "PATCH_DOWNLOAD",
                "PATCH_VERIFY",
                "PREPARE",
                "TRANSFER",
                "REPAIR",
                "VERIFY",
                "PROGRAM",
                "COMMIT",
                "BOOT_VERIFY",
                "FINISHED"),
        };

    public static bool IsApplicable(DeviceType deviceType, string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        return !KnownStages.Contains(stage) ||
               ApplicableStages.GetValueOrDefault(deviceType, KnownStages).Contains(stage);
    }

    private static IReadOnlySet<string> CreateSet(params string[] stages)
        => new HashSet<string>(stages, StringComparer.OrdinalIgnoreCase);
}
