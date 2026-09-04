# OTA 测试平台

用于 EcoLink OTA 差分包制作、发布、升级任务执行、状态机展示、历史报告与日志分析的 Windows 桌面工具。

## 仓库结构

- `src/OtaTool.App`：WPF 桌面应用。
- `src/OtaTool.Core`：协议、差分、发布、任务、报告等核心能力。
- `src/OtaTool.Update`：GitHub Release 检查、下载校验、安全解压与更新任务。
- `src/OtaTool.Updater`：独立目录切换、启动确认与失败回滚程序。
- `tests/OtaTool.Core.SmokeTests`：核心冒烟测试。
- `tests/OtaTool.Update.Tests`：在线升级安全与回滚测试。
- `scripts`：原生验证器构建、发布与 OTA 日志分析脚本。
- `assets/native`：随应用发布的原生差分制作和无界面 Patch 还原验证程序，不再依赖 `OTA_TOOL` 或 Qt。
- `docs`：设计、迁移与测试文档。

## 本地构建

```powershell
dotnet restore .\OtaTool.sln
dotnet build .\OtaTool.sln -c Release --no-restore
dotnet run --project .\tests\OtaTool.Core.SmokeTests\OtaTool.Core.SmokeTests.csproj -c Release --no-build
dotnet run --project .\tests\OtaTool.Update.Tests\OtaTool.Update.Tests.csproj -c Release --no-build
```

## 发布 Windows x64 预览

```powershell
.\scripts\Publish-WinX64.ps1
```

默认输出到仓库根目录的 `publish/win-x64`。

## 迁移状态

当前已完成独立仓库迁移，以及在线检查、双重校验、安全解压、独立更新器、启动确认和失败回滚实现。`v0.1.0` 已作为在线升级基线发布；`v0.1.1` 集成了升级任务、模式隔离、日志分析和内置 Patch 验证工具等改进；`v0.1.2` 修复更新窗口只读进度绑定异常，并改善 Node 列表滚轮与耗时显示；`v0.1.3` 增强状态轮询容错、循环升级交互、阶段展示和循环日志分析能力；`v0.1.4` 增加正反向快捷升级、Gateway/异步板版本查询、主题单订阅切换和设备发现保护；`v0.1.5` 将异步设备查询迁移到 `cmd=100`，完善 Extender/Node 遥测展示，并兼容 Gateway 到 Sync OTA 包缓存复用状态；`v0.1.6` 同步最新异步状态查询协议，将应用层查询/响应命令切换为 `0x17/0x18`；`v0.1.7` 将 Node Patch 名称改为具体设备类型前缀，避免不同 Node 类型使用相同版本号时互相覆盖；`v0.1.8` 使用无界面原生验证器替代 `OTA_TOOL`/Qt，增加低分辨率响应式布局、设备 ID 双格式显示及多 Extender Node 目标完整性门禁；`v0.1.9` 修复 v0.1.2～v0.1.7 通过内置更新器跨版本升级时被历史文件清单阻断的问题；`v0.2.0` 增加多任务串行升级队列、自动预检与版本复查、任务历史和计划汇总报告，并完善 Node 分页发现、离线设备展示及整体交互一致性；`v0.2.1` 细化循环失败轮次与 Node 预检原因，增加自适应耗时和日志底部自动跟随，并明确提示旧版非分页 Node 协议不兼容；`v0.2.2` 修复 MQTT 失败重连与 Node 同 ID 多类型识别问题，收紧队列目标范围，并为 Patch 自动命名增加 Unix 时间戳及 Gateway 长度预检；`v0.2.3` 修复时间戳文件名 Patch 被内容哈希去重误判、无法分别发布的问题；`v0.2.5` 改用可持久化的用户后缀生成 Patch 名称，在状态机中实时显示版本方向，并移除低频的手动导入 Patch 验证入口。
