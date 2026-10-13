# 固件镜像版本识别规则

更新日期：2026-10-13。适用 position_monitor 新构建镜像与 ota_tool v0.2.9。

## 1. IPM 三个工程统一规则

三种完整 BIN 在相同文件偏移 0x9F00（40704）保存 5 字节身份：

```text
[ I ][ P ][ M ][ type ][ version ]
```

IPM 为大写 ASCII，type、version 均为 1 字节无符号整数，不是文本字符。

| 文件偏移 | 长度 | 内容 |
|---|---|---|
| 0x9F00～0x9F02 | 3 | 49 50 4D，即 IPM |
| 0x9F03 | 1 | 固件设备类型 |
| 0x9F04 | 1 | 软件版本整数 N，1～254 |

| 工程 | type | 版本 1 示例 | 版本 2 示例 | 升级文件 |
|---|---|---|---|---|
| gateway | 8 | 49 50 4D 08 01 | 49 50 4D 08 02 | 完整 BIN |
| wiota | 2 | 49 50 4D 02 01 | 49 50 4D 02 02 | 正反向 Patch |
| mcu_app | 3 | 49 50 4D 03 01 | 49 50 4D 03 02 | 含 UBoot 的完整 BIN |

类型编号表示镜像所属设备类别，与软件版本、硬件版本、网络设备 ID、A/B 测试角色均独立。UI 的“类型 2”表示 IPM 的 WIoTa 同步端。

工具在 IPM 模式只读取这一固定位置的 IPM 标识，不搜索文件中的任意 IPM 字符串，也不回退使用旧 ECO 字段判断项目类型。

## 2. 固化和预留方式

网关/WIoTa 的启动 ROM 区为文件 0x7000～0x9FFF，SDK 打包器会回填该区尾部元数据。实际探针验证确认靠近 ROM 末尾的身份会被清除或覆盖，因此统一位置选择 0x9F00，避开回填区。

网关和 WIoTa 通过各自的 ipm_identity.S 产生 5 字节数据，链接脚本将它保留在固定地址。段使用与启动代码相同的 ELF 装载属性，使 SDK 正确保留位置；数据本身不作为函数执行。代码增长与身份区重叠时由链接器拒绝构建，最终 BIN 再校验位置、类型和版本。

MCU 完整 BIN 前 32 KiB 为 UBoot。统一身份放在 APP 内部：文件偏移 0x9F00，对应 MCU 地址 0x08009F00，因此 OTA 安装 APP 时也会更新身份，不会出现版本留在 UBoot 区而无法更新的问题。

## 3. 软件版本来源

源码路径相对于 D:\code\position_monitor。

| 工程 | 版本配置文件 | 宏 |
|---|---|---|
| gateway | gateway/gateway/gateway_config/eco_gateway_version.h | GATEWAY_OTA_SOFTWARE_VERSION |
| wiota | wiota/applications/common/software_version.h | SOFTWARE_VERSION |
| mcu_app | mcu_app/applications/software_version.h | SOFTWARE_VERSION |

有效版本是整数 1～254。网关的 GATEWAY_DISPLAY_SOFTWARE_VERSION 是产品展示字符串，不作为镜像内 OTA 版本。

- 普通构建输出当前 N，归档到 bin/。
- -ota 构建 N、N+1 两个业务版本，结束后恢复原版本；当前 N 必须为 1～253。归档到 bin/ota/<工程>/。
- A/B 只表示两个测试版本，不写入身份区，不加到文件名。
- 文件名日期用于归档。改名或修改外部 JSON 不会改变内嵌版本和实际运行版本。

## 4. MCU 保留扩展安全信息头

通用身份用于统一识别。MCU 原有 64 字节 APP 安全信息头继续保留，以维持现有 UBoot 接口和 CRC、型号、硬件兼容性校验；它不是另一套工具入口。

| BIN 区间 / 位置 | MCU 地址 | 用途 |
|---|---|---|
| [0,0x8000) | 0x08000000 起 | 32 KiB UBoot 与 FF 填充 |
| [0x8000,0x8040) | 0x08008000 起 | 64 字节 APP 安全信息头 |
| [0x8040,0x8200) | 0x08008040 起 | 保留区，全部 FF |
| 0x8200 | 0x08008200 | APP 向量表 |
| 0x9F00 | 0x08009F00 | 通用 IPM 身份 |
| 0xA000 起 | 0x0800A000 起 | APP 正文代码 |

安全信息头的多字节整数为小端：

