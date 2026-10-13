# IPM 固件识别与导入校验

日期：2026-10-13；适用工具版本：v0.2.9。当前身份规则已改为下述统一位置；旧问题描述仅作为排查背景。

## 问题和原因

IPM 固件仍有两种信息格式。网关和 WIoTa 复用了 `eco` 魔数，但设备类型编号沿用各自 SDK，不能使用 EcoLink 工具的类型表。此前统一按 EcoLink 解释，导致 WIoTa 类型 2 显示为异步扩展器，网关类型 8 显示为 DTU。MCU 不使用 ECO 标识，旧导入流程又依赖事先选中 MCU 任务，因此在其他任务下导入 MCU 会报“未识别到有效 ECO 固件标识”。

## 当前识别规则

| 当前模式 / 固件 | 信息位置与类型 | 识别结果 | 升级文件 |
|---|---|---|---|
| IPM / gateway | `0x9F00` 的 `IPM + type=8 + version` | 网关 | 完整 BIN |
| IPM / wiota | `0x9F00` 的 `IPM + type=2 + version` | 倾角定位-WIoTa，同步端 | 正反向差分 Patch |
| IPM / mcu_app | `0x9F00` 的 `IPM + type=3 + version`，另校验原 MCU 安全头 | 倾角定位-MCU | 包含 UBoot 的完整 BIN |
| EcoLink / 类型 2 | ECO 标识 | 异步扩展器 | 原有差分流程 |
| EcoLink / 类型 8 | ECO 标识 | DTU | 原有 Node 流程 |

IPM 内嵌软件版本必须为 1～254。识别以文件内容及当前模式为准，文件名不作为类型或版本依据。MCU 还校验头格式、设备标识、硬件版本、地址、长度、两组向量和 CRC；不会因识别到魔数而跳过完整格式检查。

IPM WIoTa 源 BIN 和新差分 JSON 的设备类型统一为 2；`patch_type` 为 `ipm-wiota`，`mode=2` 明确绑定 IPM。本地 `.ipm.json` 另保存项目与文件哈希。EcoLink/传统模式仍使用各自原有类型表和命名，IPM 镜像不会回退按 ECO 标识解释。旧 IPM BIN 和旧差分清单需重新构建及制作。

完整字段、偏移、版本来源及显示规则见 [IPM 镜像内嵌版本识别规则](IPM镜像内嵌版本识别规则.md)。EcoLink 和传统模式的前缀与类型表保持原有逻辑。

## 操作

1. 先切换到 IPM 模式。
2. WIoTa：分别从 A、B 入口导入两个版本，确认显示“倾角定位-WIoTa（同步端）”、类型 2 和实际版本，再制作 Patch。工具生成正反向包并执行还原校验。
3. 网关或 MCU：可从“导入升级文件”导入完整 BIN，也可直接从 A、B 入口导入两个版本。导入时自动选择对应任务；两份兼容镜像就绪后，B 为正向、A 为反向。无需制作差分包。
4. 网关创建升级任务前仍需要刷新 Gateway，查询其当前版本；MCU 使用实际同步端目标 ID。

源文件保持原样，导入目录保存本地哈希及项目记录。MCU OTA 下载和校验完整文件，只安装 APP，设备 UBoot 仍只能通过烧录器更新。

`position_monitor` 普通归档位于 `bin/`，双版本归档位于 `bin/ota/<工程>/`；两种目录中的正常 BIN 均可使用上述入口。

## 已完成验证

- 核心回归：同一类型字节在 IPM 与 EcoLink 下分别正确解释；拒绝 IPM 非法类型、非法版本与跨模式配对，识别 MCU 信息头。
- 界面模型：从网关任务导入 MCU 自动切换目标；网关与 MCU 的 A/B 导入、原样复制、发布前检查和循环任务模板通过。
- 使用实际 `bin/ota/gateway/`、`mcu_app/`、`wiota/` 中的 v0.1、v0.2 共六份镜像验证；WIoTa 正反向 Patch 制作和还原校验通过。
- 保留原有 EcoLink 类型表及网关版本查询门禁；没有执行 MQTT 下发或实机 OTA。

```powershell
dotnet build OtaTool.sln -c Release --no-restore
dotnet run --project tests/OtaTool.Core.SmokeTests -c Release --no-build
$env:IPM_OTA_PAIR_DIR = 'D:\code\position_monitor\bin\ota'
dotnet run --project tests/OtaTool.App.SmokeTests -c Release --no-build
```

排查时应同时记录当前工具版本、所选模式、源 BIN 的实际标识字节和 APP 信息头。仅看文件名、UI 标签或复用的魔数，无法确定设备类型表是否使用正确。
