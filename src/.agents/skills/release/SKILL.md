---
name: release
description: 为 STranslate 项目创建发布版本。当用户要发布新版本、打 tag 发版、更新 CHANGELOG、生成版本更新说明时使用。触发短语包括"发布版本"、"打 tag 发版"、"发版"、"更新 changelog"、"准备发布"、"release"、"发一个新版本"。即使用户只是想看看自上次发版后改了什么，也应触发此技能来整理变更。
---

# Release — STranslate 版本发布

为 STranslate 项目准备一次正式发布：校验 tag、汇总自上一个 tag 以来的改动、按既定风格生成 `CHANGELOG.md`，并给出打 tag 推送以触发 GitHub Actions 自动打包发布的步骤。

## 背景事实（项目现状，开工前已核实）

- 发版触发：推送形如 `v2.0.x`（`v` + 三段语义化版本号，`x` 为占位符）的 tag → `.github/workflows/dotnet.yml` 在 windows-latest 上构建，并用 `CHANGELOG.md` 作为 GitHub Release 的 body（`body_path: CHANGELOG.md`），产物发布到 `publish/*`。**无需 Token，`permissions: contents: write`。**
- 版本号来源：tag 名即版本号。`build.ps1` 会去掉 `v` 前缀并以 `/p:Version=` 传入 dotnet 构建，同时改写 `src/SolutionAssemblyInfo.cs` 后用 `git restore` 还原。**不要手动改任何版本号文件。**
- tag 形式：历史 tag 为**带注释的 tag**（`git cat-file -t v2.0.8` → `tag`），用 `git tag -a` 创建。
- `CHANGELOG.md` 是**整文件覆盖**式发布说明，每次发版前重写为本次内容（GitHub Release body 就是它）。
- 插件 NuGet 包版本独立于应用 tag 版本：存在 `src/STranslate.Plugin/STranslate.Plugin.csproj` 的 `<PackageVersion>`，仅当 `src/STranslate.Plugin/` 下公共 API 有变更时升 patch；版本记录累积在同目录 `README.md` 顶部。

## 发布流程

### 1. 确定版本号（严格校验）

1. 取最新 tag：`git describe --tags --abbrev=0`（取最新 `vX.Y.Z` tag）。
2. tag 格式严格为 **`v` + 三段语义化版本号**，即 `v主.次.修订`，例如 `v2.0.9`、`v2.1.1`、`v3.0.9` 均合法。`v` 前缀不可省略，必须是**恰好三段数字**。
   - 默认下一版本 = 当前 patch + 1（如 `v2.0.8` → `v2.0.9`），用于纯 bugfix / 小幅优化发版。
   - **minor / major 跳级是允许的**：若本次含较大新功能可由用户指定 `v2.1.0`，跨大版本重构可指定 `v3.0.0`。版本号段由用户决定，技能不替用户判断该跳 minor 还是 patch——但**格式必须合规**。
   - **严禁**：去掉 `v` 前缀、用 `v2.0.x` 这种字面占位符、四段号或带构建号后缀（如 `v2.0.9.1`、`v2.0.9-beta`）、只有两段（如 `v2.0`）。
   - 历史上早期 tag 曾是 `2.0.x`（无 `v`）或 `1.5.x.x` 形式，但 **v2.0.0 起统一为 `v主.次.修订`**，新发版一律沿用此格式。
3. 如果用户给定版本号，先校验：必须匹配正则 `^v\d+\.\d+\.\d+$`，否则停下来告知用户格式不符并给出正确建议，不要自行假设。

### 2. 汇总改动（核心）

1. 获取自上个 tag 以来的提交：
   ```
   git log <上一个tag>..HEAD --pretty=format:"%h %s" --no-merges
   ```
   合并提交（Merge）默认用 `--no-merges` 过滤掉，因为它们不携带语义信息。
