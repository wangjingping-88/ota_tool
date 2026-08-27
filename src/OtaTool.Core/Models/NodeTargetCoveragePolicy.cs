namespace OtaTool.Core.Models;

public sealed record NodeTargetCoverageResult(
    int SelectedExtenderCount,
    IReadOnlyList<uint> MissingExtenderIds,
    IReadOnlyList<uint> UnexpectedExtenderIds)
{
    public bool IsValid => SelectedExtenderCount > 0 &&
                           MissingExtenderIds.Count == 0 &&
                           UnexpectedExtenderIds.Count == 0;
}

public static class NodeTargetCoveragePolicy
{
    public static NodeTargetCoverageResult Check(
        IEnumerable<uint> selectedExtenderIds,
        IEnumerable<OtaExtenderTarget> targets)
    {
        var selected = selectedExtenderIds
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .ToArray();
        var targetExtendersWithNodes = targets
            .Where(target => uint.TryParse(target.ExtenderId, out var extenderId) &&
                             extenderId > 0 &&
                             target.NodeIds.Any(nodeId => ushort.TryParse(nodeId, out var parsedNodeId) && parsedNodeId > 0))
            .Select(target => uint.Parse(target.ExtenderId))
            .Distinct()
            .Order()
            .ToArray();

        return new NodeTargetCoverageResult(
            selected.Length,
            selected.Except(targetExtendersWithNodes).ToArray(),
            targetExtendersWithNodes.Except(selected).ToArray());
    }
}
