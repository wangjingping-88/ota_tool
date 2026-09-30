# EcoLink 设备类型 v2.0 与 Patch 门限

按最新 `app_dev_type_e` 定义，固件身份标识位于完整镜像 `0x6FFF` 附近：版本字节、`eco` 三字节标记、设备类型字节。导入完整镜像及生成差分清单时按下表解析。旧类型编号不能复用到 v2.0；旧清单 `schema_version=1` 和无具体类型的 `node` 前缀须重新生成，以免把旧设备误认作新类型。

| 类型值 | 设备 | Patch 前缀 | OTA 路由 |
|---:|---|---|---|
| 0 | 云平台服务器 | `server` | 不支持 OTA |
| 1 | 网关 | `gateway` | Gateway |
| 2 | 异步扩展器 | `ext-a` | Async |
| 3 | 同步扩展器 | `ext-s` | Sync |
| 4 | 室内灯控 | `room-light` | Node |
| 5 | 室内开关 | `switch` | Node |
| 6 | 电源插座 | `socket` | Node |
| 7 | SLC 路灯控制器 | `street-light` | Node |
| 8 | DTU | `dtu` | Node |
| 9 | 低功耗 DTU | `low-power-dtu` | Node |

新生成差分清单使用 `schema_version=2`。Node 默认门限为 124 KiB；类型 7 的路灯门限为 112 KiB；Async、Sync、Gateway 分别维持原有门限，其中 Sync 为 52 KiB、Gateway 为 2 MiB。已保存的旧默认 Node 52 KiB 在加载工作区时迁移至 124 KiB；旧内置 Node 类型选择随之迁移。

系统设置按模式展示门限：EcoLink 显示 Node、Async、Sync、Gateway；IPM 只显示 Sync、Gateway；传统模式隐藏这些现代协议门限项。历史设计文档及发布说明中的旧编号、旧门限是当时版本的记录，不代表 v2.0 当前值。
