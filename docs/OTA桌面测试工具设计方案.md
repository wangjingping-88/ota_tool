# OTA Windows 桌面测试工具设计方案

## 1. 建设目标

开发独立、可扩展的 Windows OTA 测试工具，采用 `.NET 8 + WPF + MVVM`，发布为 `win-x64 self-contained`。

工具不绑定任何单一产品，运行时必须选择协议模式：

- **传统模式**：仅支持 Gateway 与 Sync 升级；可指定 ID 定向升级，或选择广播升级而不填写 ID；不包含 Gateway OTA 状态轮询协议，也不发送广播时间参数；不支持日志解析。
- **EcoLink 模式**：支持 Gateway、Sync、Async、Node 升级，并启用本方案第 8 章定义的 Gateway OTA 状态轮询协议。

两种模式复用同一套 MQTT/HTTP、Patch 管理、文件发布、任务编排、循环测试、报告与基础校验实现，但界面工作区和持久化数据按模式隔离；模式差异只能封装在协议适配器、任务参数模型和模式工作区中，不得复制整套界面或业务流程。

EcoLink Gateway 当前固件的 `ota.dev_type` 采用协议字符串而非桌面显示名称：Gateway 为
`gateway`、Sync 为 `iote`、Async 为 `ex_mcu`、Node 为 `node`。界面必须继续显示通用设备
名称，编解码器负责上述映射，避免将 `sync` 或 `async` 作为未被 Gateway 接受的请求值发送。

工具用于完成：

- 本地或公网 MQTT 环境连接。
- 本地 HTTP Range 服务。
- 公网 HTTP/SFTP 文件发布。
- OTA Patch 管理及后续差分包生成。
- Gateway、Sync、Async、Node 升级任务配置（其中 Async、Node 仅 EcoLink 模式提供）。
- 单次升级和双向循环升级。
- EcoLink 模式下工具主动轮询 Gateway OTA 状态。
- OTA 进度、阶段耗时和结果展示。
- EcoLink 模式四端日志导入分析。
- HTML、JSON 测试报告导出。

工具不包含串口日志抓取、固件烧录和固件编译。

## 2. 工程架构

新建目录：

```text
tools/ota_tool/
├── OtaTool.App
├── OtaTool.Core
├── OtaTool.Mqtt
├── OtaTool.Http
├── OtaTool.Diff
├── OtaTool.Analysis
└── OtaTool.Tests
```

模块职责：

- `App`：WPF 界面、MVVM、页面导航。
- `Core`：OTA 任务、状态机、设备参数、循环测试、报告、模式与协议适配器。
- `Mqtt`：内嵌 Broker、公网 Client、MQTT 传输与协议适配。
- `Http`：本地 Range 服务、SFTP 发布、公网文件验证。
- `Diff`：Patch 导入、差分引擎接口、哈希和容量门禁。
- `Analysis`：仅 EcoLink 模式调用日志分析 EXE 并展示结果。
- `Tests`：协议、状态机、网络和集成测试。

本地任务和报告索引使用 SQLite，普通设置使用 JSON，密码和私钥口令保存到 Windows Credential Manager。JSON 内以 `ModeWorkspaces.EcoLink`、`ModeWorkspaces.Traditional` 作为全局模式工作区表，并记录 `ActiveMode`；旧版顶层设置首次加载时自动迁移为两份模式工作区。Credential Manager 键同样带模式前缀，避免两种模式互相覆盖。

模式切换时分别保存并恢复当前页面、MQTT/HTTP/SFTP、Patch 目录与选择、升级参数、循环参数、Node 类型及发现勾选、日志目录、报告范围和升级状态机视图。历史报告按任务所属模式过滤；切换前如存在升级任务、MQTT 连接、本地 Broker、本地 HTTP 服务或 Patch 发布操作，必须先结束相关操作，防止把活动连接误套用到另一模式。

模式与协议扩展点：

