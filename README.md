# OTA 测试平台

用于 EcoLink OTA 差分包制作、发布、升级任务执行、状态机展示、历史报告与日志分析的 Windows 桌面工具。

## 仓库结构

- `src/OtaTool.App`：WPF 桌面应用。
- `src/OtaTool.Core`：协议、差分、发布、任务、报告等核心能力。
- `src/OtaTool.Update`：GitHub Release 检查、下载校验、安全解压与更新任务。
- `src/OtaTool.Updater`：独立目录切换、启动确认与失败回滚程序。
- `tests/OtaTool.Core.SmokeTests`：核心冒烟测试。
- `tests/OtaTool.Update.Tests`：在线升级安全与回滚测试。
- `scripts`：Patch 还原验证与 OTA 日志分析脚本。
- `assets/native`：随应用发布的原生差分工具和 Patch 还原验证运行时；桌面端不依赖机器预装的 `OTA_TOOL`。
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

当前已完成独立仓库迁移，以及在线检查、双重校验、安全解压、独立更新器、启动确认和失败回滚实现。`v0.1.0` 已作为在线升级基线发布；`v0.1.1` 集成了升级任务、模式隔离、日志分析和内置 Patch 验证工具等改进；`v0.1.2` 修复更新窗口只读进度绑定异常，并改善 Node 列表滚轮与耗时显示；`v0.1.3` 增强状态轮询容错、循环升级交互、阶段展示和循环日志分析能力；`v0.1.4` 增加正反向快捷升级、Gateway/异步板版本查询、主题单订阅切换和设备发现保护；`v0.1.5` 将异步设备查询迁移到 `cmd=100`，完善 Extender/Node 遥测展示，并兼容 Gateway 到 Sync OTA 包缓存复用状态；`v0.1.6` 同步最新异步状态查询协议，将应用层查询/响应命令切换为 `0x17/0x18`。