2. **过滤规则**（这是本技能的关键约束）：
   - **忽略纯发版/版本号提交**：`chore: update version to v...`、`docs: update changelog and publish ...`、`chore: bump version`、`c1deb830` 这类仅改 CHANGELOG / 版本号的提交一律不计入更新说明。
   - **忽略新增功能的修复类提交不进 CHANGELOG**：用户明确要求"忽略新增功能的修复 commit"。即：
     - 修复的是**本次开发周期内新增、尚未发布的特性**所引入的 bug（提交信息通常是 `fix(compact): ...`、`fix(placement): ...`、`fix(image-translate): ...` 等，且该特性在同周期内才由 `feat` 引入）→ **不单独列条目**，其效果并入对应特性的"添加/优化"条目即可，不单写"修复：xxx"。
     - 修复的是**既有功能、面向用户历史版本的回归** → **正常列入**"修复："。
     - 判断不确定时，看该 `fix` 涉及的模块在 `<上一个tag>` 是否已存在：不存在→属于新功能自修，忽略；存在→面向用户的回归修复，列入。
   - **忽略**纯 `chore:`（除版本号外如 `chore: revert ...`、`chore`）、纯 `docs:`（设计文档、实现计划、规格说明）、纯内部 `refactor:` 无用户可见行为变化的提交——除非 refactor 带来了用户可感知的优化（则归入"优化："）。
   - `feat:` / `perf:` / 有用户可见效果的 `refactor:` / 面向用户的 `fix:` → 进入更新说明。
3. 中文提交信息（如"更新软件&文档描述"）按其真实语义归类，拿不准语义的琐碎提交宁可略去，保持更新说明清爽。
4. **检测插件契约变更**：用 `git diff <上一个tag>..HEAD --stat -- src/STranslate.Plugin/` 看插件项目是否有改动；若有，进一步确认是否为公共 API 变更（判断标准见第 3 步）。这是是否执行第 3 步的依据。

### 3. 同步插件版本号与 README（仅当插件契约有变更）

第 2 步判定 `src/STranslate.Plugin/` 下公共 API 有变更时执行本步；无变更则跳过。

**什么是"插件契约变更"**：面向插件作者的公共契约发生变化——`IOcrPlugin`/`ITranslatePlugin`/`ITtsPlugin`/`IVocabularyPlugin`/`IPluginContext` 等接口、`Service`/`PluginMetaData` 等公共类、`LangEnum` 等枚举的**公共成员**被新增/修改/移除。纯 `InternalsVisibleTo`、纯 `internal`/`private` 实现、纯 `.csproj` 包引用调整不算。拿不准时用 `git diff <上一个tag>..HEAD -- src/STranslate.Plugin/<文件>.cs` 核实。

**升版本号**：改 `src/STranslate.Plugin/STranslate.Plugin.csproj` 的 `<PackageVersion>`，patch + 1（如 `1.0.12` → `1.0.13`）。插件 NuGet 包版本独立于应用 tag 版本，不跟 `v2.0.x` 走，按自身 patch 递增；仅当出现破坏性 API 变更才升 minor/major（罕见，需用户确认）。

**迭代 README**：在 `src/STranslate.Plugin/README.md` 顶部（首行介绍之后）新增一条版本记录，格式与历史一致：

```md
## vX.Y.Z - YYYY-MM-DD

- <对插件作者可见的 API 变更，中文，一条一行>
```

日期取发版当日；变更说明聚焦插件作者视角的契约变化，不写应用层 UI/功能；只追加到顶部，不改历史记录。

本步确定的新插件版本号供第 5 步 CHANGELOG 的 `## 插件开发` 区块引用。

### 4. 分类整理为 CHANGELOG 条目

按项目既有风格，统一用中文动词前缀，**不要**直接搬运 commit message：

- `添加：`——新功能、新插件、新配置项、新语种等用户能用上的新东西。
- `优化：`——改进既有行为、UI 调整、性能优化、逻辑完善、策略变更。
- `修复：`——仅限面向用户历史版本的回归修复（见上方过滤规则）。
- `插件开发：`——涉及 `STranslate.Plugin` 契约变更（新增接口方法、新增枚举、参数变更、版本号提升）时使用。可作为 `## 更新` 下的内联条目（`- 插件开发：...`），也可在改动较多时单独成 `## 插件开发` 区块。判断依据：是否改了 `src/STranslate.Plugin/` 下的公共 API 或提升其版本。版本号由第 3 步提升，区块内首条用「`STranslate.Plugin` 更新至 `1.0.x`」开头，再列具体 API 变更。

条目写法要点：

- 一条 commit 可能合并/拆分；以**用户视角的变更**为粒度，而非 commit 粒度。多个相关 commit 合成一条，描述最终效果。
- 用词贴近既有 CHANGELOG（参见下方"格式模板"），避免出现"重构了代码内部结构"这类用户不关心的实现细节。
- 涉及贡献者在 commit 中提及的（如 `@sean908`、`#711`），保留 `@用户名 #PR号` 的引用写法。
- 涉及具体插件/服务名时用全称或与历史一致的简称（如"腾讯翻译（Transmart）"、"DeepL 插件"）。

### 5. 写入 CHANGELOG.md