```csharp
public enum OtaMode { Traditional, EcoLink }

public interface IOtaProtocolProfile
{
    OtaMode Mode { get; }
    IReadOnlySet<DeviceType> SupportedDeviceTypes { get; }
    bool SupportsGatewayStatusPolling { get; }
    bool SupportsBroadcastTime { get; }
    bool SupportsLogAnalysis { get; }
    Task<ProtocolStartResult> StartAsync(OtaTask task, CancellationToken cancellationToken);
    Task<ProtocolCompletionResult> WaitForCompletionAsync(
        OtaTask task, CancellationToken cancellationToken);
}
```

传统模式实现必须固定 `SupportsGatewayStatusPolling=false`、`SupportsBroadcastTime=false`、`SupportsLogAnalysis=false`；EcoLink 模式实现分别为 `true`。广播和定向升级应使用同一个任务模型，通过目标范围字段表达，不以“空 ID”作为隐式约定。

## 3. 主界面设计

页面划分：

1. 环境服务。
2. 差分包中心。
3. 升级任务。
4. 循环测试。
5. 日志分析（仅 EcoLink 模式显示）。
6. 历史报告。
7. 系统设置。

首页展示：

- MQTT 连接状态。
- HTTP 服务状态。
- 当前 Patch 状态。
- 当前模式、Gateway 在线状态。
- 当前 OTA 阶段（EcoLink 模式显示轮询阶段；传统模式显示最终结果上报状态）。
- 目标完成数量。
- 已用时间和预计剩余时间。
- 启动、取消、暂停轮询等操作。

## 4. MQTT 与 HTTP 功能

### 4.1 MQTT

支持：

- 本地内嵌 MQTT Broker。
- 可选账号密码。
- 公网 `mqtt/mqtts` 连接。
- TLS 证书验证。
- 自动重连。
- 消息收发查看和按任务过滤。
- 多 Gateway 连接状态展示。

EcoLink 模式 OTA 升级请求 Topic：

```text
ucchip/down/sgw/<gateway_id>/<task_seq>
```

EcoLink 模式生成全局递增的 32 位 `task_seq`，一次 OTA 任务期间不得改变或复用。传统模式的 Topic、请求/结果编解码和任务序号规则由传统协议适配器配置；它不发送 `cmd=8/9` 轮询消息，也不依赖 `task_seq`/SID 状态关联。

### 4.2 HTTP

内嵌 Kestrel 服务器必须支持：

- `HEAD`。
- 完整 `GET`。
- 单段 `Range GET`。
- `206 Partial Content`。
- 正确的 `Content-Range`。

公网发布支持：

- SFTP 密码认证。
- OpenSSH 私钥认证。
- 临时文件上传。
- 上传完成后原子重命名。
- HTTP HEAD 和 Range 验证。
- 完整下载后的长度、MD5 校验。

未通过 HTTP 验证不得启动 OTA。

## 5. 差分包中心

### 5.1 已有 Patch 导入

导入后自动计算：

- 文件长度。
- MD5。
- SHA256。
- 目标类型。
- 旧版本和新版本。
- 是否超过目标容量。

容量门禁：

```text
Node  最大 Patch：0xD000
Async 最大 Patch：0x2F000
```

### 5.2 差分包生成

操作流程：

1. 选择目标类型。
2. 导入旧版 BIN。
3. 导入新版 BIN。
4. 填写旧版本和新版本。
5. 调用已认证差分引擎。
6. 生成 Patch。
7. 执行本地 PatchTest。
8. 恢复结果与新版 BIN 进行长度和 SHA256 比较。
9. 生成 Package Manifest。
10. 发布到 HTTP 服务。

差分引擎接口：

```csharp
public interface IDiffEngine
{
    DiffEngineInfo GetInfo();

    Task<DiffResult> GenerateAsync(
        DiffRequest request,
        CancellationToken cancellationToken);

    Task<PatchVerifyResult> VerifyAsync(
        string oldImage,
        string patchFile,
        string expectedNewImage,
        CancellationToken cancellationToken);
}
```

Manifest 记录：

- 设备类型。
- 旧/新版本。
- 旧/新固件长度及 SHA256。
- Patch 长度、MD5、SHA256。
- 差分引擎名称、版本和文件 SHA256。
- PatchTest 结果。
- 生成时间。

### 5.3 当前差分引擎约束

