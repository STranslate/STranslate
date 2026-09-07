# 翻译前正则替换功能规划

状态：首版已实现并通过离线验收。原设计核对日期为 2026-09-06，实现及验收日期为 2026-09-07。

## 目标与默认假设

用户输入翻译内容后，按配置的正则规则替换匹配片段，将处理结果发送给下游翻译提供商。

“部分文字”指正则命中的片段。首版采用全局规则，输入框继续显示原文，发送文本使用独立副本；默认关闭，开启后对各翻译入口生效。译文保留替换产生的表达。译后恢复原内容需要额外的占位符映射及完整性检查，属于后续扩展。

例如规则 `\bACME-\d+\b` → `产品A`，将 `Please review ACME-123 today.` 处理为 `Please review 产品A today.`，下游只接收后者。未匹配内容保持原样。

## 设计时的接入依据

下表保留实施前的接入分析；当前执行方式见 [主翻译链路](flow-main-translation.md)、[图片翻译链路](flow-image-translation.md) 和 [配置与历史](config-storage-history.md)。

| 当前位置 | 已核实的行为 | 规划中的调整 |
| --- | --- | --- |
| [MainWindowViewModel.cs](../STranslate/ViewModels/MainWindowViewModel.cs) | 主翻译、单服务执行、缓存、语言识别由此编排 | 请求开始时准备一次处理结果；缓存与识别显式使用该上下文 |
| [TranslationOperation.cs](../STranslate/Core/TranslationOperation.cs) | 保存原文快照，构建 `TranslateRequest`，执行词典和回译，隔离流式结果 | 区分原文与请求文本，主翻译和词典使用处理后的文本 |
| [TranslationResultCoordinator.cs](../STranslate/Core/TranslationResultCoordinator.cs) | 管理操作编号和结果发布所有权 | 保持“先登记新操作，再执行耗时处理”的时序 |
| `MainWindowViewModel.ReplaceTranslateAsync()` | 取词后直接识别语言、调用翻译插件，再输出译文 | 在语言识别前接入共享处理器 |
| [ImageTranslateWindowViewModel.cs](../STranslate/ViewModels/ImageTranslateWindowViewModel.cs) | OCR 布局分块后，每块独立识别语言并直接调用翻译插件 | 整张图共用规则快照；每块发送前处理一次 |
| [LanguageDetector.cs](../STranslate/Helpers/LanguageDetector.cs) | 同时支持本地和在线语言检测 | 统一检测处理后的文本，用户指定源语种仍优先 |
| [SqlService.cs](../STranslate/Core/SqlService.cs) | 按原文、源语种、目标语种查历史，服务结果保存在 `HistoryData` JSON 中 | 服务结果增加预处理上下文；读取、显示、补译和合并均验证上下文 |
| [Settings.cs](../STranslate/Core/Settings.cs) | 顶层属性变化触发配置保存 | 增加预处理配置，规则编辑完成后整体替换配置对象 |
| [TranslateService.cs](../STranslate/Services/TranslateService.cs) | 管理服务列表及专用服务选择 | 它不是所有请求共用的执行入口，不能只在此处添加替换 |

现有 `HandleCapturedText()` 负责特定取词入口的换行和分隔符处理，键盘输入不经过这里。新增正则处理放在真正执行翻译时，避免输入过程反复替换，也覆盖粘贴、取词、外部调用等进入主翻译的路径。

## 功能与交互

在“设置 → 翻译”点击“翻译前替换”打开规则管理界面。

| 能力 | 首版行为 |
| --- | --- |
| 全局开关 | 默认关闭；关闭时保留现有行为 |
| 规则管理 | 新增、编辑、删除、单条启停、上移下移 |
| 规则字段 | 名称、正则表达式、替换文本、忽略大小写、多行模式、单行模式 |
| 执行顺序 | 按列表从上到下执行；后一条处理前一条的结果 |
| 测试预览 | 输入样例，展示最终发送文本、各规则命中次数、错误位置；使用正式引擎，无网络请求 |
| 主窗口提示 | 匹配发生时显示“已替换 N 处”，可查看本次实际发送文本 |
| 保存行为 | 编辑使用草稿；校验通过后整体应用，取消编辑不改变运行中配置 |

界面名称使用“翻译前替换”，与现有把译文写回外部输入框的“替换翻译”区分。规则编辑弹窗按项目约定显式指定 owner；运行失败用当前窗口 Snackbar，避免自动翻译时反复弹窗。

## 规则语义和错误处理

