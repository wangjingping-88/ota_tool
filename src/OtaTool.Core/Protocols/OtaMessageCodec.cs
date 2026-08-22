using System.Text.Json;
using System.Text.Json.Nodes;
using OtaTool.Core.Models;

namespace OtaTool.Core.Protocols;

public sealed record OtaProtocolOptions(
    string DownstreamTopicTemplate = "ucchip/down/sgw/{gatewayId}/{sequence}",
    string UpstreamTopicFilterTemplate = "ucchip/up/sgw/{gatewayId}/#",
    int HttpAccess = 1,
    int OtaType = 1,
    int OtaRange = 1);

public sealed record OutboundOtaMessage(string Topic, string JsonPayload, int Sequence, byte QualityOfService = 1);

public sealed record GatewayFinalResult(
    int Sequence,
    byte OldVersion,
    byte NewVersion,
    string DeviceType,
    string Prompt,
    bool IsSuccess);

public sealed record GatewayOtaStage(
    string Stage,
    string State,
    long StartOffsetMs,
    long DurationMs,
    string Reason);

public sealed record GatewayOtaSubtask(
    uint ExtenderId,
    string Stage,
    string Result,
    long ElapsedMs,
    int TargetCount,
    int PreparedCount,
    int SuccessCount,
    int FailedCount,
    string Reason);

public sealed record GatewayOtaStatus(
    int QuerySequence,
    int TaskSequence,
    uint SessionId,
    string Result,
    string Status,
    string Stage,
    long? TaskElapsedMs,
    long? FileSize,
    long? TransferredBytes,
    int? TargetTotal,
    int? TargetSuccess,
    int? TargetFailed,
    IReadOnlyList<GatewayOtaStage> Stages,
    IReadOnlyList<GatewayOtaSubtask> Subtasks);

public sealed record GatewayExtenderInfo(
    uint ExtenderId,
    string Detail,
    byte DeviceType,
    byte SoftwareVersion);

public sealed record GatewayBasicInfo(
    uint GatewayId,
    byte SoftwareVersion,
    string RawSoftwareVersion);

public sealed record GatewayNodeInfo(ushort NodeId, byte NodeType, byte SoftwareVersion, sbyte Rssi);

public sealed record GatewayNodeListPage(
    int QuerySequence,
    uint ExtenderId,
    int PageIndex,
    int PageCount,
    int TotalCount,
    string Result,
    string Reason,
    IReadOnlyList<GatewayNodeInfo> Nodes);

public sealed record GatewayAsyncVersion(
    int QuerySequence,
    uint ExtenderId,
    string Result,
    string Reason,
    byte SoftwareVersion);

public static class OtaMessageCodec
{
    public const int UpgradeCommand = 5;
    public const int UpgradeCompletionCommand = 6;
    public const int StatusQueryCommand = 8;
    public const int StatusResponseCommand = 9;
    public const int NodeListQueryCommand = 10;
    public const int NodeListResponseCommand = 11;
    public const int AsyncVersionQueryCommand = 12;
    public const int AsyncVersionResponseCommand = 13;