当前实现使用 `bsdiff_cmd.exe` 生成分区 Patch，并使用无界面的
`partition_patch_verify.exe` 完成还原验证。原生验证器直接解析 16 字节分块头、自定义
BSDiff 控制流、LZzip 数据和 CRC16-USB，不再启动或分发 `OTA_TOOL.exe`、Qt 运行时及
UI Automation 脚本。

当前边界：

- `partition-bsdiff-lzzip` 命令行引擎负责生成 Patch，原生验证器承担正反向还原校验。
- 发布脚本和在线更新包必须校验 `partition_patch_verify.exe` 及第三方许可证声明。
- 仍允许导入外部 Patch，但必须通过原生还原、CRC、目标镜像和容量校验后才能进入发布和升级流程。
- 原生验证失败时不得写入 `restore_verified=true`，也不得发布或启动升级。

新引擎必须通过：

- 固定 A/B 黄金样本。
- 本地完整恢复。
- 新镜像 SHA256 一致。
- 正反向 Patch 测试。
- Node 真实 Bootloader 验证。
- Async 真实 Bootloader 验证。

### 5.4 已提取差分算法与集成方案

已审阅 `C:\Users\jpwang.UCCHIP\Downloads\bsdiff` 源码。该目录不是通用 BSDIFF40
格式的命令行工具，而是与当前固件 Bootloader 配套的“分区级 BSDiff + 定制 LZ +
分块头”实现；其输出格式由 `bspatch.cpp` 的解包逻辑定义。该引擎是 EcoLink 模式的
候选引擎；传统模式通过可插拔差分引擎或导入 Patch 支持对应设备协议，不得假定二者
Patch 格式可互换。

#### 5.4.1 算法链路

```text
旧 BIN / 新 BIN
  -> 反向定位 "UC\\0F" 分区表，读取分区数量和分区长度
  -> 按分区选择升级块（默认跳过首块；负长度分区只跨越、不生成块）
  -> 每个升级块调用 bsdiff：后缀数组匹配 -> 差分字节 + 原样附加字节
  -> 调用 LZzip（滑动窗口由 zip_flag/nRomIdx 决定）压缩差分流
  -> 填充 patch_hdr_t：块序号、长度、起始地址、新块 CRC16-USB、结束标志
  -> 顺序拼接所有块，得到 OTA Patch
```

底层差分流不是标准 BSDIFF40 的 control/diff/extra 三段文件，而是连续的自描述记录：

- 控制字节 `b7..b6` 表示旧镜像偏移长度减 1，`b5..b3` 表示 diff 长度字段字节数，
  `b2..b0` 表示 extra 长度字段字节数；所有长度和旧镜像偏移均按小端可变长度编码。
- diff 数据按 `new = old + diff_byte` 恢复；extra 数据直接写入新镜像。
- 差分流再经 `LZzip` 压缩。压缩数据在块头后依次为 `zip_flag`、首个字面量和 LZ
  指令流；解压实现以 `bspatch.cpp` 为准。
- 每个块均对恢复后的新块计算 `CRC16-USB`。块间顺序、起始地址和结束标志由
  `patch_hdr_t` 约束，而非由文件名或 Manifest 推断。

`patch_hdr_t` 源码使用 C/C++ 位域并以 `reinterpret_cast` 直接写入输出缓冲区。位域
内存布局受编译器和目标 ABI 影响；在未用黄金样本锁定字节布局前，C# 端不得用
`StructLayout` 自行猜测或重写该头部。首个实现必须在原生层保留同一结构定义，并增加
`static_assert(sizeof(patch_hdr_t) == 16)` 及字节级黄金样本测试；确认后再将其固化为
明确的序列化规范。

#### 5.4.2 原生引擎边界

在 `OtaTool.Diff` 下新增 `OtaTool.Diff.Native` 原生项目，采用 C++17
静态库或 DLL，并以窄 C ABI 暴露生成与验证函数。移植来源及职责如下：

