# 网络集成与运维链路

## 模块职责
- 提供统一 HTTP 访问层（GET/POST/流式/下载/代理测试）。
- 管理外部调用 API（本地 HTTP 服务）与命令路由。
- 管理应用更新（Velopack + GitHub 发布源）。
- 管理配置备份恢复（本地 zip 与 WebDav）。

## 关键入口
- `STranslate/Core/HttpService.cs`
  - 通用 HTTP 封装、超时/请求头/查询参数处理、下载进度。
  - `StreamPostAsync()` / `StreamPostAsyncEnumerable()`：流式 POST 响应消费。
  - `TestProxyAsync()` / `GetCurrentIpAsync()` 代理连通性验证。
- `STranslate/ViewModels/Pages/NetworkViewModel.cs`
  - 网络设置页测试入口与外部调用服务状态展示。
- `STranslate/Core/ExternalCallService.cs`
  - `HttpListener` 本地服务，路由外部请求到主窗口命令。
- `STranslate/Core/UpdaterService.cs`
  - 手动检查更新、后台轮询检查与升级应用流程。
- `STranslate/Controls/UpdateChangelogDialog.xaml(.cs)`
  - 手动更新确认弹窗：加载并渲染远程 `CHANGELOG.md`，失败时回退外链。
- `STranslate/Core/AutoUpdateCheckerService.cs`
  - 自动检查更新调度（首次延迟 + 固定轮询间隔 + 开关判定）。
- `STranslate/Services/BackupService.cs`
  - 本地备份恢复与 WebDav 备份恢复。

## 核心流程
### 从入口到结果：代理测试
1. 网络页执行 `TestConnectionCommand`。
2. `NetworkViewModel.TestConnectionAsync()` 调用：
   - `_httpService.TestProxyAsync()`。
   - `_httpService.GetCurrentIpAsync()`。
3. 将连通结果与 IP 信息回写 `TestResult`。

### 从入口到结果：外部调用 API
1. `Settings.EnableExternalCall=true` 时，`Settings.ApplyExternalCall()` 调用 `ExternalCallService.StartService("http://127.0.0.1:{port}/")`。
2. `ExternalCallService` 用 `HttpListener` 接收请求，解析路径为 `ExternalCallAction`。
3. 按 GET/POST 请求内容路由到 `MainWindowViewModel` 对应命令（翻译、OCR、图片翻译、静默 OCR/TTS、窗口操作、热键开关等）。
4. 统一返回 JSON：`code + data`。

### 从入口到结果：流式 HTTP 请求
1. 插件或主程序通过 `IHttpService.StreamPostAsync()` 使用回调消费逐行响应。
2. 需要 `await foreach` 消费时，使用 `StreamPostAsyncEnumerable()`：
   - 发送 POST 请求时使用 `HttpCompletionOption.ResponseHeadersRead`。
   - 响应成功后逐行读取内容，忽略空行，避免把 SSE 心跳空包暴露给调用方。
   - `CancellationToken` 取消时向上抛出取消状态，不包装为普通业务失败。
3. 两套接口都支持默认 HTTP 客户端和指定 `serviceName` HTTP 客户端。

### 从入口到结果：应用更新（手动）
1. `UpdaterService.UpdateAppAsync()` 使用 `UpdateLock` 防止并发更新。
2. 通过 Velopack `GithubSource` 检查新版本。
3. 非静默检查时弹出 `UpdateChangelogDialog`：
   - 默认先显示加载动画（`ProgressRing`）。
   - 通过 `IHttpService` 拉取 `https://raw.githubusercontent.com/STranslate/STranslate/refs/heads/main/CHANGELOG.md` 并用 `MarkdownViewer` 渲染完整更新内容。
   - 加载失败时显示可点击外链回退文案，用户可在浏览器查看完整更新日志。
