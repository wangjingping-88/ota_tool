# Gateway 到 Sync 缓存复用状态对接说明

## 1. 目的

本文定义 Gateway 在启用“OTA 包缓存复用”后，如何通过 `cmd=9` OTA 状态响应向桌面工具报告包来源、缓存命中情况和阶段状态。

对接目标：

- 缓存全部命中时，工具明确显示“已复用缓存，跳过 Gateway to Sync 数据传输”。
- 缓存未命中或查询失败时，工具继续显示原有完整传输进度。
- Async、Node 的下游传输继续由 `subtasks` 显示，不与 Gateway 到 Sync 的顶层 `TRANSFER` 混用。
- 新工具兼容未提供缓存字段的旧 Gateway。

本文只扩展 `cmd=9` 响应，不修改 `cmd=8` 查询格式。

## 2. 状态响应新增字段

新增字段均位于 `ota_status` 对象内。

| 字段 | 类型 | 必填条件 | 说明 |
|---|---:|---|---|
| `package_source` | string | 缓存决策完成后必填 | `CACHE` 表示全部目标命中并复用；`TRANSFER` 表示执行完整传输 |
| `cache_target_total` | integer | 进入缓存查询后建议必填 | 本次参与缓存查询的目标 Sync 总数，必须大于等于 0 |
| `cache_hit_count` | integer | 进入缓存查询后建议必填 | 已确认合法 `HIT` 的目标数，范围为 `0..cache_target_total` |
| `cache_query_elapsed_ms` | integer | 进入缓存查询后建议必填 | 缓存查询累计耗时，单位毫秒，必须大于等于 0 |

每个 `subtasks[]` 对象增加可选字段：

| 字段 | 类型 | 取值 | 说明 |
|---|---:|---|---|
| `cache_result` | string | `HIT`、`MISS`、`BUSY`、`ERROR`、`TIMEOUT` | 对应 Extender 最近一次缓存查询事实；尚无结果时省略或传空字符串 |

约束：

- `package_source=CACHE` 仅允许在所有目标均返回合法 `HIT` 后设置。
- 任一目标为 `MISS/BUSY/ERROR/TIMEOUT` 时，必须设置 `package_source=TRANSFER` 并回退完整传输。
- 一期不支持部分复用，不能出现一部分 Sync 复用、一部分 Sync 接收完整包的状态。
- `package_source` 在同一 `session_id` 内一旦确定，不得从 `CACHE` 和 `TRANSFER` 之间再次切换。

## 3. 阶段状态约定

### 3.1 缓存查询期间

缓存查询复用现有顶层 `PREPARE` 阶段：

- `status=RUNNING`
- `stage=PREPARE`
- `PREPARE.state=RUNNING`
- `package_source` 可暂时省略或传空字符串
- `cache_target_total`、`cache_hit_count` 和 `cache_query_elapsed_ms` 随查询更新

### 3.2 全部命中并复用

全部目标 `HIT` 后，Gateway 必须将顶层 Gateway 到 Sync 传输阶段标记为跳过：

```json
{
  "stage": "TRANSFER",
  "state": "SKIPPED",
  "start_offset_ms": 0,
  "duration_ms": 0,
  "reason": "CACHE_REUSED"
}
```

推荐状态流转：

1. 将 `PREPARE` 标记为 `PASSED`。
2. 将 `TRANSFER` 标记为 `SKIPPED`，原因设置为 `CACHE_REUSED`，耗时设置为 `0`。
3. 将当前顶层阶段推进到 `REPAIR`，再进入原 Async/Node 下游执行阶段。
4. 下游上报的 `PREPARE/TRANSFER` 只更新相应 `subtasks[]`，不得重新激活顶层 `TRANSFER`。

如果当前 Gateway 暂时不能增加 `SKIPPED` 枚举，可兼容上报：

```json
{
  "stage": "TRANSFER",
  "state": "PASSED",
  "start_offset_ms": 0,
  "duration_ms": 0,
  "reason": "CACHE_REUSED"
}
```

工具在 `package_source=CACHE` 或 `reason=CACHE_REUSED` 时，会统一显示为“已跳过”。

### 3.3 回退完整传输

任一目标未命中或查询失败时：

- `package_source=TRANSFER`
- `TRANSFER` 按原逻辑从 `PENDING` 进入 `RUNNING`，完成后进入 `PASSED`
- `transferred_bytes` 和 `file_size` 继续用于 Gateway 到 Sync 传输进度
- `cache_hit_count` 保留决策时的真实命中数，不得伪造成 0 或总数

### 3.4 `transferred_bytes` 语义

