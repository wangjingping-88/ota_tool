using System.Globalization;

namespace OtaTool.Core.Models;

/// <summary>按任务目标范围推演串行测试计划中的设备版本。</summary>
public static class OtaTestPlanVersionProjection
{
    public static OtaTestPlanItemTemplate? FindPreviousCompatible(
        IEnumerable<OtaTestPlanItemTemplate> items,
        OtaTestPlanItemTemplate candidate)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(candidate);
        var scopeKey = BuildTargetScopeKey(candidate);
        return items
            .Where(item => item.Order < candidate.Order &&
                           string.Equals(BuildTargetScopeKey(item), scopeKey, StringComparison.Ordinal))
            .OrderBy(item => item.Order)
            .LastOrDefault();
    }

    public static byte GetProjectedEndVersion(OtaTestPlanItemTemplate item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var value = item.ExecutionKind == OtaTestPlanExecutionKind.Cycle
            ? item.OldVersion
            : item.NewVersion;
        if (!byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new InvalidOperationException($"任务“{item.Name}”的结束版本无效。");
        }
        return version;
    }

    public static string BuildTargetScopeKey(OtaTestPlanItemTemplate item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var prefix = string.Join('|',
            item.Mode,
            item.GatewayId.Trim(),
            item.DeviceType);
        if (item.DeviceType == DeviceType.Gateway)
        {
            return prefix;
        }

        var rule = item.TargetRule;
        if (item.DeviceType is DeviceType.Sync or DeviceType.Async)
        {
            return string.Join('|',
                prefix,
                rule.ResolutionMode,
                NormalizeIdentifiers(rule.DeviceIds));
        }

        var extenderTargets = rule.ExtenderTargets
            .Select(target => $"{NormalizeIdentifier(target.ExtenderId)}:{NormalizeIdentifiers(target.NodeIds)}")
            .Order(StringComparer.Ordinal);
        return string.Join('|',
            prefix,
            rule.ResolutionMode,
            rule.NodeType?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            string.Join(';', extenderTargets));
    }

    private static string NormalizeIdentifiers(IEnumerable<string> values)
        => string.Join(',', values
            .Select(NormalizeIdentifier)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

    private static string NormalizeIdentifier(string value)
        => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : value.Trim();
}
