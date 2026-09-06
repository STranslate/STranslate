# 国际化 (i18n) 与语言贡献指南

本文档说明 STranslate 的多语言实现机制，并给出新增一种界面语言的完整步骤。如果你希望为项目贡献一种新语言，按文末 [贡献新语言 Checklist](#贡献新语言-checklist) 逐项操作即可。

## 支持的语言

| 语言代码 | 显示名称 | 备注 |
| :--: | :--: | :-- |
| `en` | English | 默认语言，内嵌进主程序，作为回退 |
| `zh-cn` | 中文 | |
| `zh-tw` | 中文（繁体） | |
| `ja` | 日本語 | |
| `ko` | 한국어 | |
| `tr` | Türkçe | |
| `ru` | Русский | |
| `uk` | Українська | |

语言代码沿用 [VS Code 命名习惯](https://code.visualstudio.com/docs/getstarted/locales)（小写、连字符分隔，如 `zh-cn`、`pt-br`）。

## 模块职责

- 主程序界面字符串：`STranslate/Languages/*.xaml`
- 国际化核心服务：`STranslate/Core/Internationalization.cs`
- 支持语言列表（唯一真相来源）：`Internationalization.cs` 末尾的 `AvailableLanguages` 静态类
- 语言对模型：`STranslate.Plugin/I18nPair.cs`
- 插件界面字符串 + 元数据翻译：各插件 `Languages/*.xaml` 与 `*.json`

## 关键入口

### 资源字典加载
- `STranslate/App.xaml:20`
  - `en.xaml` 通过 pack URI 内嵌进主程序二进制，作为默认/回退语言，**不**从文件系统重复加载。
- `STranslate/STranslate.csproj`
  - 用 `Languages\*.xaml` 通配符将其他语言文件以 `Content` + `PreserveNewest` 方式复制到输出目录。**新增语言文件无需改 csproj。**

### 国际化服务
- `STranslate/Core/Internationalization.cs`
  - `InitializeLanguage(string)`：启动时初始化语言。
  - `ChangeLanguage(string)`：运行时切换语言（热切换，无需重启）。
  - `OnLanguageChanged` 事件：切换完成后通知所有订阅者刷新 UI。
  - `GetTranslation(string key)`：代码内按 key 取翻译字符串。

### 语言切换 UI 入口
- `STranslate/Views/Pages/GeneralPage.xaml` 设置页「语言」下拉
  - `ItemsSource` 绑定 `GeneralViewModel.Languages`，`SelectedValue` 双向绑定 `Settings.Language`。
- `STranslate/Views/WelcomeSetupWindow.xaml` 首次启动向导语言选择

## 核心流程

### 1. 启动时初始化语言
1. `App.xaml.cs` 启动时调用 `Ioc.Default.GetRequiredService<Internationalization>().InitializeLanguage(Settings.Language)`。
2. `InitializeLanguage` 先 `InitSystemLanguageCode()`：用 `CultureInfo.CurrentCulture` 的 TwoLetter / ThreeLetter / Name 去 `AvailableLanguages` 列表匹配，匹配不到则回退 `en`。
3. 收集语言目录：`AddAppLanguageDirectory()`（主程序 `Languages/`）+ `AddPluginLanguageDirectories()`（各插件 `Languages/`）。
4. `LoadDefaultLanguage()`：总是先加载英文（内嵌），保证任何缺失 key 有回退值。
5. `ChangeLanguage(language)` 切换到目标语言。

### 2. 运行时切换语言
1. 用户在设置页选择语言 → `Settings.Language` 属性变更。
2. `Settings.cs` 的 `[ObservableProperty]` 自动触发 `Save()` 持久化配置。
3. `HandlePropertyChanged` → `ApplyLanguage()` → `i18n.ChangeLanguage(Language)`。
4. `ChangeLanguage(I18nPair)` 内部依次执行：
   - `RemoveOldLanguageFiles()`：从 `Application.Current.Resources.MergedDictionaries` 移除上次加载的非英文资源字典。
   - `LoadLanguage(language)`：遍历所有语言目录（主程序 + 各插件），把每个 `<code>.xaml` 加入 `MergedDictionaries`，并记入 `_oldResources`。**英文文件被排除**（已内嵌）。
   - `ChangeCultureInfo(languageCode)`：设置 `CurrentCulture` / `CurrentUICulture` 及主线程文化。
   - `UpdatePluginMetadataTranslations(languageCode)`：读取各插件 `<code>.json`，更新 `plugin.Name` / `plugin.Description`。
   - 触发 `OnLanguageChanged` 事件。
5. 所有 `DynamicResource` 绑定自动响应 `MergedDictionaries` 变化刷新 UI。

### 3. 缺失翻译的回退
- `LanguageFile()` 在某语言目录找不到 `<code>.xaml` 时，回退到该目录的 `en.xaml`（记录错误日志）。因此**插件不补新语言不会报错，只是该插件界面回退英文**。
- `GetTranslation(key)` 找不到 key 时返回 `"No Translation for key {key}"`。

### 4. OnLanguageChanged 订阅者
切换语言后需要额外刷新的 UI 状态（已由框架自动处理，贡献语言时无需关心）：
- `MainWindowViewModel`：刷新托盘「管理员」提示等。
- `DataProvider`：刷新所有枚举下拉项（`LangEnum`、`LanguageDetectorType`、`ElementTheme` 等）的本地化标签。
- `SearchViewModelBase`：重建设置搜索建议列表。

## 关键数据结构

### `I18nPair`（`STranslate.Plugin/I18nPair.cs`）
```csharp
public class I18nPair(string code, string display)
{
    public string LanguageCode { get; set; } = code;   // 如 "zh-cn"
    public string Display { get; set; } = display;     // 如 "中文"，以该语言母语显示
}
```

### `AvailableLanguages`（`Internationalization.cs:390-423`）
**支持语言的唯一集中定义处**，无枚举。新增语言必须在此修改三处。

### `Constant.SystemLanguageCode`（`STranslate/Core/Constant.cs:18`）
值为 `"system"`，表示跟随系统语言。设置页下拉第一项即此值。

### 主程序语言文件格式（`STranslate/Languages/zh-cn.xaml`）
```xml
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:sys="clr-namespace:System;assembly=mscorlib">
    <sys:String x:Key="General_Language">语言</sys:String>
    ...
</ResourceDictionary>
```
- key 命名约定：`<模块>_<字段>`，如 `General_Language`、`Main_Nav_Settings`。
- UI 中通过 `{DynamicResource General_Language}` 绑定（必须用 `DynamicResource` 才能热切换）。

### 插件语言文件格式
每个插件目录下 `Languages/` 同时包含两类文件，**语言代码必须与主程序一致**：

**`<code>.xaml`** — 插件配置面板 UI 字符串，key 约定为 `STranslate_Plugin_<类型>_<名称>_<字段>`：
```xml
<sys:String x:Key="STranslate_Plugin_Translate_Baidu_AppID">APP ID</sys:String>
<sys:String x:Key="STranslate_Plugin_Translate_Baidu_AppKey">App Key</sys:String>
```

**`<code>.json`** — 插件元数据（Name / Description）翻译：
```json
{
  "Name": "百度翻译",
  "Description": "用于 STranslate 的百度翻译插件"
}
```

## 贡献新语言 Checklist

以新增法语（`fr`）为例。

### 第 1 步：主程序语言文件
1. 复制 `STranslate/Languages/en.xaml` 为 `STranslate/Languages/fr.xaml`。
2. 翻译所有 `<sys:String>` 的值，**保留所有 `x:Key` 不变**。
3. 确保 key 数量与 `en.xaml` 完全一致（可用 diff 工具核对）。

### 第 2 步：注册到 `AvailableLanguages`
编辑 `STranslate/Core/Internationalization.cs:390-423`，**三处都要改**：

```csharp
internal static class AvailableLanguages
{
    public static I18nPair English = new("en", "English");
    public static I18nPair Chinese = new("zh-cn", "中文");
    public static I18nPair Chinese_TW = new("zh-tw", "中文（繁体）");
    public static I18nPair Japanese = new("ja", "日本語");
    public static I18nPair Korean = new("ko", "한국어");
    public static I18nPair French = new("fr", "Français");   // ← 新增

    public static List<I18nPair> GetAvailableLanguages()
    {
        List<I18nPair> languages =
        [
            English,
            Chinese,
            Chinese_TW,
            Japanese,
            Korean,
            French,                                                // ← 新增
        ];
        return languages;
    }

    public static string GetSystemTranslation(string languageCode)
    {
        return languageCode switch
        {
            "en" => "System",
            "zh-cn" => "系统",
            "zh-tw" => "系統",
            "ja" => "システム",
            "ko" => "시스템",
            "fr" => "Système",                                    // ← 新增，易遗漏！
            _ => "System",
        };
    }
}
```

> ⚠️ **`GetSystemTranslation` 的 switch 分支最容易被遗漏。** 漏掉的话，语言下拉里「跟随系统」选项在新语言下会显示英文 "System"。

### 第 3 步：插件语言文件
为**每个**官方插件补齐法语文件（当前共 21 个插件目录，见 `src/Plugins/`）：
- 对每个插件的 `Languages/` 目录，复制 `en.xaml` → `fr.xaml` 并翻译，复制 `en.json` → `fr.json` 并翻译。
- 不补不会报错，但对应插件界面会回退英文，体验割裂。

### 第 4 步：验证
1. 编译运行，进入 设置 → 通用 → 语言，确认下拉出现新语言且「跟随系统」选项显示为对应翻译。
2. 切换到新语言，逐页检查是否有未翻译项（会显示为英文或 `No Translation for key ...`）。
3. 检查各插件配置面板，确认插件 UI 与元数据已翻译。
4. 重启程序，确认语言选择已持久化。
5. 将系统语言设为新语言对应区域，选择「跟随系统」，确认能正确匹配到新语言。

## 约定与注意事项

### 命名约定
- **语言代码**：小写，连字符分隔，如 `zh-cn`、`pt-br`、`fr`。沿用 [VS Code locale](https://code.visualstudio.com/docs/getstarted/locales) 习惯。
- **主程序 key**：`<模块>_<字段>`，如 `General_Language`。
- **插件 key**：`STranslate_Plugin_<类型>_<名称>_<字段>`，如 `STranslate_Plugin_Translate_Baidu_AppID`。
- **资源 key 一旦发布就不要改名**，否则会破坏既有 `Settings` 等绑定与外部插件兼容性。

### 绑定方式
- UI 中**必须用 `{DynamicResource key}`** 而非 `{StaticResource}`，否则无法热切换。
- 代码内取翻译用 `Ioc.Default.GetRequiredService<Internationalization>().GetTranslation("key")`。

### 不需要改的地方
- ❌ `STranslate.csproj`：`Languages\*.xaml` 通配符自动包含新文件。
- ❌ `App.xaml`：`en.xaml` 内嵌逻辑不变。
- ❌ 设置页 / 欢迎向导 ComboBox：绑定到 `Languages` 列表，自动跟上。
- ❌ `Settings.Language` 属性：保存 / 触发逻辑是通用的。
- ❌ README / 文档：未枚举支持语言列表，无需同步。
- ❌ 无需重启提示：语言切换是热切换。

### 已知硬编码（待修复）
- 已修复（均走 i18n，对应日志保持中文）：设置页搜索框的「无结果」提示 `NoResultsFound`；网络页代理测试的四条结果提示 `ProxyTesting` / `ProxyTestSuccess` / `ProxyTestFailed` / `ProxyTestError`；服务添加对话框的标题 `AddTranslationService` / `AddOcrService` / `AddTtsService` / `AddVocabularyService` / `AddService`；词典服务不支持替换的提示 `DictionaryServiceNotSupportReplace`；插件版本校验的三条提示 `PluginVersionParseFailed` / `PluginVersionTooOld` / `PluginUpgradeAvailable`；外部调用服务启动失败的提示 `ExternalCallStartFailed`。
- 仍待处理（本 PR 未涉及，需先定方案）：
  - 抛给用户的异常文案基本都是中文。`ex.Message` 会被直接拼进 Snackbar 或对话框，共 11 处：`ViewModels/MainWindowViewModel.cs:1218`、`:1369`，`ViewModels/ImageTranslateWindowViewModel.cs:281`、`:387`、`:440`，`ViewModels/OcrWindowViewModel.cs:200`、`:322`、`:363`，`ViewModels/Pages/HistoryViewModel.cs:288`，`ViewModels/Pages/PluginViewModel.cs:349`、`:605`。因此 `Helpers/LanguageDetector.cs`、`Core/AudioReaderFactory.cs` 以及各插件里的 `throw new Exception("请求结果为空")` 之类都会原样呈现给用户；改动面涉及大量调用点和插件语言文件，宜另开 PR。
  - `Helpers/HistoryCsvHelper.cs` 的历史记录 CSV 导出：表头（`:37` 起）以及写进单元格的 `回译:`（`:128`）、`词条:`（`:141`）、`释义:`（`:145`）、`例句:`（`:152`）和分隔符 `；`（`:163`）均为中文。它属于导出文件格式而非界面文案，跟随界面语言会影响既有文件的解析，需要先定方案。

## 开发参考：在代码 / XAML 中使用国际化

### XAML
```xml
<!-- 热切换必须用 DynamicResource -->
<TextBlock Text="{DynamicResource General_Language}" />
<Button Content="{DynamicResource Main_Nav_Settings}" />
```

### C# 代码
```csharp
// 通过 DI 获取 i18n 服务
var i18n = Ioc.Default.GetRequiredService<Internationalization>();
var text = i18n.GetTranslation("General_Language");
```

### 订阅语言变更
若你的 ViewModel / Service 需要在语言切换时刷新本地化数据：
```csharp
public MyViewModel(Internationalization i18n)
{
    i18n.OnLanguageChanged += RefreshLocalizableData;
}

private void RefreshLocalizableData()
{
    // 重新加载依赖翻译的列表、标签等
}
```