| 源码 | 处理方式 | 职责 |
| --- | --- | --- |
| `bsdiff.cpp`、`bsdiff.h` | 纳入并保留版权声明 | 后缀数组和自定义差分流生成 |
| `lzzip.cpp`、`lzzip.h` | 纳入 | 差分流压缩 |
| `bsdiff_patch.cpp`、`bsdiff_patch.h` | 重构为无 CLI 的生成编排层 | 分区切块、块头、Patch 拼接 |
| `libcrc/src/crc16.c`、`checksum.h` | 纳入 | CRC16-USB |
| `bspatch.cpp` | 仅用于宿主验证适配器 | 与设备端一致地解压和恢复 |
| `file_handler.*`、`rom_api.*` | 不进入桌面运行时 | 设备端 Flash/EEPROM 抽象；验证时以内存文件适配替代 |
| `main.cpp` | 不集成 | 当前仅是 2 MiB 上限的单用途 CLI 入口 |

原代码采用 BSD 两条款许可证，移植文件、二进制发布包和第三方声明必须保留原始版权、
许可证文本及免责声明。任何本地修改均须记录来源版本、文件 SHA256 和修改说明。

建议 C ABI：

```c
typedef struct PartitionDiffOptions {
    int update_first_block;  // 0: 默认跳过首块；1: 首块不同才纳入
    int rom_index;           // 传递给 LZzip 的 zip_flag，需与 Bootloader 一致
} PartitionDiffOptions;

int partition_diff_generate(const char *old_bin, const char *new_bin,
                            const char *patch_out, const PartitionDiffOptions *options,
                            char *error_buf, size_t error_buf_len);

int partition_diff_verify(const char *old_bin, const char *patch,
                          const char *expected_new_bin,
                          char *error_buf, size_t error_buf_len);
```

`IDiffEngine.GenerateAsync` 调用 `partition_diff_generate`；`VerifyAsync` 必须调用基于
`bspatch.cpp` 状态机的宿主验证适配器，而不是使用通用 bspatch 或只校验 Patch 哈希。
原生库不得把文件大小、分区数、块长度、Patch 缓冲区容量视为可信输入，应在写入和移位
前逐项检查溢出、边界与分配结果。

#### 5.4.3 生成门禁与兼容性验收

“差分引擎未验证”在下列验收全部通过前仍然成立；通过后，首版可启用一键生成：

1. 使用已经实机升级成功的每个设备类型、每个方向各保留至少一组旧 BIN、新 BIN、
   官方 Patch 的黄金样本，并记录 Patch SHA256。
2. 原生引擎对黄金样本生成的 Patch 必须逐字节一致；若业务确认允许压缩策略变化，则
   至少要求块头、恢复结果和真实 Bootloader 验证均一致，并在 Manifest 标记引擎版本。
3. 宿主验证适配器恢复的镜像必须与新 BIN 完全一致，且长度、SHA256、每块 CRC16-USB
   均一致；同时执行反向版本 Patch。
4. 在 Node、Async 的真实 Bootloader 上分别验证正常升级、首块跳过/更新、空变更、
   分区边界、Patch 容量边界和断点/损坏 Patch 拒绝。
5. 覆盖异常输入：无分区表、分区表截断、负分区长度、旧/新镜像长度不一致、Patch 截断、
   伪造块长、超出 24 位字段上限及 CRC 错误。任一失败均不得发布或启动 OTA。

已识别的移植风险必须在代码实现前闭环：当前源码的分区表反向扫描从 `data[nDataSize]`
开始，存在一次越界读取风险；`patch_hdr_t` 位域布局未显式序列化；CLI 限制单个输入不超过
2 MiB；Patch 输出缓冲区以 `2 * block_size` 预分配且未证明对所有输入充分。移植版本应
修复前述边界问题、改用按需增长的受限缓冲区，并将最大镜像/分区/Patch 大小显式配置化。

#### 5.4.4 Manifest 增补字段

除 5.2 已列字段外，Manifest 还必须记录：

- `engine_id`：固定为 `partition-bsdiff-lzzip`；
- `engine_source_revision`、纳入源文件 SHA256、许可证标识；
- `zip_flag`/`rom_index`、`update_first_block`；
- 分区表偏移、每个分区的原始长度、是否跳过及生成块清单；
- 每个块的序号、起始地址、新块长度、CRC16-USB、Patch 文件偏移与长度；
- 黄金样本编号、宿主恢复校验结果、真实 Bootloader 验证记录。