    public static OutboundOtaMessage CreateUpgradeRequest(OtaTask task, int sequence, OtaProtocolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        options ??= new OtaProtocolOptions();
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        ArgumentException.ThrowIfNullOrWhiteSpace(task.GatewayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.PatchUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.PatchMd5);

        if (!byte.TryParse(task.OldVersion, out var oldVersion) || oldVersion is < 1 or > 254 ||
            !byte.TryParse(task.NewVersion, out var newVersion) || newVersion is < 1 or > 254 ||
            oldVersion == newVersion)
        {
            throw new InvalidOperationException("OTA 版本号必须是 1～254 且新旧版本不能相同。");
        }

        var ota = new JsonObject
        {
            ["active"] = 1,
            ["type"] = options.OtaType,
            ["range"] = options.OtaRange,
            ["new_ver"] = newVersion,
            ["old_ver"] = oldVersion,
            ["dev_type"] = ToProtocolDeviceType(task.DeviceType),
            ["md5"] = task.PatchMd5,
            ["timeout_s"] = checked((int)task.Timeout.TotalSeconds),
            ["net"] = new JsonObject
            {
                ["access"] = options.HttpAccess,
                ["addr"] = task.PatchUrl,
                ["file"] = Path.GetFileName(new Uri(task.PatchUrl, UriKind.Absolute).AbsolutePath),
            },
        };

        if (task.DeviceType == DeviceType.Node)
        {
            if (task.NodeType is not (>= 2 and <= 63))
            {
                throw new InvalidOperationException("Node OTA 必须填写 2～63 的 node_type。");
            }
            ota["node_type"] = task.NodeType.Value;
            var targets = new JsonArray();
            foreach (var target in task.ExtenderTargets)
            {
                if (!uint.TryParse(target.ExtenderId, out var extenderId) || extenderId == 0)
                {
                    throw new InvalidOperationException($"Extender ID 非法：{target.ExtenderId}");
                }
                var nodes = new JsonArray();
                foreach (var nodeId in target.NodeIds)
                {
                    if (!ushort.TryParse(nodeId, out var numericNodeId) || numericNodeId == 0)
                    {
                        throw new InvalidOperationException($"Node ID 非法：{nodeId}");
                    }
                    nodes.Add(numericNodeId);
                }
                targets.Add(new JsonObject { ["dev_id"] = extenderId, ["nodes"] = nodes });
            }
            ota["targets"] = targets;
        }
        else if (task.Target.Scope == TargetScope.SpecifiedIds)
        {
            var targets = new JsonArray();
            foreach (var deviceId in task.Target.DeviceIds)
            {
                if (!uint.TryParse(deviceId, out var numericId) || numericId == 0)
                {
                    throw new InvalidOperationException($"目标 ID 非法：{deviceId}");
                }
                targets.Add(new JsonObject { ["dev_id"] = numericId });
            }
            ota["targets"] = targets;
        }

        var root = new JsonObject
        {
            ["cmd"] = UpgradeCommand,
            ["ver"] = "v2.0",
            ["src"] = 0,
            ["dst"] = 0,
            ["ota"] = ota,
        };
        var topic = options.DownstreamTopicTemplate
            .Replace("{gatewayId}", task.GatewayId, StringComparison.Ordinal)
            .Replace("{sequence}", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return new OutboundOtaMessage(topic, root.ToJsonString(), sequence);
    }

    public static OutboundOtaMessage CreateCancelRequest(OtaTask task, int sequence, OtaProtocolOptions? options = null)
    {
        var request = CreateUpgradeRequest(task, sequence, options);
        var root = JsonNode.Parse(request.JsonPayload)?.AsObject()
            ?? throw new InvalidOperationException("无法创建 OTA 取消请求。");
        var ota = root["ota"]?.AsObject()
            ?? throw new InvalidOperationException("OTA 取消请求缺少 ota 对象。");
        ota["active"] = 0;
        return request with { JsonPayload = root.ToJsonString() };
    }

    public static OutboundOtaMessage CreateStatusQuery(string gatewayId, int querySequence, int taskSequence, uint sessionId, OtaProtocolOptions? options = null)
    {
        options ??= new OtaProtocolOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);
        if (querySequence <= 0 || taskSequence <= 0) throw new ArgumentOutOfRangeException(querySequence <= 0 ? nameof(querySequence) : nameof(taskSequence));
        var root = new JsonObject
        {
            ["cmd"] = StatusQueryCommand,
            ["ver"] = "v2.0",
            ["src"] = 0,
            ["dst"] = 0,
            ["ota_status"] = new JsonObject
            {
                ["query_seq"] = querySequence,
                ["task_seq"] = taskSequence,
                ["session_id"] = sessionId,
            },
        };
        var topic = options.DownstreamTopicTemplate
            .Replace("{gatewayId}", gatewayId, StringComparison.Ordinal)
            .Replace("{sequence}", querySequence.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return new OutboundOtaMessage(topic, root.ToJsonString(), querySequence);
    }

    public static OutboundOtaMessage CreateGatewayBasicInfoQuery(string gatewayId, int sequence, OtaProtocolOptions? options = null)
    {
        options ??= new OtaProtocolOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));