| 完整 BIN 偏移 | 长度 / 字节 | 字段 | 当前值或规则 |
|---|---|---|---|
| 0x8000 | 8 | magic | IPMAPP01，无结束零 |
| 0x8008 | 2 | format_version | 1 |
| 0x800A | 2 | header_size | 64 |
| 0x800C | 4 | software_version | N，必须与 0x9F04 的版本一致 |
| 0x8010 | 16 | device_id | IPM_GD32L233，补零 |
| 0x8020 | 4 | load_address | 0x08008000 |
| 0x8024 | 4 | vector_offset | 0x200 |
| 0x8028 | 4 | image_size | APP 段长度，完整 BIN 长度减 0x8000 |
| 0x802C | 4 | image_crc32 | APP 数据区 CRC32 |
| 0x8030 | 2 | hardware_revision | 1，对应 IPM V0.1 |
| 0x8032 | 2 | min_boot_api | 当前要求 2 |
| 0x8034 | 4 | flags | 0 |
| 0x8038 | 4 | reserved | 0 |
| 0x803C | 4 | info_crc32 | 头前 60 字节 CRC32 |

通用身份中的 MCU 类型必须为 3，型号与硬件版本必须匹配。完整 BIN 为 40960～262144 字节，APP 段为 8192～229376 字节。两组向量继续检查 SRAM 栈范围、8 字节对齐、Thumb 位和入口范围。

CRC32 为 IEEE/ZIP，反射多项式 0xEDB88320，初值与最终异或均为 0xFFFFFFFF。

- 头 CRC 覆盖 [0x8000,0x803C)。
- APP 数据 CRC 覆盖 [0x8040,文件末尾)，包含通用身份。
- OTA 整包 CRC32 / MD5 / SHA256 覆盖全部字节，包括包内 UBoot。

APP 版本与设备型号由 mcu_app 写入，UBoot 只读取和校验。UBoot 日志版本、信息格式版本、最低引导 API 和 APP 软件版本是独立字段。此次保留原安全头 ABI；OTA 仍只安装 APP，不更新设备 UBoot。

## 5. EcoLink、传统模式保持原规则

核对当前 EcoLink 实际格式为 [version][eco][type]，不是 [ECO][type][version]。其原有偏移与大小写保持不变：

| 偏移 | 内容 |
|---|---|
| 0x6FFB | 版本字节 |
| 0x6FFC～0x6FFE | 小写 eco：65 63 6F |
| 0x6FFF | EcoLink 类型字节 |

EcoLink 类型 2 仍是异步扩展器、类型 3 仍是同步扩展器、类型 8 仍是 DTU。传统模式保留已有功能和命名。新 IPM 镜像在其他模式会明确拒绝导入。

网关/WIoTa 仍保留上述旧 SDK 内部字段，供其原有引导器使用；它不再作为 IPM 工具识别入口，也不表示该 BIN 可在 EcoLink 模式使用。

## 6. IPM Patch 与清单

新 IPM WIoTa Patch 使用 ipm-wiota 前缀，例如：

```text
ipm-wiota-v1-to-v2-jpwang.patch
ipm-wiota-v2-to-v1-jpwang.patch
```

新差分 JSON 的 patch_type 为 ipm-wiota、mode 为 2（IPM）、device_type 为 2，类型与源 BIN 一致。JSON 的 mode=2 与设备 type=2 是不同字段。

EcoLink/传统模式仍保留原有 ext-s 等前缀及自身清单类型编号。新清单只能在所属模式导入和使用。此次更换 IPM 镜像规则后，旧 IPM BIN 和旧 IPM 差分包需重新构建、导入和制作；不要仅修改名称或手工改清单类型。

## 7. 版本显示说明

保存的是整数 N。当前 position_monitor 归档和 MCU 日志使用 v0.N；ota_tool 共享显示函数使用 v(N/10).(N%10)。

| N | 构建文件名 / MCU 日志 | 当前工具显示 |
|---|---|---|
| 1 | v0.1 | v0.1 |
| 2 | v0.2 | v0.2 |
| 10 | v0.10 | v1.0 |
| 12 | v0.12 | v1.2 |

N≥10 的显示命名差异尚未修改；实际比较仍使用整数。本次不修改 EcoLink 或传统模式的共享显示逻辑。

## 8. 验证依据

三工程实际 v0.1/v0.2 六份镜像均在 0x9F00 校验 IPM、类型和软件版本。MCU 另外检查 BIN/HEX/ELF 一致性、安全头、通用身份版本一致及 CRC。工具验证三模式正反向差分、还原、目录恢复和跨模式拒绝；MCU 正式 UART 传输测试使用新完整业务镜像。

关键实现：position_monitor/tools/ipm_identity.py、三个工程链接脚本，以及 ota_tool/src/OtaTool.Core/Models/IpmImageIdentity.cs。