## 6. 升级任务配置

### 6.1 模式与目标范围

所有任务均填写旧版本、新版本、Patch、超时时间及目标范围。目标范围使用显式枚举：
`SpecifiedIds` 或 `Broadcast`。

- `SpecifiedIds`：必须填写并校验至少一个目标 ID。
- `Broadcast`：不得填写目标 ID；适配器按所属协议编码广播请求。
- 广播时间仅可由协议能力明确支持时显示、校验和发送；传统模式固定不显示、不保存、
  不发送该字段。

### 6.2 传统模式

传统模式只提供 Gateway 和 Sync 两种升级类型。每种类型均可采用指定 ID 或广播范围，且不
执行 Gateway OTA 状态轮询。任务完成以 Gateway 调用
`manager_ota_upgrade_prompt(OTA_PROMPT_COMPLETE)` 后上报的最终升级结果消息为准；工具只等待并记录该终态消息，不发送 `cmd=8/9` 查询，也不进入人工确认流程。

Gateway 任务填写：

- 目标范围（定向时填写 Gateway ID）。
- 旧版本和新版本。
- Patch。
- 超时时间。

Sync 任务填写：

- 目标范围（定向时填写 Sync ID 或传统协议定义的目标列表）。
- 旧版本和新版本。
- Patch。
- 超时时间。

### 6.3 EcoLink 模式：Gateway 升级

填写：

- Gateway ID。
- 旧版本和新版本。
- Patch。
- 超时时间。

### 6.4 EcoLink 模式：Sync 升级

填写：

- Gateway ID。
- 1～16 个 Extender ID。
- 旧版本和新版本。
- Patch。
- 超时时间。

### 6.5 EcoLink 模式：Async 升级

填写：

- Gateway ID。
- 1～16 个 Extender ID。
- 旧版本和新版本。
- Patch。
- 超时时间。

### 6.6 EcoLink 模式：Node 升级

填写：

- Gateway ID。
- `node_type=2～63`。
- 1～16 个 Extender。
- 每个 Extender 对应的 Node 列表。
- 旧版本和新版本。
- Patch。
- 超时时间。

单任务最多 256 个 Node。不同 `node_type` 必须拆分为不同任务和不同 Patch。

## 7. 启动升级门禁

全部满足后，启动按钮才高亮：

- MQTT 连接成功。
- EcoLink 模式：OTA 状态响应 Topic 订阅成功，且已观察到目标 Gateway 在线。
- 传统模式：传统协议适配器完成启动前订阅/连通性校验；不得因缺少状态轮询 Topic 阻止启动。
- Patch 长度、MD5 及容量合法。
- HTTP HEAD 和 Range 验证通过。
- 公网文件与本地 Patch 长度、MD5 一致。
- 目标设备、版本和超时参数合法。
- 当前不存在活动任务。
- 循环测试的两个方向配置完整。

单次升级启动前提示：

```text
请确认目标当前运行版本为 V1，本次将升级至 V2。
```

循环测试只在首次启动时确认一次。

## 8. EcoLink 模式：Gateway OTA 状态轮询协议

本章仅适用于 EcoLink 模式。传统模式不得订阅、发送、模拟或以任何方式依赖 `cmd=8/9`
状态轮询协议；传统模式与 EcoLink 模式均接收 Gateway 调用
`manager_ota_upgrade_prompt(OTA_PROMPT_COMPLETE)` 后上报的最终升级结果消息，前者以该消息作为唯一终态依据。

### 8.1 设计原则

Gateway 不主动周期上报 OTA 进度。

正式服务器原有行为保持不变：

- `cmd=5`：启动 OTA。
- `cmd=6`：原有最终升级结果。
- 不增加周期性主动进度消息。

桌面工具通过查询命令按需获取 Gateway 当前缓存的 OTA 事实状态。

工具查询不得：

- 触发 Gateway 向 Sync 额外发送状态查询。
- 改变 OTA 定时器。
- 改变任务超时。
- 影响 Gateway→Sync 数据传输。
- 写入 Journal。
- 改变 OTA 状态。

Gateway 只读取当前内存状态或最新有效 Journal 快照并返回。