        var root = new JsonObject
        {
            ["cmd"] = 3,
            ["ver"] = "v2.0",
            ["src"] = 0,
            ["dst"] = 0,
            ["query"] = "base",
        };
        var topic = options.DownstreamTopicTemplate
            .Replace("{gatewayId}", gatewayId, StringComparison.Ordinal)
            .Replace("{sequence}", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return new OutboundOtaMessage(topic, root.ToJsonString(), sequence);
    }

    public static OutboundOtaMessage CreateGatewayAuthListQuery(string gatewayId, int sequence, OtaProtocolOptions? options = null)
    {
        options ??= new OtaProtocolOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        var root = new JsonObject
        {
            ["cmd"] = 3,
            ["ver"] = "v2.0",
            ["src"] = 0,
            ["dst"] = 0,
            ["query"] = "auth_list",
        };
        return new OutboundOtaMessage(
            BuildDownstreamTopic(gatewayId, sequence, options),
            root.ToJsonString(),
            sequence);
    }

    public static OutboundOtaMessage CreateNodeListQuery(
        string gatewayId,
        int querySequence,
        uint extenderId,
        int pageIndex,
        OtaProtocolOptions? options = null)
    {
        options ??= new OtaProtocolOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);
        if (querySequence <= 0) throw new ArgumentOutOfRangeException(nameof(querySequence));
        if (extenderId == 0) throw new ArgumentOutOfRangeException(nameof(extenderId));
        if (pageIndex is < 0 or > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var root = new JsonObject
        {
            ["cmd"] = NodeListQueryCommand,
            ["ver"] = "v2.0",
            ["src"] = 0,
            ["dst"] = 0,
            ["node_list"] = new JsonObject
            {
                ["query_seq"] = querySequence,
                ["extender_id"] = extenderId,
                ["page_index"] = pageIndex,
            },
        };
        return new OutboundOtaMessage(
            BuildDownstreamTopic(gatewayId, querySequence, options),
            root.ToJsonString(),
            querySequence);
    }