1. 使用 .NET `Regex` 语法。每条规则替换全部非重叠匹配，支持 `$1`、`${name}` 等捕获组引用；字面量 `$` 使用 `$$`。提供示例，替换框支持实际换行，不额外对用户内容做通用转义解码。
2. 每条启用规则执行一遍，不循环执行到文本稳定。例如 A→B、B→C 的最终结果是 C；A→AA 不会无限展开。重试从原文重新计算一次。
3. 空替换文本合法，表示删除；空正则不允许保存。合法的零宽匹配按 .NET 语义执行，预览明确展示结果。
4. 未开启或没有启用规则时原样返回。开启但无匹配时也原样返回，同时保留本次规则上下文。
5. 保存时验证正则，运行时仍处理外部编辑配置、正则超时等错误。任一启用规则失败，终止本次发送并指出规则名称；不将未完成处理的文本继续发给提供商。
6. 替换后为空或只有空白时，提示“替换后没有可翻译内容”，不调用语言检测、词典或翻译插件，也不自动复制旧结果。
7. 正则设置有限超时，例如首轮以每条 100 ms、整次 500 ms 为调优起点；同时限制中间输出增长，避免替换造成内存暴涨。输出上限需要在构造结果过程中检查，不能只在完整字符串分配后检查。长文本预览和执行放到后台，规则间检查取消。
8. 缓存验证过的 Regex 实例，按规则快照更新；不要在每个翻译提供商里重新解析、重新替换。错误诊断记录规则 ID 和错误类别，不新增原文/命中片段日志。

## 执行链路与作用范围

主流程顺序为：完成既有输入处理，快照原文和规则，执行正则替换，验证缓存，未命中时识别语言，再把同一份处理结果交给各提供商。语言识别放在替换之后，因为现有检测器可能发送文本到在线服务。

新增宿主内部 `TextPreprocessingService`，返回不可变 `PreparedTranslationInput`，包含 `OriginalText`、`RequestText`、`RulesFingerprint` 和命中统计。配置模型建议为 `TextPreprocessingSettings` 与 `TextReplacementRule`；规则用稳定 `Id` 标识，顺序以列表位置为准。

`TranslationOperation.Text` 继续表示原输入，历史导航和输入区使用它；操作另外保存一次性准备好的输入上下文。先登记操作编号和取消令牌，再执行后台预处理，避免新请求仍在处理时旧任务发布结果。预处理完成后检查取消及所有权。后续异步流程不重新读取可变的规则列表。

| 路径 | 处理方式 |
| --- | --- |
| 主窗口手动/自动翻译 | 一次输入只预处理一次，所有自动服务共用结果 |
| 单服务执行、重试、提示词切换后重新翻译 | 从当次原文快照重新处理，应用当次规则 |
| 同批词典查询 | 使用同一份处理文本，避免同一输入在词典路径绕过规则 |
| 截图翻译、取词、剪贴板、外部文本调用 | 经主翻译链路统一处理 |
| 现有“替换翻译” | 取词完成后、语言检测前处理；成功后仍输出译文 |
| 图片翻译 | OCR 布局分块后，每块独立处理；整张图固定同一规则快照，不跨布局块匹配 |
| 自动/手动回译 | 使用对应主译文，不再套用面向原输入的规则；回译必须与产生它的主译文绑定 |
| 仅 OCR、朗读、生词本保存 | 属于其他功能，本稿不改变它们的文本处理语义 |

图片中某块规则失败或结果为空时，该块不向检测器/翻译器发送，保留原图显示并汇总提示，避免并发提示刷屏；其他块可以继续完成。不能把失败的处理静默回退成发送 OCR 原文。正则处理发生在 OCR 之后，不会改写已经发送给 OCR 服务的图片。

主输入框、OCR 原文和布局坐标保持原有含义，处理结果只改变发往翻译链路的文本。普通及流式翻译都通过已有插件调用接口接收 `RequestText`，SDK 的 `TranslateRequest` / `ITranslatePlugin` 无需增加字段。

## 配置、缓存与历史

配置放入现有 `Settings.json`，建议增加一个顶层 `TextPreprocessing` 属性，默认值为关闭及空规则。界面编辑草稿与持久对象分离，通过整体替换该属性触发现有自动保存机制；仅在嵌套列表里改字段不会自动触发当前 `Settings.PropertyChanged`。旧配置缺字段时使用默认值；已有字段损坏时提示配置错误，避免把错误的启用配置当作有效空规则运行。沿用 `AppStorage`、原子保存和备份恢复，并验证备份往返能保留新字段。

历史继续用原文和语种定位记录，建议在 `HistoryData` 的 JSON 中增加可选的 `RequestText`、`PreprocessingFingerprint`，以及对应服务结果的实际语种快照。按服务保存是因为单服务重试可能只更新部分结果，而不同结果可能使用不同版本的规则。现有 `RawData` 能容纳这些新增字段，首版无需增加 SQLite 列。

