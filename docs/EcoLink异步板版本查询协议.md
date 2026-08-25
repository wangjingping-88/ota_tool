# EcoLink `cmd=100` 异步查询协议

## 查询职责

- Gateway `cmd=3` 鉴权列表仍用于取得 Sync 完整 WIoTa ID，以及 Sync 升级所需的原始版本字节。
- Node 列表使用 `cmd=100 + 0x0E/0x0F`。
- Sync/Async 状态使用 `cmd=100 + 0x17/0x18`；Async 升级以响应中的 `async_version` 为准。
- 工具不再发送或解析旧的 `cmd=10～13`，也不在新查询失败时回退旧协议。

## MQTT 透传封装

Node 列表请求：

```json
{
  "cmd": 100,
  "ver": "v2.0",
  "src": 0,
  "dst": 1821373,
  "fmt": "hex",
  "uc": "C00104"
}
```

状态请求仅将 `uc` 改为 `E00204`。`dst` 必须是鉴权列表中的完整 Sync ID；MQTT 主题末尾序号只用于发布，不能作为响应事务号。

工具兼容 Gateway 直接返回单个 JSON 对象，以及 MQTT 服务将一个或多个上行项包装为 JSON 数组的格式；数组会逐项解析。只处理同时包含 `cmd=100`、`src`、`fmt` 和 `data` 的上行项，仅含 `uc.res` 的发送 ACK 会被忽略。响应允许 `fmt=hex` 或 `fmt=base64`，拒绝 `string`、奇数长度 Hex 和非法 Base64。

## 应用帧校验

三字节 Header 按小端 24 位整数解析：

- `Property`：bit 0～4，响应固定为 `0x09`。
- `Cmd`：bit 5～11，设备列表为 `0x0F`，状态为 `0x18`。
- `SrcType`：bit 12～17，响应固定为 `1`。
- `DestType`：bit 18～23，响应固定为 `0`。

Header 后依次为 Async 应用地址（小端 16 位）、一字节 `DataLen` 和数据区。工具要求 `DataLen` 与实际帧长度完全相等，不接受截断和尾随字节。

### `0x0F` Node 列表

数据区为：

```text
设备数 + N × (Node 类型 + Node ID LE16 + RSSI 绝对值 + 软件版本)
```

- 单帧最多 50 项；51 项及以上明确作为协议容量错误，不截断。
- Node ID 必须非零且不重复，类型范围为 2～63。
- RSSI 绝对值范围为 0～200；`0` 表示未上线，解析时保留语义，在返回界面前过滤。
- 软件版本 `0/255` 作为“未知版本”显示，不能参与升级。

### `0x18` Sync/Async 状态

数据区固定 6 字节：

```text
sync_version + sync_rssi_abs + sync_snr_i8 + async_version + online_count + total_count
```

界面显示 Sync/Async 版本、Sync RSSI/SNR，以及在线数/总数。原始版本字节继续用于 Patch 校验和 MQTT 下发；显示时按十进制一位小数转换，例如 `1 → 0.1`、`23 → 2.3`。

## 调度与失败处理

- 同一完整 Extender ID 的 `0x0E` 与 `0x17` 使用同一把查询锁，严格串行。
- 不同 Extender 并行查询。
- 响应按 Gateway 上行主题、顶层 `src` 和应用层 Cmd 关联。
- 每次等待 5 秒，最多两次；重试前保留 500 ms 过期响应排空期。
- 完成、超时或取消后立即注销处理器，重复响应不能覆盖已完成结果。
- 单板失败不丢弃其他 Extender 的成功结果；全部 `0x18` 查询失败时清空 Async 可升级目标。

## 固件联调前提

Gateway、Sync、Async 和工具必须成套升级：

1. Async 的 `0x0F` 必须返回文档规定的 5 字节设备项，并输出 RSSI 绝对值。
2. Async 必须实现 `0x17 → 0x18` 固定 6 字节状态组包。
3. 未完成上述固件修改时，工具会严格超时并禁止 Async 升级，不会生成伪造结果或回退旧协议。
