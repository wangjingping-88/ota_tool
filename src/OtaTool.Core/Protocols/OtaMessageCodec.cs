using System.Text;
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

public sealed record GatewayOtaRequestAck(
    int TaskSequence,
    uint SessionId,
    bool Accepted,
    bool Duplicate,
    string Result,
    string Status,
    string Reason);

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
    string Reason)
{
    public string CacheResult { get; init; } = string.Empty;
}

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
    IReadOnlyList<GatewayOtaSubtask> Subtasks)
{
    public int? NodeRetryCount { get; init; }

    public int? RetryRound { get; init; }

    public IReadOnlyList<OtaFailedNode> FailedNodes { get; init; } = [];

    public string PackageSource { get; init; } = string.Empty;

    public int? CacheTargetTotal { get; init; }

    public int? CacheHitCount { get; init; }

    public long? CacheQueryElapsedMs { get; init; }

    public bool UsesCachedPackage =>
        string.Equals(PackageSource, "CACHE", StringComparison.OrdinalIgnoreCase) ||
        Stages.Any(stage =>
            string.Equals(stage.Stage, "TRANSFER", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(stage.State, "SKIPPED", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(stage.Reason, "CACHE_REUSED", StringComparison.OrdinalIgnoreCase)));
}

public sealed record GatewayExtenderInfo(
    uint ExtenderId,
    string Detail,
    byte DeviceType,
    byte SoftwareVersion);

public sealed record GatewayBasicInfo(
    uint GatewayId,
    byte SoftwareVersion,
    string RawSoftwareVersion);

public sealed record GatewayNodeInfo(ushort NodeId, byte NodeType, byte SoftwareVersion, int Rssi)
{
    public bool IsOnline => Rssi < 0;
}

public sealed record GatewayNodeList(
    uint ExtenderId,
    ushort AsyncAddress,
    IReadOnlyList<GatewayNodeInfo> Nodes,
    byte PageIndex,
    byte PageCount,
    byte TotalCount);

public sealed record GatewayExtenderStatus(
    uint ExtenderId,
    ushort AsyncAddress,
    byte SyncSoftwareVersion,
    int SyncRssi,
    sbyte SyncSnr,
    byte AsyncSoftwareVersion,
    byte OnlineCount,
    byte TotalCount);

public static class OtaMessageCodec
{
    public const int GatewayHttpAddressMaxUtf8Bytes = 255;
    public const int GatewayFileNameMaxUtf8Bytes = 63;

    public const int UpgradeCommand = 5;
    public const int UpgradeCompletionCommand = 6;
    public const int StatusQueryCommand = 8;
    public const int StatusResponseCommand = 9;
    public const int UpgradeRequestAckCommand = 10;
    public const int UserDataCommand = 100;
    // Gateway cmd=3 鉴权列表的同步端编号；不同于 WIoTa 应用帧中的 ExtenderS=3。
    public const byte GatewayAuthSyncDeviceType = 2;
    public const byte AsyncNodeListQueryCommand = 0x18;
    public const byte AsyncNodeListResponseCommand = 0x19;
    public const byte AsyncStatusQueryCommand = 0x27;
    public const byte AsyncStatusResponseCommand = 0x28;
    public const int MaxNodeListItemCount = 50;

    public static OutboundOtaMessage CreateUpgradeRequest(OtaTask task, int sequence, OtaProtocolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        options ??= new OtaProtocolOptions();
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        ArgumentException.ThrowIfNullOrWhiteSpace(task.GatewayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.PatchUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.PatchMd5);

        var transportValidationError = ValidateGatewayPatchUrl(task.PatchUrl);
        if (transportValidationError is not null)
        {
            throw new InvalidOperationException(transportValidationError);
        }
        if (task.PatchMd5.Length != 32 || !task.PatchMd5.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Patch MD5 必须为 32 位十六进制字符串。");
        }
        var patchUri = new Uri(task.PatchUrl, UriKind.Absolute);

        if (!byte.TryParse(task.OldVersion, out var oldVersion) || oldVersion is < 1 or > 254 ||
            !byte.TryParse(task.NewVersion, out var newVersion) || newVersion is < 1 or > 254 ||
            oldVersion == newVersion)
        {
            throw new InvalidOperationException("OTA 版本号必须是 1～254 且新旧版本不能相同。");
        }

        if (OtaProtocolProfiles.ValidateIpmContract(task) is { } error) throw new InvalidOperationException(error);
        var ota = new JsonObject
        {
            ["active"] = 1,
            ["type"] = task.Mode == OtaMode.IPM ? (task.PackageKind == PackageKind.FullImage ? 0 : 1) : options.OtaType,
            ["range"] = task.Mode == OtaMode.IPM && task.DeviceType != DeviceType.Gateway ? (task.Target.Scope == TargetScope.Broadcast ? 0 : 1) : options.OtaRange,
            ["new_ver"] = newVersion,
            ["old_ver"] = oldVersion,
            ["dev_type"] = ToProtocolDeviceType(task.DeviceType),
            ["md5"] = task.PatchMd5,
            ["timeout_s"] = checked((int)task.Timeout.TotalSeconds),
            ["net"] = new JsonObject
            {
                ["access"] = options.HttpAccess,
                ["addr"] = task.PatchUrl,
                ["file"] = GetGatewayPatchFileName(patchUri),
            },
        };

        if (task.DeviceType == DeviceType.Node)
        {
            if (task.NodeType is not (>= 4 and <= 63))
            {
                throw new InvalidOperationException("Node OTA 必须填写 4～63 的 node_type。");
            }
            ota["node_type"] = task.NodeType.Value;
            if (task.NodeRetryCount is not (0 or 1))
            {
                throw new InvalidOperationException("Node OTA 重试次数只能是数字 0 或 1。");
            }
            ota["node_retry_count"] = task.NodeRetryCount;
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

    public static string? ValidateGatewayPatchUrl(string patchUrl)
    {
        if (!Uri.TryCreate(patchUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return "Patch URL 必须是 HTTP 或 HTTPS 地址。";
        }

        var byteCount = Encoding.UTF8.GetByteCount(patchUrl);
        if (byteCount > GatewayHttpAddressMaxUtf8Bytes)
        {
            return $"Patch URL 长度为 {byteCount} 字节，超过 Gateway 地址缓存上限 {GatewayHttpAddressMaxUtf8Bytes} 字节。请缩短 HTTP 基地址或 Patch 文件名。";
        }

        var escapedFileName = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(escapedFileName))
        {
            return "Patch URL 中缺少文件名。";
        }
        var fileName = Uri.UnescapeDataString(escapedFileName);
        var fileNameByteCount = Encoding.UTF8.GetByteCount(fileName);
        return fileNameByteCount <= GatewayFileNameMaxUtf8Bytes
            ? null
            : $"Patch 文件名长度为 {fileNameByteCount} 字节，超过 Gateway 文件名缓存上限 {GatewayFileNameMaxUtf8Bytes} 字节。请缩短 Patch 文件名。";
    }

    private static string GetGatewayPatchFileName(Uri patchUri)
    {
        var fileName = Uri.UnescapeDataString(Path.GetFileName(patchUri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Patch URL 中缺少文件名。");
        }
        return fileName;
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

    public static OutboundOtaMessage CreateAsyncNodeListQuery(
        string gatewayId,
        int querySequence,
        uint extenderId,
        OtaProtocolOptions? options = null)
    {
        return CreateUserDataQuery(
            gatewayId,
            querySequence,
            extenderId,
            CreateApplicationQueryHex(AsyncNodeListQueryCommand),
            options);
    }

    public static OutboundOtaMessage CreateAsyncStatusQuery(
        string gatewayId,
        int querySequence,
        uint extenderId,
        OtaProtocolOptions? options = null)
    {
        return CreateUserDataQuery(
            gatewayId,
            querySequence,
            extenderId,
            CreateApplicationQueryHex(AsyncStatusQueryCommand),
            options);
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

    public static bool TryParseAsyncNodeListResponse(string json, out GatewayNodeList? nodeList)
        => TryParseAsyncNodeListResponse(json, out nodeList, out _, out _);

    public static bool TryParseAsyncNodeListResponse(
        string json,
        out GatewayNodeList? nodeList,
        out uint sourceExtenderId,
        out string? protocolError)
        => TryParseAsyncNodeListResponse(
            json,
            null,
            out nodeList,
            out sourceExtenderId,
            out protocolError);

    public static bool TryParseAsyncNodeListResponse(
        string json,
        uint expectedExtenderId,
        out GatewayNodeList? nodeList,
        out string? protocolError)
    {
        if (expectedExtenderId == 0) throw new ArgumentOutOfRangeException(nameof(expectedExtenderId));
        return TryParseAsyncNodeListResponse(
            json,
            expectedExtenderId,
            out nodeList,
            out _,
            out protocolError);
    }

    private static bool TryParseAsyncNodeListResponse(
        string json,
        uint? expectedExtenderId,
        out GatewayNodeList? nodeList,
        out uint sourceExtenderId,
        out string? protocolError)
    {
        nodeList = null;
        sourceExtenderId = 0;
        protocolError = null;
        if (!TryParseUserDataFrame(
                json,
                AsyncNodeListResponseCommand,
                expectedExtenderId,
                out sourceExtenderId,
                out var asyncAddress,
                out var data,
                out protocolError))
        {
            return false;
        }

        if (data.Length == 0)
        {
            protocolError = "0x19 设备列表缺少数量信息。";
            return false;
        }
        var count = data[0];
        if (count > MaxNodeListItemCount)
        {
            protocolError = $"0x19 设备列表包含 {count} 项，超过协议容量上限 {MaxNodeListItemCount} 项。";
            return false;
        }

        if (data.Length == 1 + count * 5)
        {
            protocolError = "0x19 设备列表缺少 2.0 协议的4字节分页头。";
            return false;
        }
        if (data.Length < 4)
        {
            protocolError = "0x19 设备列表缺少4字节分页头。";
            return false;
        }
        var pageIndex = data[1];
        var pageCount = data[2];
        var totalCount = data[3];
        const int nodeDataOffset = 4;
        if (pageCount is 0 or > 6 || pageIndex >= pageCount)
        {
            protocolError = $"0x19 分页信息非法：页号 {pageIndex}，总页数 {pageCount}。";
            return false;
        }
        if ((totalCount == 0 && (count != 0 || pageIndex != 0 || pageCount != 1)) ||
            (totalCount > 0 && (count == 0 || totalCount < count || totalCount > pageCount * MaxNodeListItemCount)))
        {
            protocolError = $"0x19 数量信息非法：本页 {count}，总数 {totalCount}，总页数 {pageCount}。";
            return false;
        }
        if (data.Length != nodeDataOffset + count * 5)
        {
            protocolError = $"0x19 数据结构长度错误：分页头声明 {count} 项，实际数据 {data.Length} 字节。";
            return false;
        }

        var nodes = new List<GatewayNodeInfo>(count);
        var nodeKeys = new HashSet<(byte NodeType, ushort NodeId)>();
        for (var index = 0; index < count; index++)
        {
            var offset = nodeDataOffset + index * 5;
            var nodeType = data[offset];
            var nodeId = (ushort)(data[offset + 1] | data[offset + 2] << 8);
            var rssiAbsolute = data[offset + 3];
            var softwareVersion = data[offset + 4];
            if (nodeType is < 4 or > 63 || rssiAbsolute > 200)
            {
                protocolError = "0x19 包含非法 Node 类型或 RSSI。";
                return false;
            }
            if (nodeId == 0)
            {
                protocolError = "0x19 包含零 Node ID。";
                return false;
            }
            if (!nodeKeys.Add((nodeType, nodeId)))
            {
                protocolError = $"0x19 包含重复 Node：类型 {nodeType}，Node ID {nodeId}。";
                return false;
            }
            nodes.Add(new GatewayNodeInfo(
                nodeId,
                nodeType,
                softwareVersion,
                rssiAbsolute == 0 ? 0 : -rssiAbsolute));
        }

        nodeList = new GatewayNodeList(sourceExtenderId, asyncAddress, nodes, pageIndex, pageCount, totalCount);
        return true;
    }

    public static bool TryParseAsyncStatusResponse(string json, out GatewayExtenderStatus? status)
        => TryParseAsyncStatusResponse(json, null, out status, out _);

    public static bool TryParseAsyncStatusResponse(
        string json,
        uint expectedExtenderId,
        out GatewayExtenderStatus? status,
        out string? protocolError)
    {
        if (expectedExtenderId == 0) throw new ArgumentOutOfRangeException(nameof(expectedExtenderId));
        return TryParseAsyncStatusResponse(json, (uint?)expectedExtenderId, out status, out protocolError);
    }

    private static bool TryParseAsyncStatusResponse(
        string json,
        uint? expectedExtenderId,
        out GatewayExtenderStatus? status,
        out string? protocolError)
    {
        status = null;
        protocolError = null;
        if (!TryParseUserDataFrame(
                json,
                AsyncStatusResponseCommand,
                expectedExtenderId,
                out var extenderId,
                out var asyncAddress,
                out var data,
                out protocolError))
        {
            return false;
        }
        if (data.Length != 6)
        {
            protocolError = $"0x28 状态数据长度错误：预期 6 字节，实际 {data.Length} 字节。";
            return false;
        }

        var syncRssiAbsolute = data[1];
        var syncSnr = unchecked((sbyte)data[2]);
        var onlineCount = data[4];
        var totalCount = data[5];
        if (syncRssiAbsolute > 200 ||
            syncSnr is < -30 or > 30 ||
            onlineCount > totalCount)
        {
            protocolError = "0x28 包含非法 RSSI、SNR 或 Node 数量。";
            return false;
        }

        status = new GatewayExtenderStatus(
            extenderId,
            asyncAddress,
            data[0],
            syncRssiAbsolute == 0 ? 0 : -syncRssiAbsolute,
            syncSnr,
            data[3],
            onlineCount,
            totalCount);
        return true;
    }

    public static bool TryParseUserDataQuery(string json, out uint extenderId, out byte applicationCommand)
    {
        extenderId = 0;
        applicationCommand = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("cmd", out var command) ||
                !command.TryGetInt32(out var commandValue) ||
                commandValue != UserDataCommand ||
                !root.TryGetProperty("dst", out var destination) ||
                !destination.TryGetUInt32(out extenderId) ||
                extenderId == 0 ||
                !root.TryGetProperty("fmt", out var format) ||
                format.ValueKind != JsonValueKind.String ||
                !string.Equals(format.GetString(), "hex", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("uc", out var userData) ||
                userData.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            var bytes = Convert.FromHexString(userData.GetString() ?? string.Empty);
            if (bytes.Length != 3 ||
                !TryParseApplicationHeader(bytes, out var property, out applicationCommand, out var sourceType, out var destinationType) ||
                property != 0 || sourceType != (byte)FirmwareDeviceType.Server ||
                destinationType != (byte)FirmwareDeviceType.ExtenderA)
            {
                return false;
            }
            return applicationCommand is AsyncNodeListQueryCommand or AsyncStatusQueryCommand;
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
                subtasks)
            {
                NodeRetryCount = GetOptionalInt(otaStatus, "node_retry_count"),
                RetryRound = GetOptionalInt(otaStatus, "retry_round"),
                FailedNodes = ParseFailedNodes(otaStatus),
                PackageSource = GetString(otaStatus, "package_source"),
                CacheTargetTotal = GetOptionalNonNegativeInt(otaStatus, "cache_target_total"),
                CacheHitCount = GetOptionalNonNegativeInt(otaStatus, "cache_hit_count"),
                CacheQueryElapsedMs = GetOptionalNonNegativeLong(otaStatus, "cache_query_elapsed_ms"),
            };
            return true;
        }
        catch (Exception exception) when (IsMalformedPayloadException(exception))
        {
            return false;
        }
    }

    public static bool TryParseGatewayOtaRequestAck(string json, out GatewayOtaRequestAck? ack)
    {
        ack = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                return TryParseGatewayOtaRequestAckElement(root, out ack);
            }
            if (root.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            foreach (var element in root.EnumerateArray())
            {
                if (TryParseGatewayOtaRequestAckElement(element, out ack)) return true;
            }
            return false;
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
        DeviceType.Async or DeviceType.Mcu => "ex_mcu",
        DeviceType.Node => "node",
        _ => throw new ArgumentOutOfRangeException(nameof(deviceType)),
    };

    private static string GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static bool TryParseGatewayOtaRequestAckElement(JsonElement root, out GatewayOtaRequestAck? ack)
    {
        ack = null;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("cmd", out var command) ||
            !command.TryGetInt32(out var commandValue) ||
            commandValue != UpgradeRequestAckCommand ||
            !root.TryGetProperty("ota_ack", out var ackElement) ||
            ackElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var taskSequence = GetInt(ackElement, "task_seq");
        var sessionId = GetOptionalUInt(ackElement, "session_id") ?? 0;
        var accepted = GetOptionalInt(ackElement, "accepted");
        var duplicate = GetOptionalInt(ackElement, "duplicate");
        var result = GetString(ackElement, "result");
        var status = GetString(ackElement, "status");
        var reason = GetString(ackElement, "reason");
        if (taskSequence <= 0 || accepted is not (0 or 1) || duplicate is not (0 or 1) ||
            (accepted == 1 && sessionId == 0) ||
            string.IsNullOrWhiteSpace(result) || string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        ack = new GatewayOtaRequestAck(
            taskSequence,
            sessionId,
            accepted == 1,
            duplicate == 1,
            result,
            status,
            reason);
        return true;
    }

    private static int GetInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static int? GetOptionalInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static long? GetOptionalLong(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static int? GetOptionalNonNegativeInt(JsonElement element, string name)
        => GetOptionalInt(element, name) is { } value && value >= 0 ? value : null;

    private static long? GetOptionalNonNegativeLong(JsonElement element, string name)
        => GetOptionalLong(element, name) is { } value && value >= 0 ? value : null;

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

    private static IReadOnlyList<OtaFailedNode> ParseFailedNodes(JsonElement otaStatus)
    {
        if (!otaStatus.TryGetProperty("failed_node_list", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<OtaFailedNode>();
        }

        var result = new List<OtaFailedNode>();
        var unique = new HashSet<(uint ExtenderId, byte NodeType, ushort NodeId)>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            var extenderId = GetOptionalUInt(node, "extender_id");
            var nodeType = GetOptionalInt(node, "node_type");
            var nodeId = GetOptionalInt(node, "node_id");
            if (extenderId is not > 0 ||
                nodeType is not (>= 4 and <= 63) ||
                nodeId is not (>= 1 and <= ushort.MaxValue))
            {
                continue;
            }

            var key = (extenderId.Value, (byte)nodeType.Value, (ushort)nodeId.Value);
            if (unique.Add(key)) result.Add(new OtaFailedNode(key.Item1, key.Item2, key.Item3));
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
                GetString(subtask, "reason"))
            {
                CacheResult = GetString(subtask, "cache_result"),
            });
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

    private static OutboundOtaMessage CreateUserDataQuery(
        string gatewayId,
        int querySequence,
        uint extenderId,
        string userDataHex,
        OtaProtocolOptions? options)
    {
        options ??= new OtaProtocolOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);
        if (querySequence <= 0) throw new ArgumentOutOfRangeException(nameof(querySequence));
        if (extenderId == 0) throw new ArgumentOutOfRangeException(nameof(extenderId));
        var root = new JsonObject
        {
            ["cmd"] = UserDataCommand,
            ["ver"] = "v2.0",
            ["src"] = 0,
            ["dst"] = extenderId,
            ["fmt"] = "hex",
            ["uc"] = userDataHex,
        };
        return new OutboundOtaMessage(
            BuildDownstreamTopic(gatewayId, querySequence, options),
            root.ToJsonString(),
            querySequence);
    }

    private static string CreateApplicationQueryHex(byte command)
    {
        var header = (uint)command << 6 |
                     (uint)FirmwareDeviceType.ExtenderA << 18;
        return Convert.ToHexString([
            (byte)header,
            (byte)(header >> 8),
            (byte)(header >> 16),
        ]);
    }

    private static bool TryParseUserDataFrame(
        string json,
        byte expectedCommand,
        uint? expectedExtenderId,
        out uint extenderId,
        out ushort asyncAddress,
        out byte[] data,
        out string? protocolError)
    {
        extenderId = 0;
        asyncAddress = 0;
        data = Array.Empty<byte>();
        protocolError = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var parsed = TryParseUserDataFrameElement(
                    root,
                    expectedCommand,
                    out extenderId,
                    out asyncAddress,
                    out data,
                    out protocolError);
                if (expectedExtenderId.HasValue && extenderId != expectedExtenderId.Value)
                {
                    extenderId = 0;
                    asyncAddress = 0;
                    data = Array.Empty<byte>();
                    protocolError = null;
                    return false;
                }
                return parsed;
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            uint firstErrorSource = 0;
            string? firstProtocolError = null;
            foreach (var element in root.EnumerateArray())
            {
                if (TryParseUserDataFrameElement(
                        element,
                        expectedCommand,
                        out var elementSource,
                        out var elementAddress,
                        out var elementData,
                        out var elementError))
                {
                    if (expectedExtenderId.HasValue && elementSource != expectedExtenderId.Value)
                    {
                        continue;
                    }
                    extenderId = elementSource;
                    asyncAddress = elementAddress;
                    data = elementData;
                    return true;
                }

                if ((!expectedExtenderId.HasValue || elementSource == expectedExtenderId.Value) &&
                    firstProtocolError is null &&
                    elementSource != 0 &&
                    !string.IsNullOrWhiteSpace(elementError))
                {
                    firstErrorSource = elementSource;
                    firstProtocolError = elementError;
                }
            }

            extenderId = firstErrorSource;
            protocolError = firstProtocolError;
            return false;
        }
        catch (Exception exception) when (IsMalformedPayloadException(exception))
        {
            return false;
        }
    }

    private static bool TryParseUserDataFrameElement(
        JsonElement root,
        byte expectedCommand,
        out uint extenderId,
        out ushort asyncAddress,
        out byte[] data,
        out string? protocolError)
    {
        extenderId = 0;
        asyncAddress = 0;
        data = Array.Empty<byte>();
        protocolError = null;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("cmd", out var command) ||
                !command.TryGetInt32(out var commandValue) ||
                commandValue != UserDataCommand ||
                !root.TryGetProperty("src", out var source) ||
                !source.TryGetUInt32(out extenderId) ||
                extenderId == 0 ||
                !root.TryGetProperty("data", out var payload) ||
                payload.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("fmt", out var format) ||
                format.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var encoded = payload.GetString() ?? string.Empty;
        var formatValue = format.GetString();
        byte[] frame;
        try
        {
            if (string.Equals(formatValue, "hex", StringComparison.OrdinalIgnoreCase))
            {
                if (encoded.Length == 0 || (encoded.Length & 1) != 0)
                {
                    protocolError = "cmd=100 包含空数据或奇数长度 Hex。";
                    return false;
                }
                frame = Convert.FromHexString(encoded);
            }
            else if (string.Equals(formatValue, "base64", StringComparison.OrdinalIgnoreCase))
            {
                frame = Convert.FromBase64String(encoded);
            }
            else
            {
                protocolError = $"cmd=100 不支持 fmt={formatValue}。";
                return false;
            }
        }
        catch (FormatException)
        {
            protocolError = $"cmd=100 包含非法 {formatValue} 数据。";
            return false;
        }

        if (frame.Length < 6 ||
            !TryParseApplicationHeader(frame, out var property, out var applicationCommand, out var sourceType, out var destinationType) ||
            (property & 0x20) != 0 ||
            (property & 0x09) != 0x09 ||
            applicationCommand != expectedCommand ||
            sourceType != (byte)FirmwareDeviceType.ExtenderA ||
            destinationType != (byte)FirmwareDeviceType.Server)
        {
            protocolError = "cmd=100 应用帧 Header、Cmd 或地址类型不符合预期。";
            return false;
        }

        var offset = 3;
        if ((property & 0x10) != 0)
        {
            if (offset >= frame.Length || frame[offset++] == 0)
            {
                protocolError = "cmd=100 应用帧包序号无效。";
                return false;
            }
        }
        if (offset + 2 > frame.Length)
        {
            protocolError = "cmd=100 应用帧缺少源地址。";
            return false;
        }
        asyncAddress = (ushort)(frame[offset] | frame[offset + 1] << 8);
        offset += 2;
        if (asyncAddress == 0)
        {
            protocolError = "cmd=100 应用帧源地址不能为零。";
            return false;
        }
        if ((property & 0x04) != 0)
        {
            if (offset >= frame.Length || frame[offset] == 0 ||
                offset + 1 + frame[offset] * 2 > frame.Length)
            {
                protocolError = "cmd=100 应用帧目的地址列表长度无效。";
                return false;
            }
            offset += 1 + frame[offset] * 2;
        }
        if ((property & 0x02) != 0)
        {
            if (offset >= frame.Length || frame[offset] is < 1 or > 44)
            {
                protocolError = "cmd=100 应用帧唤醒周期必须为 1～44 秒。";
                return false;
            }
            offset++;
        }
        if (offset >= frame.Length)
        {
            protocolError = "cmd=100 应用帧缺少 DataLen。";
            return false;
        }
        var dataLength = frame[offset++];
        if (frame.Length != offset + dataLength)
        {
            if (expectedCommand == AsyncNodeListResponseCommand &&
                frame.Length > offset &&
                frame[offset] > MaxNodeListItemCount)
            {
                protocolError = $"0x19 设备列表包含 {frame[offset]} 项，超过协议容量上限 {MaxNodeListItemCount} 项。";
            }
            else
            {
                protocolError = $"cmd=100 应用帧 DataLen={dataLength}，实际数据长度为 {frame.Length - offset}。";
            }
            return false;
        }
        data = frame[offset..];
        return true;
    }

    private static bool TryParseApplicationHeader(
        ReadOnlySpan<byte> frame,
        out byte property,
        out byte command,
        out byte sourceType,
        out byte destinationType)
    {
        property = 0;
        command = 0;
        sourceType = 0;
        destinationType = 0;
        if (frame.Length < 3)
        {
            return false;
        }
        var header = frame[0] | frame[1] << 8 | frame[2] << 16;
        property = (byte)(header & 0x3F);
        command = (byte)((header >> 6) & 0x3F);
        sourceType = (byte)((header >> 12) & 0x3F);
        destinationType = (byte)((header >> 18) & 0x3F);
        return true;
    }

    private static string BuildDownstreamTopic(string gatewayId, int sequence, OtaProtocolOptions options)
        => options.DownstreamTopicTemplate
            .Replace("{gatewayId}", gatewayId, StringComparison.Ordinal)
            .Replace("{sequence}", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
