---
title: IPM MCU 清单超长导致升级未启动（2026-09-28）
date: 2026-09-28
tags:
  - OTA
  - IPM
  - 联调
  - 踩坑
---

# IPM MCU 清单超长导致升级未启动

## 结论

本次直接阻塞点在网关单播发送前的参数检查：MCU Manifest 封装后为 738 字节，超过 `UC_DL_MAX_LEN=310`。日志中六次 `uc_wiota_send_data_order ... input param error` 均紧随 `WIOTA_APP_MSG len 738`，足以解释清单没有到达 WIoTa、MCU 没有进入接收流程。

网关还存在发送状态判断问题：把消息放入 WIoTa 线程队列成功当成清单发送成功。三次提交后仍打印 `manifest 1/1 sent`，进入文件广播；该进度不能代表 MCU 接收进度。

本次只进行了日志分析、源码核对和结构尺寸编译验证，未修改或烧录固件。

## 证据范围

原始日志目录：`D:\tools\SerialLog\data\logs\ipm\2026-09-28\20260928_144228_949`。

日志仍在追加，分析使用独立快照：`D:\code\ota_tool\artifacts\ipm-log-analysis-20260928-144228`。网关快照截至 14:45:03.822、WIoTa 截至 14:45:02.570、MCU 截至 14:45:01.894；不据此断言后续最终状态。

代码依据为分析时 `D:\code\position_monitor` 的工作区，含已有未提交改动；未读取设备固件校验和，因此运行固件与工作区的完全一致性尚未验证。不过，日志中的报文长度和报错行与源码直接吻合。

## 时间线

| 时间 | 网关证据 | 含义 |
| --- | --- | --- |
| 14:42:43.951 | `sid 3353126150 seq 57115` | MCU OTA 会话创建 |
| 14:42:53.536 | `http download success` | 网关已取得文件 |
| 14:42:53.55 左右 | 文件 162676 字节，版本 1→2，318 块 | 任务版本参数已经到达网关 |
| 14:42:54.506 | 目标逻辑 ID `0xcabd`，空口 ID `0x80000039` | 目标空口 ID 非零 |
| 14:42:54.507～14:43:01.587 | 六次 `WIOTA_APP_MSG len 738` 后参数错误 | 清单始终在本地发送接口被拒绝 |
| 14:43:00.562 | `down ota manifest 1/1 sent` | 队列提交被误认为发送成功 |
| 14:43:01.587 | `packet_type 2 to 0`，`WIOTA_APP_SEND_OTA len 662` | 未确保清单到达便开始固件广播 |
| 14:45:03.822 | `iote ota_upgrade 6%` | 网关本地广播计数，不能证明 MCU 已接收 |

对应网关快照行：116、138～169、398。三次上层提交，每次一次额外重试，与六次错误相符。

## 738 字节从哪里来

`mcu_ota_gateway_manifest_t` 直接内嵌完整 `downstream_ota_gateway_manifest_t`。通用结构保留 `node_ids[256]`，每个元素为 2 字节。MCU 分支虽然要求 `node_count=0`，仍用 `sizeof(*mcu_manifest)` 作为载荷长度，将整个空数组发送。

| 组成 | 字节数 |
| --- | ---: |
| 通用清单固定字段（不含 Node 数组） | 72 |
| MCU 不使用的 `node_ids[256]` | 512 |
| MCU 包 CRC32 | 4 |
| MCU 清单小计 | 588 |
| 本次外层 OTA 前缀及应用头 | 150 |
| 实际单播报文 | 738 |

外层 OTA 前缀源码断言为 148 字节；本次完整封装额外应用头为 2 字节。738−588=150，与文件块报文 662−512=150 相互印证。

已使用现有 `D:\Program Files\LLVM\bin\clang.exe`，直接包含仓库头文件编译尺寸探针，确认通用清单 584 字节、MCU 清单 588 字节、Node 数组 512 字节。探针与 LLVM 输出保存在日志快照目录，未安装依赖。

若定义 MCU 专用紧凑清单去掉该数组，清单为 76 字节，同样外层封装下约 226 字节，低于 310。实际修复仍应对最终编码长度做边界检查，不应只检查内层结构能否放进 640 字节 OTA 缓冲区。

