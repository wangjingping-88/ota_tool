# EcoLink 异步板版本查询协议

## 背景

Gateway 鉴权列表中的 `software_version` 表示 Extender 同步板版本，不能用于判断同一块 Extender 上异步板的当前版本。桌面工具在“拓展器-异步升级”模式刷新 Extender 时，需要在取得鉴权列表后逐个查询异步板版本；同步升级仍只读取鉴权列表。

## MQTT 请求

- 命令：`cmd=12`
- 下行主题：`ucchip/down/sgw/{gateway_id}/{query_seq}`
- `query_seq`：工具生成的正整数事务号。
- `extender_id`：鉴权列表中对应 Extender 的设备 ID。

```json
{
  "cmd": 12,
  "ver": "v2.0",
  "src": 0,
  "dst": 0,
  "async_version": {
    "query_seq": 2038,
    "extender_id": 1821373
  }
}
```

Gateway 应将查询转发到目标 Sync，再由 Sync 通过内部串口向 Async 查询当前 `SOFTWARE_VERSION`。

## MQTT 响应

- 命令：`cmd=13`
- 上行主题：`ucchip/up/sgw/{gateway_id}/{query_seq}`
- `query_seq` 和 `extender_id` 必须原样返回，工具据此关联并发查询。
- 成功时 `software_version` 必须为 `1～254`。

```json
{
  "cmd": 13,
  "ver": "v2.0",
  "src": 0,
  "dst": 0,
  "async_version": {
    "query_seq": 2038,
    "extender_id": 1821373,
    "result": "OK",
    "reason": "NONE",
    "software_version": 2
  }
}
```

失败响应不携带有效版本，示例：

```json
{
  "cmd": 13,
  "ver": "v2.0",
  "src": 0,
  "dst": 0,
  "async_version": {
    "query_seq": 2038,
    "extender_id": 1821373,
    "result": "FAILED",
    "reason": "TIMEOUT",
    "software_version": 0
  }
}
```

## 工具行为

1. 同步升级：刷新鉴权列表后直接使用同步板版本，不发送 `cmd=12`。
2. 异步升级：先刷新鉴权列表，再对在线 Extender 并发发送 `cmd=12`。
3. 仅版本查询成功的 Extender 可进入异步升级目标列表；部分失败会明确提示失败数量。
4. 所有查询均失败时清空发现列表，避免把同步版本误认为异步版本。
5. 正向、反向按钮高亮、启动前校验和成功后的本地版本更新均使用与升级类型对应的版本。

> 当前仓库完成的是桌面工具端协议和处理逻辑。Gateway、Sync、Async 固件也必须实现上述 `cmd=12/13` 转发与应答，旧固件不会返回异步板版本。
