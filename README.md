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
- `assets/native`：随应用发布的原生差分工具。
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

当前已完成独立仓库迁移，以及在线检查、双重校验、安全解压、独立更新器、启动确认和失败回滚实现。推送 `vMAJOR.MINOR.PATCH` 标签后，Release 工作流会生成便携 ZIP 与 SHA-256 文件。首次真实在线升级仍需在 GitHub 发布 `v0.1.0`、`v0.1.1` 后完成闭环验收。