- 指纹包含规则语义版本、启用规则的顺序、表达式、替换文本和选项；规则显示名不参与。关闭或无启用规则用约定的空指纹。
- 查询后逐服务校验指纹和实际请求文本，再决定是否复用。当前 `PopulateResultsFromCache()` 会先展示缓存再检查完整性，需改为验证通过后才能展示，包括识别语种状态。
- 修改规则、顺序、启停状态后，旧结果不再作为本次有效缓存；无需删除用户历史。功能关闭时可复用同样未预处理的缓存。
- 旧记录缺字段时按“未预处理”解释，实际请求文本回退为该记录的原文；有启用规则时保守地重新翻译。
- 手动历史合并必须比较上下文：上下文改变时替换当前服务的主译及其回译组合，不能用旧回译填补新结果中的空字段。跨上下文的“仅补回译”不能关联到新的主翻译。
- 历史页面展示原文与已存译文，并支持按服务查看当时的发送文本及实际语种。浏览历史不重新处理历史内容；重新翻译才使用当前规则。
- 多提供商并发、单服务重试和旧操作迟到仍遵守现有操作所有权约束，预处理失败不能触发自动复制或写入成功历史。

## 实施拆分

| 步骤 | 交付内容 | 验收依据 |
| --- | --- | --- |
| 1. 配置与引擎 | 配置模型、纯处理引擎、快照、错误类型、超时与输出限制 | 规则顺序、捕获组、删除、无匹配、取消及错误用例通过 |
| 2. 主翻译与缓存 | 接入操作上下文、语言检测、词典、单服务；新增历史元数据和兼容判断 | 替换后的文本到达伪提供商；规则改变后不复用旧缓存；旧操作不能覆盖新结果 |
| 3. 其他翻译入口 | 接入现有替换翻译和图片翻译 | 两个直接调用路径均使用共享处理器；图片块固定快照、错误不回退发送 |
| 4. 设置与预览 | 规则列表、草稿编辑、启停排序、本地预览、实际发送文本查看 | 预览与真实请求一致；保存、取消、重启、备份恢复行为符合约定 |
| 5. 回归与文档 | 中英文资源、使用示例、功能链路文档更新 | 完成下列测试与人工场景；首版覆盖所有上述入口后交付 |

预计主要改动：新增模型和处理服务；调整 `Settings.cs`、`App.xaml.cs` 注册、`TranslationOperation.cs` / `TranslationResultCoordinator.cs`、`MainWindowViewModel.cs`、`ImageTranslateWindowViewModel.cs`、`SqlService.cs` 中的历史模型及使用点；增加规则编辑控件及 ViewModel，扩展 `TranslatePage` 和历史详情、`Languages/zh-cn.xaml` / `en.xaml`。

## 验收清单

以下为首版验收要求，验证记录见本文末尾。

1. 单条/多条规则、顺序变化、单条关闭、全局关闭、无匹配、全量删除、捕获组、字面量 `$`、Unicode、多行与零宽匹配符合约定，原文始终保留。
2. 无效正则、灾难性回溯、输出膨胀、取消和空结果均在受影响请求的网络调用前终止；预览与执行给出同类错误。
3. 伪语言检测器、普通/流式翻译插件、词典插件收到相同的预处理文本；多提供商不会重复处理，重试从原文开始。
4. 正则规则在请求中途修改时，旧请求仍使用原快照，新请求使用新规则；取消、失败及迟到回调不覆盖新请求、复制旧结果或错误入库。
5. 缓存测试覆盖旧 JSON、开关切换、规则编辑/重排、混合服务命中、失败结果、单服务合并、缓存仅补回译；验证 UI 不先闪现不匹配的旧结果。
6. 覆盖主窗口输入、自动翻译、粘贴/取词/截图入口、单服务重试、现有替换翻译、图片翻译；回译使用正确主译文，图片规则不跨块匹配。
7. 保存后重启与备份恢复保持规则、顺序和开关；取消编辑不生效；中文/英文界面、弹窗 owner、错误提示与处理文本预览正常。

引擎与编排用例放入现有 `Tests/STranslate.Tests`，扩展 `TranslationResultCoordinatorTests` 的取消和并发场景。用伪提供商验证实际参数，避免测试依赖真实外部接口。

实施完成后，在 `src` 目录运行 `dotnet test Tests/STranslate.Tests/STranslate.Tests.csproj --configuration Debug`，以及 `dotnet build STranslate.slnx --configuration Debug`；随后对设置界面和上述翻译入口执行人工冒烟。按需更新 `flow-main-translation.md`、`flow-image-translation.md`、`config-storage-history.md`，保持 `AGENTS.md` 为文档导航入口。

后续可单独扩展按服务/语种配置规则、规则导入导出、译后还原；当前首版先完成全局输入替换的完整闭环。

## 使用示例

