# Gateway 升级前版本校验

## 查询方式

桌面工具在 EcoLink 网关升级页面提供“刷新 Gateway”按钮。用户点击后发送基础信息查询：

```json
{
  "cmd": 3,
  "ver": "v2.0",
  "src": 0,
  "dst": 0,
  "query": "base"
}
```

工具从响应的 `base` 对象读取以下字段，优先级从高到低：

1. `ota_software_version`
2. `software_version`
3. `sw_ver`

推荐固件明确返回单字节数值字段：

```json
{
  "cmd": 3,
  "ver": "v2.0",
  "src": 704027,
  "base": {
    "dev_id": 704027,
    "ota_software_version": 2,
    "sw_ver": "v1.3.1"
  }
}
```

`ota_software_version` 必须为 `1～254`，并与 Gateway 固件构建参数 `gateway_software_version` 一致。`sw_ver` 可继续保存产品语义版本；类似 `v1.3.1` 的多段版本不会被工具误认为 OTA 版本。为兼容旧固件，单段字符串 `1` 或 `v1` 可以作为 OTA 版本解析。

## 校验规则

- 当前版本与 Patch `old_ver` 一致：对应方向的升级按钮高亮，并允许进入升级确认。
- 当前版本与 Patch `old_ver` 不一致：拒绝启动，并同时显示实际版本和 Patch 要求版本。
- 基础信息超时、Gateway ID 不匹配或版本字段无效：拒绝启动。
- 未执行手动刷新或 Gateway ID 已变更：正向、反向和循环升级按钮保持禁用，不在启动时自动查询。
- 循环升级使用首轮开始前最近一次手动查询结果；之后只有前一步成功才会进入下一方向，因此按成功结果推进本地版本状态。
- Gateway 升级成功后，工具将本次查询缓存更新为 Patch `new_ver`，但下次新任务启动前仍会重新查询设备事实版本。