4. 用户点击“下载”后继续下载更新；取消则终止本次更新流程。
5. 便携模式下先把便携目录复制到临时目录，避免覆盖丢失配置。
6. 下载完成后，通过 `AppMessageBox` 显示确认弹窗；用户确认重启时执行 `WaitExitThenApplyUpdates()` 并关闭应用。

### 从入口到结果：应用更新（自动检查）
1. 应用启动后，`App.OnStartup()` 在主窗口 `Loaded` 阶段启动 `AutoUpdateCheckerService`。
2. 服务首次延迟 2 分钟执行检查，之后每 24 小时轮询一次。
3. 每轮先判断 `Settings.AutoCheckUpdate`，关闭时跳过本轮检查。
4. 开启时调用 `UpdaterService.NotifyUpdateIfAvailableAsync()`，并复用 `UpdateLock` 防并发。
5. 发现新版本后通过托盘通知展示按钮，点击按钮会跳转到“设置-关于”页面执行更新。

### 从入口到结果：本地与 WebDav 备份恢复
- 本地备份/恢复：
  1. 选择 zip 文件。
  2. 调用 `STranslate.Host`（`backup` 模式）对 `Plugins` 与 `Settings` 打包或还原。
  3. 程序重启并通过 `InfoFilePath` 向主程序传递提示信息。
- WebDav 备份：
  1. 预检查 WebDav 可达。
  2. 重启前通过 Host 先生成本地 zip 并把路径写入 `BackupFilePath`。
  3. 下次启动 `App.WebDavBackupOperation()` 读取路径并调用 `BackupService.PostWebDavBackupAsync()` 上传。
- WebDav 恢复：
  1. 拉取远端可用备份列表。
  2. 下载所选 zip。
  3. 通过 Host 执行还原并重启。

## 关键数据结构/配置
- `Options`（HTTP 参数）
  - `Headers`、`QueryParams`、`Timeout`、`ContentType`。
- `StreamPostAsyncEnumerable`
  - 返回 `IAsyncEnumerable<string>`，适合大模型流式响应或 SSE 逐行处理。
- `DownloadProgress`
  - `DownloadedBytes`、`TotalBytes`、`Speed`、`ElapsedTime`。
- `ExternalCallAction`
  - 外部路由动作枚举。
- 关键设置
  - `Settings.Proxy`、`Settings.HttpTimeout`。
  - `Settings.EnableExternalCall`、`Settings.ExternalCallPort`。
  - `Settings.AutoCheckUpdate`（是否启用自动检查更新）。
  - `Settings.Backup`（地址、账号、密码、备份类型）。

## 关键文件
- `STranslate/Core/HttpService.cs`
- `STranslate/ViewModels/Pages/NetworkViewModel.cs`
- `STranslate/Core/ExternalCallService.cs`
- `STranslate/Core/UpdaterService.cs`
- `STranslate/Controls/UpdateChangelogDialog.xaml`
- `STranslate/Controls/UpdateChangelogDialog.xaml.cs`
- `STranslate/Core/AutoUpdateCheckerService.cs`
- `STranslate/Services/BackupService.cs`
- `STranslate/App.xaml.cs`

## 常见改动任务
- 新增外部调用路径：在 `ExternalCallAction` 增加枚举，并在 `ExecuteExternalCall()` 添加分支。
- 更换更新源或策略：修改 `UpdaterService` 的 `UpdateManager` 源与版本判定逻辑。
- 调整自动检查频率：修改 `AutoUpdateCheckerService` 的轮询间隔常量。
- 下载链路优化：优先扩展 `HttpService.DownloadFileAsync()`，保证进度、取消、异常统一。
- 流式请求改造：优先扩展 `StreamPostAsync()` 与 `StreamPostAsyncEnumerable()` 的共同读取语义，确保取消、空行过滤和错误处理一致。
- 备份策略增强：统一改 `BackupService` 与 Host 参数协议，避免主进程和 Host 参数不一致。