### 8.2 命令定义

```c
#define PROT_OTA_STATUS_QUERY     8
#define PROT_OTA_STATUS_RESPONSE  9
```

查询 Topic：

```text
ucchip/down/sgw/<gateway_id>/<query_seq>
```

查询示例：

```json
{
  "cmd": 8,
  "ver": "v2.0",
  "src": 0,
  "dst": 0,
  "ota_status": {
    "task_seq": 1001,
    "session_id": 0
  }
}
```

字段说明：

- `query_seq`：本次状态查询序号，来自 Topic 末尾。
- `task_seq`：原始 `cmd=5` 升级请求的 Topic 序号。
- `session_id`：首次查询填 0；获得 Gateway 生成的 SID 后填写实际值。

响应发布到 Gateway 原有上行 Topic：

```text
ucchip/up/sgw/<gateway_id>/<publish_seq>
```

响应示例：

```json
{
  "cmd": 9,
  "ver": "v2.0",
  "src": 704027,
  "dst": 0,
  "ota_status": {
    "query_seq": 2001,
    "task_seq": 1001,
    "session_id": 1481685217,
    "result": "OK",
    "status": "RUNNING",
    "stage": "TRANSFER",
    "reason": 0,
    "elapsed_s": 120,
    "stage_elapsed_s": 48,
    "file_size": 16631,
    "transferred_bytes": 8320,
    "subtask_total": 1,
    "subtask_completed": 0,
    "target_total": 5,
    "target_prepared": 5,
    "target_ready": 0,
    "target_verified": 0,
    "target_success": 0,
    "target_failed": 0,
    "missing_blocks": 12,
    "repair_round": 1
  }
}
```

### 8.3 查询结果

`result` 支持：

- `OK`：找到匹配任务。
- `NO_ACTIVE_TASK`：Gateway 没有活动 OTA 任务。
- `NOT_FOUND`：没有找到指定 `task_seq` 或 SID。
- `STALE_SESSION`：`task_seq` 存在，但 SID 不匹配。
- `INVALID_REQUEST`：字段或版本非法。
- `UNSUPPORTED_VERSION`：Gateway 不支持该查询协议。

任务状态：

- `RUNNING`。
- `SUCCESS`。
- `FAILED`。
- `CANCELLING`。
- `CANCELLED`。

任务阶段：

- `ACCEPTED`。
- `HTTP_DOWNLOAD`。
- `PACKAGE_DISTRIBUTE`。
- `PREPARE`。
- `TRANSFER`。
- `REPAIR`。
- `VERIFY`。
- `COMMIT`。
- `BOOT_VERIFY`。
- `FINISHED`。

### 8.4 终态查询

OTA 结束后，Gateway 可能已经释放运行上下文，因此查询逻辑必须支持读取最新有效 Journal。

要求：

- 最新终态至少保留到下一个 OTA 任务覆盖 Journal。
- 精确匹配 `task_seq` 和 SID 时返回终态。
- `DONE` 返回 `SUCCESS`。
- `FAILED/ABORTED/REPLACED` 返回对应失败或取消状态。
- 终态查询不得重新激活任务。
- Journal 损坏时返回 `NOT_FOUND`，不得根据不完整数据猜测成功。

### 8.5 工具轮询策略

默认策略：

```text
发送 cmd=5 后等待 2 秒
首次查询：2 秒
正常运行：每 5 秒查询一次
BOOT_VERIFY：每 10 秒查询一次
查询响应超时：3 秒
连续超时退避：5 秒、10 秒、20 秒、30 秒
最大查询间隔：30 秒
```

约束：

- 同一 Gateway 同一时刻只允许一个未完成状态查询。
- 查询使用 QoS 1，不设置 Retain。
- 重复查询必须幂等。
- 查询超时只表示 Gateway 状态未知，不直接判定 OTA 失败。
- 达到任务硬超时后，工具才进入超时处理。
- 收到终态后立即停止轮询。
- Gateway 调用 `manager_ota_upgrade_prompt(OTA_PROMPT_COMPLETE)` 后上报的最终升级结果消息可作为快速终态提示，但工具仍用一次 `cmd=8` 查询确认最终事实。

