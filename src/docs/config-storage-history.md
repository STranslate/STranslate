# 配置、存储与历史数据

## 模块职责
- 维护全局设置模型与服务配置模型。
- 提供统一 JSON 存储抽象（应用配置与插件配置）。
- 统一便携模式/漫游模式路径策略。
- 负责翻译历史 SQLite 存储模型与读写逻辑。
- 负责历史记录 CSV 导出、批量/全量删除与历史展示快照字段兼容。

## 关键入口
- `STranslate/Core/Settings.cs`
  - 主配置模型，属性变更自动保存并触发行为应用。
- `STranslate/Core/ServiceSettings.cs`
  - 服务实例列表与特殊服务 ID（替换翻译、图片翻译）。
- `STranslate/Core/StorageBase.cs`
  - 通用存储抽象：加载、保存、备份恢复、原子写入。
- `STranslate/Core/AppStorage.cs`
  - 应用级配置存储：`SettingsDirectory/{TypeName}.json`。
- `STranslate/Core/PluginStorage.cs`
  - 插件级配置存储：`PluginSettingsDirectory/{serviceId}.json`。
- `STranslate/Core/DataLocation.cs`
  - 便携/漫游目录与缓存/日志/数据库路径决策。
- `STranslate/Core/SqlService.cs`
  - 历史表初始化、读写、分页、游标与相邻记录查询。
- `STranslate/Helpers/HistoryCsvHelper.cs`
  - 历史记录 CSV 宽表导出。
- `STranslate/ViewModels/Pages/HistoryViewModel.cs`
  - 历史页搜索、加载更多、导出、批量删除与全量删除。

## 核心流程
### 从入口到结果：配置加载、行为应用与自动保存
1. 启动阶段由 `AppStorage<T>.Load()` 读取：`Settings`、`HotkeySettings`、`ServiceSettings`。
2. `SetStorage()` 后，`Settings.PropertyChanged` 会自动触发：
   - 行为应用（语言、主题、字体、开机启动、外部调用、日志级别等）。
   - 配置落盘（部分高频字段使用 500ms 防抖保存）。
3. `StorageBase.Save()` 采用临时文件 + 原子替换，降低写入中断导致损坏的风险。
4. `StorageBase.Load()` 遇到 JSON 异常时回退 `.bak`，再失败则使用默认对象。
5. 首次欢迎向导不使用额外完成标记：`App` 在 `Load()` 前检查 `Settings.json`、`HotkeySettings.json`、`ServiceSettings.json` 是否都不存在；都不存在才在主窗口创建前自动打开向导。向导跳过、关闭或完成时保存三类配置，生成文件后后续启动不再自动弹出。首页语言选择直接绑定 `Settings.Language`，沿用现有语言切换和保存行为。

### 翻译前替换配置
- `Settings.TextPreprocessing` 包含全局开关和有序 `TextReplacementRule` 列表，默认关闭且为空。规则 ID 持久保存；指纹只包含启用规则的顺序、表达式、替换模板、匹配选项及语义版本，显示名不影响缓存。
- 编辑器克隆配置，保存时校验并整体替换顶层属性以触发自动保存，取消不修改持久对象。替换文本保留实际换行；不做通用反斜线解码。
- 旧 `Settings.json` 缺少该字段时使用默认值；字段为 null、列表损坏或结构不合法时，专用 JSON converter 保留错误值并阻止翻译，不把错误配置静默当作关闭状态。
- 配置沿用 `AppStorage` 和现有 ZIP/WebDAV 备份范围。异步 `.bak` 恢复先释放读取流再原子替换，避免 Windows 文件占用错误。

### 从入口到结果：便携/漫游路径策略
1. `DataLocation.PortableDataLocationInUse()` 判断程序目录下是否存在便携目录。
2. 若存在：配置、缓存、日志、插件目录全部落在 `PortableDataPath`。
3. 若不存在：落在 `RoamingDataPath`（`%AppData%/STranslate`）。
4. 数据库连接串固定指向 `CacheDirectory/history.db`。

### 从入口到结果：历史数据写入与缓存命中
1. 首次启动执行 `SqlService.InitializeDB()` 创建 `History` 表。
   - 旧库会通过 `PRAGMA table_info(History)` 检查并补齐 `EffectiveSourceLang` / `EffectiveTargetLang` 列。
2. 翻译完成后调用 `InsertOrUpdateDataAsync(history, count)`：
   - 超上限时先删除最旧记录。
   - 同 `(SourceText, SourceLang, TargetLang)` 命中则更新，否则插入。
