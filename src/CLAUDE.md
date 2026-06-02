# CLAUDE.md

本文件是仓库文档唯一导航入口。`docs/` 目录保持扁平结构。

## 语言规则
- 所有解释、推理和评论必须使用简体中文。
- 代码标识符、接口名、文件名保持原始英文。
- 错误说明、改动说明与结论统一用中文。

## 全局开发规则
- 项目内 MessageBox 必须统一走 `AppMessageBox.Show()`，不要直接调用 iNKORE `MessageBox.Show()`。

## 最小构建与运行
```powershell
# 最常用：直接运行
 dotnet run --project STranslate/STranslate.csproj

# 调试构建
 dotnet build STranslate.slnx --configuration Debug
```

## 文档导航（唯一入口）

| 功能模块 | 文档 | 重点内容 |
| --- | --- | --- |
| 运行时启动 | [docs/runtime-bootstrap.md](docs/runtime-bootstrap.md) | 单实例、DI、窗口生命周期、异常与退出链路 |
| 插件与服务运行时 | [docs/runtime-plugin-service.md](docs/runtime-plugin-service.md) | 插件发现/加载、服务实例化、PluginContext 与配置持久化 |
| 主翻译链路 | [docs/flow-main-translation.md](docs/flow-main-translation.md) | 自动翻译、防抖、缓存命中、回译、复制与词典路径 |
| OCR 与图片翻译 | [docs/flow-ocr-image.md](docs/flow-ocr-image.md) | 截图入口、OCR窗口、版面分析、图片翻译专用服务 |
| 输入与触发系统 | [docs/flow-input-trigger.md](docs/flow-input-trigger.md) | 全局/软件内热键、低级键盘钩子、Ctrl+CC、鼠标划词、剪贴板监听 |
| 插件市场与管理 | [docs/plugin-market-management.md](docs/plugin-market-management.md) | 已安装插件管理、市场加载、下载/取消/升级/重启策略 |
| 配置、存储与历史 | [docs/config-storage-history.md](docs/config-storage-history.md) | Settings/ServiceSettings、存储抽象、便携/漫游路径、历史记录 |
| 网络集成与运维 | [docs/integration-network-ops.md](docs/integration-network-ops.md) | HTTP层、代理测试、外部调用、更新、备份恢复 |
| 插件SDK开发 | [docs/plugin-sdk-development.md](docs/plugin-sdk-development.md) | SDK接口、插件生命周期、`plugin.json` 规范、官方实现范式 |

## 模块检索入口

| 任务场景 | 先看文档 |
| --- | --- |
| 启动崩溃/窗口行为异常 | [docs/runtime-bootstrap.md](docs/runtime-bootstrap.md) |
| 插件加载失败/服务实例异常 | [docs/runtime-plugin-service.md](docs/runtime-plugin-service.md) |
| 翻译结果异常/缓存不命中 | [docs/flow-main-translation.md](docs/flow-main-translation.md) |
| OCR 或图片翻译结果异常 | [docs/flow-ocr-image.md](docs/flow-ocr-image.md) |
| 热键冲突/触发不生效 | [docs/flow-input-trigger.md](docs/flow-input-trigger.md) |
| 插件市场下载或升级问题 | [docs/plugin-market-management.md](docs/plugin-market-management.md) |
| 配置丢失/路径不一致/历史问题 | [docs/config-storage-history.md](docs/config-storage-history.md) |
| 代理、更新、备份与外部调用 | [docs/integration-network-ops.md](docs/integration-network-ops.md) |
| 新增或改造插件 | [docs/plugin-sdk-development.md](docs/plugin-sdk-development.md) |