当 `package_source=TRANSFER` 时，`transferred_bytes` 表示 Gateway 到 Sync 已发送的包字节数，工具显示进度条。

当 `package_source=CACHE` 时，Gateway 到 Sync 实际传输字节数为 0。工具会隐藏顶层传输进度条；下游传输量应保留在对应 `subtasks[]` 中，不应使用顶层 `transferred_bytes` 冒充 Gateway 到 Sync 传输量。

## 4. 完整响应示例

### 4.1 缓存查询中

```json
{
  "cmd": 9,
  "ota_status": {
    "query_seq": 842,
    "task_seq": 801,
    "session_id": 17400001,
    "result": "OK",
    "status": "RUNNING",
    "stage": "PREPARE",
    "task_elapsed_ms": 1250,
    "file_size": 18293,
    "transferred_bytes": 0,
    "cache_target_total": 2,
    "cache_hit_count": 1,
    "cache_query_elapsed_ms": 320,
    "stages": [
      {
        "stage": "PREPARE",
        "state": "RUNNING",
        "start_offset_ms": 930,
        "duration_ms": 320,
        "reason": ""
      },
      {
        "stage": "TRANSFER",
        "state": "PENDING",
        "start_offset_ms": 0,
        "duration_ms": 0,
        "reason": ""
      }
    ],
    "subtasks": [
      {
        "extender_id": 1821362,
        "stage": "PREPARE",
        "result": "RUNNING",
        "elapsed_ms": 1250,
        "target_count": 1,
        "prepared_count": 0,
        "success_count": 0,
        "failed_count": 0,
        "reason": "",
        "cache_result": "HIT"
      },
      {
        "extender_id": 1821373,
        "stage": "PREPARE",
        "result": "RUNNING",
        "elapsed_ms": 1250,
        "target_count": 1,
        "prepared_count": 0,
        "success_count": 0,
        "failed_count": 0,
        "reason": "",
        "cache_result": ""
      }
    ]
  }
}
```

### 4.2 全部命中并跳过 Gateway 到 Sync 传输

```json
{
  "cmd": 9,
  "ota_status": {
    "query_seq": 843,
    "task_seq": 801,
    "session_id": 17400001,
    "result": "OK",
    "status": "RUNNING",
    "stage": "REPAIR",
    "task_elapsed_ms": 2480,
    "file_size": 18293,
    "transferred_bytes": 0,
    "package_source": "CACHE",
    "cache_target_total": 2,
    "cache_hit_count": 2,
    "cache_query_elapsed_ms": 410,
    "stages": [
      {
        "stage": "PREPARE",
        "state": "PASSED",
        "start_offset_ms": 930,
        "duration_ms": 410,
        "reason": ""
      },
      {
        "stage": "TRANSFER",
        "state": "SKIPPED",
        "start_offset_ms": 0,
        "duration_ms": 0,
        "reason": "CACHE_REUSED"
      },
      {
        "stage": "REPAIR",
        "state": "RUNNING",
        "start_offset_ms": 1340,
        "duration_ms": 1140,
        "reason": ""
      }
    ],
    "subtasks": [
      {
        "extender_id": 1821362,
        "stage": "TRANSFER",
        "result": "RUNNING",
        "elapsed_ms": 2480,
        "target_count": 5,
        "prepared_count": 5,
        "success_count": 0,
        "failed_count": 0,
        "reason": "",
        "cache_result": "HIT"
      },
      {
        "extender_id": 1821373,
        "stage": "PREPARE",
        "result": "RUNNING",
        "elapsed_ms": 2480,
        "target_count": 5,
        "prepared_count": 3,
        "success_count": 0,
        "failed_count": 0,
        "reason": "",
        "cache_result": "HIT"
      }
    ]
  }
}
```

工具显示：

- 包来源：`Sync 缓存 · 缓存命中 2/2 · 查询耗时 0分0秒410毫秒`
- 顶层 `TRANSFER`：`缓存复用 / 已跳过 / Sync 本地缓存 / CACHE_REUSED`
- 不显示 Gateway 到 Sync 传输进度条
- Extender 子任务继续显示各自的下游阶段

### 4.3 未命中并回退完整传输