**整文件覆盖**写入，结构严格如下（保持区块顺序与标题层级）：

```md
## 更新

- 添加：...
- 添加：...
- 优化：...
- 修复：...

## 插件开发

- `STranslate.Plugin` 更新至 `1.0.x`
- ...

## 其他

- [插件市场](https://stranslate.zggsong.com/plugins/)
- [使用说明](https://stranslate.zggsong.com/docs/)
- [集成调用](https://stranslate.zggsong.com/docs/invoke/)
- [安装卸载](https://stranslate.zggsong.com/docs/install/)
- [FAQ](https://stranslate.zggsong.com/docs/faq/)

**完整更新日志:** [v2.0.7...v2.0.8](https://github.com/STranslate/STranslate/compare/v2.0.7...v2.0.8)
```

规则：

- `## 更新` 永远是第一行（文件开头无 `# 标题`、无空白）。条目内顺序：先 `添加：` 再 `优化：` 再 `修复：`，每类内按重要性/相关性排列。
- `## 插件开发` 区块**仅当本次确有插件 API / 插件版本变更时才写**；没有则整个区块省略，不要留空区块。
- `## 其他` 区块及其五条链接沿用发版前仓库 `CHANGELOG.md` 中的最新地址。若官网迁移，同步更新本模板。
- 最后一行 `**完整更新日志:**` 的链接：前一个 tag 和本次 tag 分别替换。仓庘认定为 `STranslate/STranslate`。
- 若某个分类本次没有任何条目，则该分类整段省略（例如纯 bugfix 版本可能只有 `修复：`）。

### 6. 提交并打 tag 发版

1. 提交（沿用历史 commit 风格 `chore: update version to vX.Y.Z`）。除 `CHANGELOG.md` 外，若第 3 步执行了插件版本/README 更新，则一并 `git add`：
   ```
   git add CHANGELOG.md src/STranslate.Plugin/STranslate.Plugin.csproj src/STranslate.Plugin/README.md
   git commit -m "chore: update version to vX.Y.Z"
   ```
   未改插件时只 `git add CHANGELOG.md`。
2. 创建**带注释的 tag**（与历史一致）：
   ```
   git tag -a vX.Y.Z -m "vX.Y.Z"
   ```
3. 推送触发自动打包发布（**外向操作，推送前向用户确认**）：
   ```
   git push origin main
   git push origin vX.Y.Z
   ```
   tag 推送后 `.github/workflows/dotnet.yml` 会自动构建并发布 GitHub Release，无需手动跑 `build.ps1`。

## 自检清单（写完 CHANGELOG、打 tag 前过一遍）

- [ ] 版本号匹配 `^v\d+\.\d+\.\d+$`（三段数字 + `v` 前缀）。若为默认递增则 = 上一 tag patch + 1；若用户指定了 minor/major 跳级，确认格式合规即可。
- [ ] 改动来源是 `<上一个tag>..HEAD`，已用 `--no-merges`。
- [ ] 已剔除版本号/changelog 维护提交。
- [ ] "修复："条目均为面向用户历史版本的回归，不含本周期新功能的自修 fix。
- [ ] 纯 chore / docs / 无用户可见效果的 refactor 已剔除。
- [ ] `## 更新` 在文件第一行；`## 其他` 五条链接与官网最新地址一致。
- [ ] 末尾 compare 链接的两个 tag 正确（前一个 tag ... 本次 tag）。
- [ ] `## 插件开发` 仅在确有插件 API/版本变更时存在。
- [ ] 若插件契约有变更：`<PackageVersion>` 已升 patch、`README.md` 顶部已新增对应版本记录，且三文件一并提交；若无变更则二者均未改动。
- [ ] tag 用 `git tag -a` 创建（带注释），非轻量 tag。

## 边界情况

- **当前 HEAD 已是某个 tag**（即没有新提交）：告诉用户自上个 tag 以来无改动，没有发版内容，不要硬凑条目。
- **跨多个特性分支合并**：以最终合入 main 的 squash/merge 结果为准，按用户可见变更去重汇总，不要把同一特性的多步 commit 拆成多条。
- **用户只想预览不想发版**：只整理 CHANGELOG 草稿展示给用户，不写文件、不打 tag、不推送，等用户确认。
- **CHANGELOG 与 release body 的关系**：`body_path: CHANGELOG.md` 意味着发版时 CHANGELOG 当前内容会原样成为 Release 说明。所以每次发版前必须把 CHANGELOG 重写为**本次**的内容，历史版本的说明在 GitHub Releases 页面各自留存，无需在文件里累积。