新增规则后填写表达式和替换文本，启用全局开关并保存。可先在弹窗下方输入样例检查结果；取消会丢弃本次全部编辑。

| 表达式 | 替换文本 | 输入 | 实际请求 |
| --- | --- | --- | --- |
| `\bACME-\d+\b` | `产品A` | `Review ACME-123.` | `Review 产品A.` |
| `(?<code>ACME)-(?<number>\d+)` | `${code} product ${number}` | `ACME-42` | `ACME product 42` |
| `secret` | 留空 | `remove secret here` | `remove  here` |
| `USD` | `$$` | `USD 10` | `$ 10` |

需要实际换行时直接在替换框中换行。输入框中的原文不会变化，主窗口的请求详情按钮和历史记录中的请求详情按钮分别展示本次及当时的发送文本。

## 验证入口

```powershell
# 在 src 目录运行
dotnet test Tests/STranslate.Tests/STranslate.Tests.csproj --configuration Debug
dotnet build STranslate.slnx --configuration Debug
dotnet run --project Tests/STranslate.UiSmoke/STranslate.UiSmoke.csproj --configuration Debug -- .artifacts/preprocessing-ui-smoke
```

`STranslate.UiSmoke` 使用独立进程、本地伪提供商和输出目录内的便携配置/SQLite，不连接真实翻译服务。它运行真实主翻译命令，点击中英文弹窗按钮，验证保存/取消、请求详情、完整 Settings 重启和 ZIP/备份恢复，并在 `.artifacts/preprocessing-ui-smoke` 输出 1000×780、700×600 窗口截图。引擎、缓存和并发测试继续放在 `STranslate.Tests`。

## 验收记录

2026-09-07 完成五个实施步骤，检查结果如下。

| 验收项 | 当前证据 |
| --- | --- |
| 1. 规则语义与原文保留 | `TextPreprocessingServiceTests`、`TextPreprocessingEdgeCaseTests` 与 .NET 原生替换对照；覆盖顺序、开关、捕获组、删除、Unicode、多行、零宽、无匹配和字面量美元符 |
| 2. 错误与资源限制 | 无效规则、灾难性回溯、总时限、捕获组膨胀前的分配上限、取消、空结果测试；`TextPreprocessingEditorTests` 验证预览错误和保存校验 |
| 3. 实际发送参数 | `TranslationOperationPreprocessingTests` 和 `MainTranslationSmoke` 验证检测、两个流式伪提供商、词典共用处理文本；主输入保持原文，单服务重试从原文开始 |
| 4. 快照与并发所有权 | `TranslationResultCoordinatorTests` 与预处理操作测试覆盖旧流式回调、取消、新请求处理失败、规则中途修改和一次准备多次复用；实际主命令验证错误/空结果不调用服务、不保留旧显示 |
| 5. 缓存与回译 | `TranslationCacheContextTests` 直接调用生产缓存填充逻辑，覆盖旧 JSON、混合命中、失败主译/回译、重排/改名/开关及不匹配结果零发布；`HistoryDataPreprocessingTests` 覆盖主译替换及回译合并；真实主命令验证 SQLite 元数据和缓存重用 |
| 6. 入口覆盖 | 主窗口手动/自动服务、单服务和回译由真实命令验证；粘贴、取词、截图、剪贴板、外部调用的 `ExecuteTranslate` 路由已核对；替换翻译与图片块共用 `DirectTranslationRunner`，伪检测器/提供商测试覆盖取消、空结果和不跨块匹配；失败图片块不生成覆盖层 |
| 7. 设置与界面 | 草稿取消/校验/一次保存、预览竞争测试通过；独立 WPF 进程点击中英文弹窗保存/取消并查看请求详情；两种尺寸的 8 张截图已检查；完整 Settings 同步重启、ZIP 往返和 `.bak` 恢复通过 |

- `dotnet test Tests/STranslate.Tests/STranslate.Tests.csproj --configuration Debug`：333 通过，0 失败，0 跳过。
- `dotnet build STranslate.slnx --configuration Debug`：25 个项目构建成功，0 警告，0 错误。
- `dotnet run --project Tests/STranslate.UiSmoke/STranslate.UiSmoke.csproj --configuration Debug -- .artifacts/preprocessing-ui-smoke`：主翻译流程及中英文界面两组 PASS。
- 验证使用本地伪提供商和窗口交互自动化，未调用真实外部翻译/OCR API，也未启用全局鼠标 Hook。截图、取词等操作系统取词本身沿用既有实现；本次检查集中在取得文本后的预处理发送路径。

实施过程中额外修复了异步备份恢复的 Windows 文件占用问题：`StorageBase.TryLoadBackupAsync()` 现在在 `File.Replace` 前关闭读取流，相应往返测试已覆盖。