### 8.6 进度与剩余时间

Gateway 只返回事实数据，不负责计算剩余时间。

工具根据以下信息计算：

- 当前阶段。
- 文件长度和已传输长度。
- 历史同设备类型阶段耗时。
- 当前传输速率。
- 目标 Node 数量。
- 修复轮数。
- BOOT_VERIFY 历史耗时。

没有足够历史样本时使用预设阶段区间，并显示“估算”。

## 9. 循环升级

一轮定义为：

```text
V1 → V2 → V1
```

规则：

- 每个方向使用独立 Patch 和 Package Manifest。
- EcoLink 模式当前方向查询到 `SUCCESS` 后才能启动下一方向；传统模式仅在收到 Gateway 通过 `manager_ota_upgrade_prompt(OTA_PROMPT_COMPLETE)` 上报的明确成功结果后才能启动下一方向。
- `STORAGE_VERIFIED` 不能作为完整升级成功。
- 失败、取消、超时或状态无法关联时停止循环。
- 工具异常退出后只恢复任务展示，不自动重发升级请求。
- CANCEL、COMMIT 与回滚能力由当前协议适配器声明；界面不得将 EcoLink 语义套用到传统模式。
- 自动循环不重复弹出版本确认。
- 相邻两次单次升级之间支持固定秒数或自定义最小/最大秒数的随机间隔；间隔为 0 时连续执行。
- 间隔只发生在相邻任务之间，首个任务前和最后一个任务后不等待；等待期间允许取消整个循环。

## 10. 报告与日志分析

报告记录：

- 运行模式、协议适配器 ID 和版本。
- Gateway、Extender、Node 目标集合（传统模式记录 Gateway/Sync 的定向 ID 或广播范围）。
- 设备类型和 `node_type`（仅适用时）。
- 版本对。
- Patch 长度、MD5、SHA256。
- 差分引擎信息。
- MQTT 升级命令。
- EcoLink 模式下的每次状态查询及响应。
- 阶段时间线，或传统模式的最终结果上报记录。
- EcoLink 模式的查询超时和重试次数；传统模式记录最终结果消息的等待耗时。
- 目标完成情况。
- 循环测试成功率和耗时。
- EcoLink 模式的日志分析结论；传统模式固定记录“日志解析不支持”。

导出格式：

- HTML 可视化报告。
- JSON 机器可读报告。
- 可选 ZIP 证据包。

日志分析（仅 EcoLink 模式）：

- 将 `analyze_ota_logs.py` 封装为独立 EXE。
- 桌面工具不依赖 Python 环境。
- 首版支持当前 Node 四端分析。
- 后续增加 Async、Sync 和 Gateway 专项分析规则。
- 日志由用户手动导入，不负责串口抓取。
- 选择目录后生成本次 `.log` 文件清单；用户可从清单删除文件，但不删除磁盘源文件。
- 分析时先创建清单快照，分析器不得读取同目录中未列入清单的日志。
- 当前分析器按最新 OTA `session_id` 输出单轮结论；循环任务包含多个 SID 时不会自动汇总所有轮次，需按轮分别分析。

## 11. 测试与验收

### 11.1 传统模式

- Gateway、Sync 定向升级。
- Gateway、Sync 广播升级；广播任务不填写、保存或发送目标 ID。
- 广播任务不显示、不保存、不发送广播时间。
- 不订阅、不发送 `cmd=8/9`，并且缺少状态轮询 Topic 不得阻止任务启动。
- Gateway 调用 `manager_ota_upgrade_prompt(OTA_PROMPT_COMPLETE)` 后上报的最终升级结果消息的成功、失败、超时处理。
- 未收到最终结果消息时任务保持超时，不得进入人工确认或继续循环测试。
- 日志分析页面、日志导入和分析 EXE 均不可用。

### 11.2 EcoLink 模式：MQTT 状态查询

- 活动任务查询。
- 首次 SID 发现。
- SID 精确查询。
- 重复查询幂等。
- 无活动任务。
- 错误 `task_seq`。
- 错误 SID。
- Gateway 重启后从 Journal 查询。
- 终态上下文释放后查询。
- 查询期间 Gateway 不额外触发下游空口 QUERY。
- 查询不改变 OTA 超时和状态。
- 正式服务器未发送查询时，Gateway 不产生进度消息。