## 为什么 WIoTa 有 UPG_REQ，而 MCU 没有升级日志

WIoTa 的 `gw cmd UPG_REQ` 在命令分派时打印，`gw bin s ...` 在路由到 MCU 分支之前打印，不能视为 MCU 已接受升级。

`mcu_ota_bridge.c` 需要先收到并校验 Manifest 才建立会话，并通过 UART 命令 `0x71` 向 MCU 转发。处理文件块还要求会话有效，且 session、文件长度、版本等匹配。清单在网关就被拒绝，后续文件广播无法为本次会话建立 MCU 接收流程。

MCU 快照只有传感器和运行信息，没有 `mcu ota: receiving` 或 `READY`。代码中存在这些日志，并非整个 MCU OTA 分支完全没有输出。当前快照未包含启动阶段，也不能凭此认定烧录版本或下载测试宏是否正确。

## 修正位置及回归要求

1. 网关和 WIoTa 同步定义 MCU 专用紧凑清单，并同步修改接收长度检查、CRC32 位置及协议兼容策略。不能仅从发送尾部截掉 512 字节，因为 CRC32 目前位于整个通用结构之后；接收端也严格检查完整结构长度。
2. 对最终单播报文检查 310 字节限制；遇到永久参数错误立即报告明确原因，不能靠延时重试解决。不要直接提高 SDK 的长度宏。
3. 清单推进应接入真实发送结果，失败时有限重试并结束任务；如需证明远端已建立会话，应再等待远端确认。不能把入队成功等价为空口成功。
4. 回归应覆盖编码后长度、发送失败不推进到广播、清单接受及 UART 转发，以及 MCU `receiving`→`READY` 的完整流程。还需保留文件尺寸、哈希、会话和版本一致性检查。
5. 分析时的旧无 UBoot 固件仅验证下载；2026-10-12 已改为正式 UBoot 升级，READY 后等待 COMMIT、重启、搬运及新 APP 确认。当前规则见 [IPM 模式升级说明](IPM模式升级说明.md)，不能再按旧下载联调日志判定完成。

## 源码定位（分析时行号）

- `gateway/gateway/gateway_manager/ota_manager/mcu_ota_wire.h:15`：完整 base 加 CRC32；35 行只检查内层小于 640，未约束单播报文。
- `gateway/gateway/gateway_manager/ota_manager/downstream_ota_protocol.h:253`：通用清单；271 行为 Node 数组。
- `gateway/gateway/gateway_manager/ota_manager/ota_manager.c:3086`：MCU 使用完整结构；约 3104 行按 sizeof 设长度。
- `gateway/gateway/codec/coding/uc_coding.h:185`：OTA 前缀 148 字节静态断言。
- `gateway/gateway/wiota_app/wiota_api/uc_wiota_api.h:9`：单播上限 310；7 行广播上限 1024，二者不同。
- `gateway/gateway/wiota_app/wiota_api/uc_wiota_spi_com.c:3559`：长度超限立即回调失败并返回。
- `gateway/gateway/wiota_app/wiota_app.c:902`：单播失败重试。
- `gateway/gateway/gateway_manager/interaction_manager/interaction_manager.c:142`：发送函数返回值只说明入队结果。
- `gateway/gateway/gateway_manager/ota_manager/ota_manager.c:3727`：入队成功后增加清单发送计数；3741 行打印 sent。
- `wiota/applications/downstream_ota/mcu_ota_bridge.c:393`：接受清单条件；440 行 UART 转发；444 行起要求有效会话才接收块。
- `wiota/PS/app/src/uc_wiota_gateway_api.c:1253`：打印分区信息；1854 行打印 UPG_REQ。
- `mcu_app/applications/ota/ota.c:713`：MCU 开始接收日志；490 行 READY 日志。

## 可复用调试经验

先区分「入队」「空口发送」「远端协议接受」「UART 转发」「Flash 写入」「整包校验」六个层次。对参数错误先比较实际编码长度和接口上限，并区分单播与广播通道；重试次数和百分比都不能替代接收确认。复用通用结构时，尤其检查固定容量数组是否因 sizeof 被无条件上行或下发。