    public static OutboundOtaMessage CreateAsyncVersionQuery(
        string gatewayId,
        int querySequence,
        uint extenderId,
        OtaProtocolOptions? options = null)
    {
        options ??= new OtaProtocolOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);
        if (querySequence <= 0) throw new ArgumentOutOfRangeException(nameof(querySequence));
        if (extenderId == 0) throw new ArgumentOutOfRangeException(nameof(extenderId));
        var root = new JsonObject
        {
            ["cmd"] = AsyncVersionQueryCommand,
            ["ver"] = "v2.0",
            ["src"] = 0,
            ["dst"] = 0,
            ["async_version"] = new JsonObject
            {
                ["query_seq"] = querySequence,
                ["extender_id"] = extenderId,
            },
        };
        return new OutboundOtaMessage(
            BuildDownstreamTopic(gatewayId, querySequence, options),
            root.ToJsonString(),
            querySequence);
    }

    public static bool TryParseGatewayAuthListPage(
        string json,
        out IReadOnlyList<GatewayExtenderInfo> extenders)
    {
        extenders = Array.Empty<GatewayExtenderInfo>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("cmd", out var command) ||
                command.GetInt32() != 3 ||
                !root.TryGetProperty("auth_num", out var authCount) ||
                !authCount.TryGetInt32(out var count) ||
                count < 0)
            {
                return false;
            }
            var result = new List<GatewayExtenderInfo>();
            foreach (var property in root.EnumerateObject())
            {
                if (!uint.TryParse(property.Name, out var extenderId) || extenderId == 0)
                {
                    continue;
                }
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }
                var detail = property.Value.TryGetProperty("detail", out var detailElement) &&
                             detailElement.ValueKind == JsonValueKind.String
                    ? detailElement.GetString() ?? string.Empty
                    : string.Empty;
                var deviceType = property.Value.TryGetProperty("device_type", out var typeElement) &&
                                 typeElement.TryGetByte(out var parsedDeviceType)
                    ? parsedDeviceType
                    : (byte)0;
                var softwareVersion = property.Value.TryGetProperty("software_version", out var versionElement) &&
                                      versionElement.TryGetByte(out var parsedSoftwareVersion)
                    ? parsedSoftwareVersion
                    : (byte)0;
                result.Add(new GatewayExtenderInfo(
                    extenderId,
                    detail,
                    deviceType,
                    softwareVersion));
            }
            if (result.Count > count)
            {
                return false;
            }
            extenders = result;
            return true;
        }
        catch (Exception exception) when (IsMalformedPayloadException(exception))
        {
            return false;
        }
    }

    public static bool TryParseGatewayBasicInfo(
        string json,
        out GatewayBasicInfo? info)
    {
        info = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("cmd", out var command) ||
                command.GetInt32() != 3 ||
                !root.TryGetProperty("base", out var basicInfo) ||
                basicInfo.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var gatewayId = GetOptionalUInt(basicInfo, "dev_id") ??
                            GetOptionalUInt(root, "src") ?? 0;
            foreach (var name in new[]
                     {
                         "ota_software_version",
                         "software_version",
                         "sw_ver",
                     })
            {
                if (!basicInfo.TryGetProperty(name, out var versionElement) ||
                    !TryParseSoftwareVersion(versionElement, out var softwareVersion, out var rawVersion))
                {
                    continue;
                }
                info = new GatewayBasicInfo(gatewayId, softwareVersion, rawVersion);
                return gatewayId > 0;
            }
            return false;
        }
        catch (Exception exception) when (IsMalformedPayloadException(exception))
        {
            return false;
        }
    }

    public static bool TryParseNodeListPage(string json, out GatewayNodeListPage? page)
    {
        page = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("cmd", out var command) ||
                command.GetInt32() != NodeListResponseCommand ||
                !root.TryGetProperty("node_list", out var nodeList))
            {
                return false;
            }
            var nodes = new List<GatewayNodeInfo>();
            if (nodeList.TryGetProperty("nodes", out var nodeArray) &&
                nodeArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in nodeArray.EnumerateArray())
                {
                    if (!node.TryGetProperty("node_id", out var nodeIdElement) ||
                        !nodeIdElement.TryGetUInt16(out var nodeId) ||
                        !node.TryGetProperty("node_type", out var nodeTypeElement) ||
                        !nodeTypeElement.TryGetByte(out var nodeType) ||
                        !node.TryGetProperty("software_version", out var softwareVersionElement) ||
                        !softwareVersionElement.TryGetByte(out var softwareVersion) ||
                        !node.TryGetProperty("rssi", out var rssiElement) ||
                        !rssiElement.TryGetSByte(out var rssi))
                    {
                        return false;
                    }
                    nodes.Add(new GatewayNodeInfo(nodeId, nodeType, softwareVersion, rssi));
                }
            }
            var itemCount = GetOptionalInt(nodeList, "item_count") ?? -1;
            if (itemCount != nodes.Count || itemCount is < 0 or > 56)
            {
                return false;
            }
            page = new GatewayNodeListPage(
                GetInt(nodeList, "query_seq"),
                GetOptionalUInt(nodeList, "extender_id") ?? 0,
                GetOptionalInt(nodeList, "page_index") ?? -1,
                GetOptionalInt(nodeList, "page_count") ?? 0,
                GetOptionalInt(nodeList, "total_count") ?? 0,
                GetString(nodeList, "result"),
                GetString(nodeList, "reason"),
                nodes);
            return page.QuerySequence > 0 && page.ExtenderId > 0;
        }
        catch (Exception exception) when (IsMalformedPayloadException(exception))
        {
            return false;
        }
    }

    public static bool TryParseAsyncVersionResponse(
        string json,
        out GatewayAsyncVersion? version)
    {
        version = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("cmd", out var command) ||
                command.GetInt32() != AsyncVersionResponseCommand ||
                !root.TryGetProperty("async_version", out var asyncVersion) ||
                asyncVersion.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var result = GetString(asyncVersion, "result");
            var softwareVersion = asyncVersion.TryGetProperty("software_version", out var versionElement) &&
                                  versionElement.TryGetByte(out var parsedVersion)
                ? parsedVersion
                : (byte)0;
            version = new GatewayAsyncVersion(
                GetInt(asyncVersion, "query_seq"),
                GetOptionalUInt(asyncVersion, "extender_id") ?? 0,
                result,
                GetString(asyncVersion, "reason"),
                softwareVersion);
            return version.QuerySequence > 0 &&
                   version.ExtenderId > 0 &&
                   (!result.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                    softwareVersion is >= 1 and <= 254);
        }
        catch (Exception exception) when (IsMalformedPayloadException(exception))
        {
            return false;
        }
    }

    public static bool TryParseAsyncVersionQuery(
        string json,
        out uint extenderId)
    {
        extenderId = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("cmd", out var command) ||
                command.GetInt32() != AsyncVersionQueryCommand ||
                !root.TryGetProperty("async_version", out var asyncVersion) ||
                asyncVersion.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            extenderId = GetOptionalUInt(asyncVersion, "extender_id") ?? 0;
            return extenderId > 0;
        }
        catch (Exception exception) when (IsMalformedPayloadException(exception))
        {
            return false;
        }
    }

    public static bool TryParseNodeListQuery(
        string json,
        out uint extenderId,
        out int pageIndex)
    {
        extenderId = 0;
        pageIndex = -1;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("cmd", out var command) ||
                command.GetInt32() != NodeListQueryCommand ||
                !root.TryGetProperty("node_list", out var nodeList))
            {
                return false;
            }
            extenderId = GetOptionalUInt(nodeList, "extender_id") ?? 0;
            pageIndex = GetOptionalInt(nodeList, "page_index") ?? -1;
            return extenderId > 0 && pageIndex >= 0;
        }
        catch (Exception exception) when (IsMalformedPayloadException(exception))
        {
            return false;
        }
    }

    public static bool TryParseGatewayFinalResult(string json, out GatewayFinalResult? result)
    {
        result = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("cmd", out var command) || command.GetInt32() != UpgradeCompletionCommand)
            {
                return false;
            }
            var prompt = root.TryGetProperty("prompt", out var promptElement) ? promptElement.GetString() ?? string.Empty : string.Empty;
            var sequence = root.TryGetProperty("seq", out var seqElement) ? seqElement.GetInt32() : 0;
            if (!root.TryGetProperty("old_ver", out var oldElement) ||
                !oldElement.TryGetByte(out var oldVersion) ||
                oldVersion is < 1 or > 254 ||
                !root.TryGetProperty("new_ver", out var newElement) ||
                !newElement.TryGetByte(out var newVersion) ||
                newVersion is < 1 or > 254)
            {
                return false;
            }
            var deviceType = root.TryGetProperty("dev_type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
            result = new GatewayFinalResult(sequence, oldVersion, newVersion, deviceType, prompt,
                prompt.Equals("upgrade process has end!", StringComparison.OrdinalIgnoreCase));
            return true;
        }
        catch (Exception exception) when (IsMalformedPayloadException(exception))
        {
            return false;
        }
    }

    public static bool TryParseGatewayStatus(string json, out GatewayOtaStatus? status)
    {
        status = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("cmd", out var command) || command.GetInt32() != StatusResponseCommand || !root.TryGetProperty("ota_status", out var otaStatus))
            {
                return false;
            }
            var stages = ParseStages(otaStatus);
            var subtasks = ParseSubtasks(otaStatus);
            status = new GatewayOtaStatus(
                GetInt(otaStatus, "query_seq"),
                GetInt(otaStatus, "task_seq"),
                GetOptionalUInt(otaStatus, "session_id") ?? 0,
                GetString(otaStatus, "result"),
                GetString(otaStatus, "status"),
                GetString(otaStatus, "stage"),
                GetOptionalLong(otaStatus, "task_elapsed_ms"),
                GetOptionalLong(otaStatus, "file_size"),
                GetOptionalLong(otaStatus, "transferred_bytes"),
                GetOptionalInt(otaStatus, "target_total"),
                GetOptionalInt(otaStatus, "target_success"),
                GetOptionalInt(otaStatus, "target_failed"),
                stages,
                subtasks);
            return true;
        }
        catch (Exception exception) when (IsMalformedPayloadException(exception))
        {
            return false;
        }
    }

    public static string ToProtocolDeviceType(DeviceType deviceType) => deviceType switch
    {
        DeviceType.Gateway => "gateway",
        // Gateway 固件 OTA 管理器定义的协议字符串分别为 iote / ex_mcu。
        DeviceType.Sync => "iote",
        DeviceType.Async => "ex_mcu",
        DeviceType.Node => "node",
        _ => throw new ArgumentOutOfRangeException(nameof(deviceType)),
    };

    private static string GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static int GetInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static int? GetOptionalInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static long? GetOptionalLong(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static IReadOnlyList<GatewayOtaStage> ParseStages(JsonElement otaStatus)
    {
        if (!otaStatus.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GatewayOtaStage>();
        }
        var result = new List<GatewayOtaStage>();
        foreach (var stage in stages.EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object) continue;
            result.Add(new GatewayOtaStage(
                GetString(stage, "stage"),
                GetString(stage, "state"),
                GetOptionalLong(stage, "start_offset_ms") ?? 0,
                GetOptionalLong(stage, "duration_ms") ?? 0,
                GetString(stage, "reason")));
        }
        return result;
    }

    private static IReadOnlyList<GatewayOtaSubtask> ParseSubtasks(JsonElement otaStatus)
    {
        if (!otaStatus.TryGetProperty("subtasks", out var subtasks) || subtasks.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GatewayOtaSubtask>();
        }
        var result = new List<GatewayOtaSubtask>();
        foreach (var subtask in subtasks.EnumerateArray())
        {
            if (subtask.ValueKind != JsonValueKind.Object) continue;
            result.Add(new GatewayOtaSubtask(
                GetOptionalUInt(subtask, "extender_id") ?? 0,
                GetString(subtask, "stage"),
                GetString(subtask, "result"),
                GetOptionalLong(subtask, "elapsed_ms") ?? 0,
                GetOptionalInt(subtask, "target_count") ?? 0,
                GetOptionalInt(subtask, "prepared_count") ?? 0,
                GetOptionalInt(subtask, "success_count") ?? 0,
                GetOptionalInt(subtask, "failed_count") ?? 0,
                GetString(subtask, "reason")));
        }
        return result;
    }

    private static uint? GetOptionalUInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetUInt32(out var number) ? number : null;

    private static bool TryParseSoftwareVersion(
        JsonElement element,
        out byte softwareVersion,
        out string rawVersion)
    {
        softwareVersion = 0;
        rawVersion = element.ToString();
        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetByte(out softwareVersion) &&
            softwareVersion is >= 1 and <= 254)
        {
            return true;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        rawVersion = element.GetString()?.Trim() ?? string.Empty;
        var numeric = rawVersion.StartsWith('v') || rawVersion.StartsWith('V')
            ? rawVersion[1..]
            : rawVersion;
        return !numeric.Contains('.') &&
               byte.TryParse(numeric, out softwareVersion) &&
               softwareVersion is >= 1 and <= 254;
    }

    private static bool IsMalformedPayloadException(Exception exception)
        => exception is JsonException or InvalidOperationException or FormatException or OverflowException;

    private static string BuildDownstreamTopic(string gatewayId, int sequence, OtaProtocolOptions options)
        => options.DownstreamTopicTemplate
            .Replace("{gatewayId}", gatewayId, StringComparison.Ordinal)
            .Replace("{sequence}", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