3. `HistoryModel.RawData` 负责将 `List<HistoryData>` 与 JSON 字段互转；`HistoryData.ServiceDisplayName` 保留服务显示名快照。
4. `HistoryModel.EffectiveSourceLang` / `EffectiveTargetLang` 保存自动识别后实际参与翻译的语言。
5. 查询路径包括：精准命中、模糊检索、分页、上一条/下一条导航。
6. `HistoryData` 逐服务保存 `RequestText`、`PreprocessingFingerprint`、`EffectiveSourceLang`、`EffectiveTargetLang` 和 `BackTranslationSourceText`，无需新增 SQLite 列。
7. 缓存先验证请求文本、指纹和成功状态再发布。缺字段的旧 JSON 按未预处理解释；启用规则时保守重译。更新历史的整条语种前，把旧语种和请求上下文补入各服务数据，避免混合结果互相覆盖实际语种。
8. 手动合并新主译时一起替换其回译；仅补回译必须与原主译文本及请求上下文一致。失败回译可重试，但不能带出其他主译的旧回译。
9. 历史详情的每个服务提供请求详情按钮，可查看当时的发送文本及实际语种。浏览历史不重新执行当前规则，重新翻译才使用当前配置。

### 从入口到结果：历史导出与删除
1. 历史页选择部分记录时，`ExportHistoryAsync()` 只导出选中项，导出后清空选择。
2. “导出全部”通过 `SqlService.GetDataAsync()` 读取全部历史，不依赖当前分页加载状态。
3. `HistoryCsvHelper.BuildCsv()` 输出 UTF-8 with BOM CSV，列结构为基础字段 + 动态翻译引擎列：
   - 序号、记录 ID、时间、原文语言、目标语言、翻译原文。
   - 每个服务一组“翻译引擎 N / 翻译结果 N”。
4. 原始语言为 `Auto` 且存在有效语言时，导出和历史页统一显示为 `Auto(有效语言)`。
5. “删除选中”逐条删除并汇总成功/失败数量；“删除全部”走 `SqlService.DeleteAllDataAsync()` 后清空当前 UI 集合。

## 关键数据结构/配置
- `Settings`
  - UI/主题/语言/窗口位置/自动翻译/网络/插件市场/OCR分段逻辑/外部调用/自动检查更新等全局配置。
  - 取词相关：`TextSeparatorHandleType`、`TextSeparatorHandleScopes`、`SelectedTextFetchTimeoutMs`、`CrosswordFetchFailedFallbackTarget`。
  - 窗口相关：`MainWindowMaxHeightRatio`、`ShowImageTranslateItemInNotifyIconMenu`、`ImageTranslateWindowMode`。
- `ServiceSettings`
  - `TranSvcDatas/OcrSvcDatas/TtsSvcDatas/VocabularySvcDatas`。
  - `ReplaceSvcID/ImageTranslateSvcID/ImageTranslateOcrSvcID`。
  - 欢迎向导会复用服务页的服务创建逻辑和插件配置 UI，以 5 页流程配置语言、翻译/OCR 服务、图片翻译/替换翻译专用服务和关键快捷键。
- `ProxySettings` 与 `BackupSettings`
  - 网络代理与备份目标配置。
- `HistoryModel` / `HistoryData`
  - 历史记录主表模型与每服务结果载体。
  - `EffectiveSourceLang` / `EffectiveTargetLang`：自动识别后的实际语言快照。
  - `ServiceDisplayName`：服务显示名快照，导出和历史页展示优先使用。

## 关键文件
- `STranslate/Core/Settings.cs`
- `STranslate/Core/ServiceSettings.cs`
- `STranslate/Core/StorageBase.cs`
- `STranslate/Core/AppStorage.cs`
- `STranslate/Core/PluginStorage.cs`
- `STranslate/Core/DataLocation.cs`
- `STranslate/Core/SqlService.cs`
- `STranslate/Helpers/HistoryCsvHelper.cs`
- `STranslate/ViewModels/Pages/HistoryViewModel.cs`
- `STranslate/Converters/HistoryLanguageDisplayConverter.cs`
- `STranslate/Converters/HistoryServiceDisplayNameConverter.cs`
- `STranslate/Core/ProxySettings.cs`
- `STranslate/Core/BackupSettings.cs`

## 常见改动任务
- 新增设置项：
  1. 在 `Settings` 或 `ServiceSettings` 增加属性。
  2. 需要即时行为时补充 `HandlePropertyChanged` 分支。
  3. 在对应 UI 页面绑定并验证序列化兼容。
  4. 对数值型设置补充归一化逻辑时，确保保存分支覆盖该属性。
- 调整首次向导时，不要新增“是否完成向导”字段；启动条件以三类配置文件启动前是否存在为准，并保持自动向导先于主窗口显示。
- 调整存储可靠性：优先改 `StorageBase`，避免在业务层重复实现备份恢复。
- 修改便携策略：改 `DataLocation`，并验证升级流程中的 `TmpConfigDirectory` 回迁逻辑。
- 历史结构演进：新增表列需在 `SqlService.InitializeDB()` 补迁移逻辑；修改 `HistoryData` 时需确认 `RawData` 兼容旧版本 JSON。
- 调整历史导出：优先改 `HistoryCsvHelper`，保持历史页展示与导出语言/服务名解析一致。
