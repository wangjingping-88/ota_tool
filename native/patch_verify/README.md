# 无界面 Patch 还原验证器

该程序直接解析 UCCHIP 自定义分区 Patch：16 字节分块头、BSDiff 控制流、LZzip 压缩流及 CRC-16/USB。它不启动窗口，也不依赖 Qt。

当前桌面工具采用原生还原验证门禁：

1. `partition_patch_verify.exe` 完成原生还原、分块边界、CRC 和目标镜像比对；
2. 验证通过后，Manifest 才写入 `restore_verified=true`；
3. 原生验证失败、Patch 超容量或元数据不一致时均禁止发布和升级。

旧 `OTA_TOOL.exe`、UI Automation 脚本及 Qt 运行时已退出发布包。