### 11.3 EcoLink 模式：OTA 流程

- Gateway、Sync、Async、Node 单次升级。
- 单 Node 和 5 Node 4+1。
- 1 个和 16 个 Extender 配置边界。
- Node 最多 256 个目标。
- 双向循环升级。
- Gateway 重启和 MQTT 重连。
- 查询响应丢失、重复和乱序。
- CANCEL 及 COMMIT 后失败处理。

### 11.4 共用 Patch 和 HTTP

- Node `0xD000/0xD001` 边界。
- Async `0x2F000/0x2F001` 边界。
- HTTP HEAD 和 206。
- 公网文件长度及 MD5 不一致。
- 未认证差分引擎禁止生成。
- PatchTest 失败禁止发布。
- 正反向 Patch 绑定错误时禁止启动。

## 12. 默认约束

- 每个任务必须固化运行模式、协议适配器 ID/版本和其能力快照；任务开始后不得切换模式。
- 传统模式只允许 Gateway、Sync；支持指定 ID 或无 ID 广播；不发送广播时间，不使用 Gateway OTA 状态轮询协议，也不支持日志解析。
- 两种模式均以 Gateway 调用 `manager_ota_upgrade_prompt(OTA_PROMPT_COMPLETE)` 后上报的最终升级结果消息作为终态上报来源；传统模式以该消息为唯一完成依据，EcoLink 模式以该消息提示终态后继续用 `cmd=8` 确认。
- EcoLink 模式支持 Gateway、Sync、Async、Node；Gateway 不主动周期上报进度，工具使用 `cmd=8` 查询、Gateway 使用 `cmd=9` 响应，并保留 `cmd=6` 最终结果协议。
- EcoLink 查询只读取 Gateway 已有事实，不触发下游查询；传统模式不实现该查询。
- 支持导入 Patch 和通过已认证的 `partition-bsdiff-lzzip` 引擎一键生成，二者都必须通过内置还原校验。
- 不再分发或调用 OTA_TOOL、Qt 和 UI Automation 脚本；还原校验由原生验证器完成。
- 不负责构建、烧录和串口日志采集。
- 同一工具实例同一时刻只执行一个父 OTA 任务。

## 13. 当前实现落地与安全边界

当前实现位于 `tools/ota_tool/`，为减少桌面端部署复杂度，首版将 MQTT、HTTP、SFTP、
差分接口、日志分析、设置和报告实现收敛在 `OtaTool.Core`，并由 `OtaTool.App` 提供 WPF
界面；`OtaTool.Core.SmokeTests` 提供无需外部设备的核心冒烟测试。

已实现并已纳入本地验证的能力：

- MQTT 3.1.1 客户端、TLS、账号密码、自动重连与内嵌 Broker；
- 本地 HTTP Range 服务及 HEAD、Range、完整 MD5 启动门禁；
- 传统模式的 `cmd=6` 终态处理，以及 EcoLink 的 `cmd=5/6/8/9`、轮询响应超时退避；
- Gateway / Sync / Async / Node 的任务建模；Node 使用 `node_type` 和
  `ExtenderID -> NodeID[]` 显式映射，单任务限制 16 个 Extender、256 个 Node；
- SFTP 密码/私钥发布、Host Key SHA256 固定校验、临时文件原子重命名及公网完整 MD5 校验；
- 正反向 Patch 双向循环、SQLite 报告索引、HTML/JSON 导出和 EcoLink 外部日志分析入口；
- JSON 普通设置与 Windows Credential Manager 密码/口令分离保存。

`partition-bsdiff-lzzip` 已集成 Patch 生成和无界面原生还原验证。制作正向、反向 Patch
以及验证外部 Patch 时，均必须完成分块边界、LZzip/BSDiff 还原、CRC16-USB、容量和目标
镜像一致性校验；任一检查失败时不得生成已验证 Manifest、发布或启动升级。旧
`OTA_TOOL.exe`、Qt 运行时和 UI Automation 脚本不再属于桌面端或在线更新包依赖。