```json
{
  "cmd": 9,
  "ota_status": {
    "query_seq": 844,
    "task_seq": 802,
    "session_id": 17400002,
    "result": "OK",
    "status": "RUNNING",
    "stage": "TRANSFER",
    "task_elapsed_ms": 5200,
    "file_size": 18293,
    "transferred_bytes": 9216,
    "package_source": "TRANSFER",
    "cache_target_total": 2,
    "cache_hit_count": 1,
    "cache_query_elapsed_ms": 5030,
    "stages": [
      {
        "stage": "TRANSFER",
        "state": "RUNNING",
        "start_offset_ms": 5030,
        "duration_ms": 170,
        "reason": ""
      }
    ],
    "subtasks": [
      {
        "extender_id": 1821362,
        "stage": "PREPARE",
        "result": "PENDING",
        "elapsed_ms": 5200,
        "target_count": 5,
        "prepared_count": 0,
        "success_count": 0,
        "failed_count": 0,
        "reason": "",
        "cache_result": "HIT"
      },
      {
        "extender_id": 1821373,
        "stage": "PREPARE",
        "result": "PENDING",
        "elapsed_ms": 5200,
        "target_count": 5,
        "prepared_count": 0,
        "success_count": 0,
        "failed_count": 0,
        "reason": "",
        "cache_result": "MISS"
      }
    ]
  }
}
```

## 5. 工具端兼容规则

工具按以下优先级识别缓存复用：

1. `package_source` 等于 `CACHE`。
2. 顶层 `TRANSFER.state` 等于 `SKIPPED`。
3. 顶层 `TRANSFER.reason` 等于 `CACHE_REUSED`。

只要满足其中之一，工具就会：

- 将顶层 `TRANSFER` 名称显示为“缓存复用”。
- 将状态显示为“已跳过”。
- 将方向显示为“Sync 本地缓存”。
- 将原因显示为“已复用缓存，跳过 Gateway to Sync 数据传输”。
- 隐藏顶层传输进度条。
- 在导出的报告阶段时间线中保留相同语义。

旧 Gateway 未提供任何新增字段时，工具继续按原逻辑显示“数据传输 / 网关 to Sync”，不会阻止升级任务。

## 6. Gateway 实现注意事项

### 6.1 固定阶段表

Gateway 可以继续上报固定阶段表，但缓存命中后不得让 `TRANSFER` 长期保持 `PENDING`，也不得让下游 `TRANSFER` 再次覆盖该顶层阶段。

建议增加专用辅助函数完成缓存跳过事实：

```c
manager_status_skip_stage(
    OTA_STAGE_TRANSFER,
    "CACHE_REUSED",
    now_ms);
```

该函数应只修改现有 `ota_stage_record_t` 中的 `state`、`start_offset_ms`、`duration_ms` 和 `reason`，不伪造数据传输耗时。

### 6.2 Journal 版本

如果 `ota_status_snapshot_t` 直接持久化进 Gateway Journal，新增顶层字段会改变结构体布局。必须同步：

- 提升 Journal 版本。
- 校验保存长度和 CRC 范围。
- 旧版本 Journal 按不兼容记录丢弃，不能按新结构强行恢复。
- 验证断电恢复后 `package_source` 不会与真实缓存决策冲突。

如果缓存统计只保存在运行时状态且不进入 Journal，应在恢复任务时重新依据持久化事实生成，不能保留未确认的 `CACHE`。

### 6.3 数值和字符串

- 所有计数和耗时必须使用 JSON number，不能使用字符串。
- 枚举字符串统一使用大写 ASCII。
- 未知字段允许工具忽略，但已定义字段不得改变类型。
- `cache_hit_count` 不得大于 `cache_target_total`。
- `cache_query_elapsed_ms` 使用单调时钟计算，不能使用系统日期时间。

## 7. 联调验收清单

- [ ] 单 Sync 首次升级：`package_source=TRANSFER`，传输进度正常增长。
- [ ] 单 Sync 二次同包升级：`package_source=CACHE`，顶层传输显示“已跳过”。
- [ ] 多 Sync 全部命中：`cache_hit_count=cache_target_total`，不发送任何 512 字节 OTA 数据块。
- [ ] 多 Sync 任一 `MISS`：`package_source=TRANSFER`，所有 Sync 走完整传输。
- [ ] `BUSY/ERROR/TIMEOUT`：安全回退完整传输，并保留真实 `cache_result`。
- [ ] 缓存命中后，下游 `TRANSFER` 只显示在 Extender 子任务，不把顶层传输重新改成 `RUNNING`。
- [ ] `CACHE` 模式的顶层 `transferred_bytes` 不冒充下游传输字节数。
- [ ] 重复 `cmd=8` 查询返回同一会话的一致缓存决策。
- [ ] 终态报告中仍能看到“缓存复用 / 已跳过 / CACHE_REUSED”。
- [ ] 使用旧 Gateway 状态响应时，新工具保持原完整传输显示。
