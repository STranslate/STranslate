using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Win32;
using STranslate.Controls;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;

namespace STranslate.ViewModels.Pages;

public partial class PluginViewModel : ObservableObject
{
    private readonly PluginService _pluginService;
    private readonly Internationalization _i18n;
    private readonly IHttpService _httpService;

    public DataProvider DataProvider { get; }

    private readonly ISnackbar _snackbar;
    private readonly Settings _settings;
    private readonly CollectionViewSource _pluginCollectionView;
    public ICollectionView PluginCollectionView => _pluginCollectionView.View;

    #region 视图切换

    [ObservableProperty]
    public partial bool IsMarketView { get; set; }

    [ObservableProperty]
    public partial bool IsMarketInitialized { get; set; }

    /// <summary>
    /// 切换市场视图
    /// </summary>
    [RelayCommand]
    private async Task ToggleMarketViewAsync()
    {
        IsMarketView = !IsMarketView;

        // 首次切换到市场视图时延迟加载，避免初始化卡顿
        if (IsMarketView && !IsMarketInitialized)
        {
            IsMarketInitialized = true;
            await LoadPluginsAsync();
        }
    }

    #endregion

    [ObservableProperty] public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty] public partial string MarketFilterText { get; set; } = string.Empty;

    /// <summary>
    /// 所有插件数量
    /// </summary>
    public int TotalPluginCount => _pluginService.PluginMetaDatas.Count;

    /// <summary>
    /// 翻译插件数量（包含翻译和词典插件）
    /// </summary>
    public int TranslatePluginCount => _pluginService.PluginMetaDatas
        .Where(x => typeof(ITranslatePlugin).IsAssignableFrom(x.PluginType) || typeof(IDictionaryPlugin).IsAssignableFrom(x.PluginType))
        .Count();

    /// <summary>
    /// OCR插件数量
    /// </summary>
    public int OcrPluginCount => _pluginService.PluginMetaDatas
        .Where(x => typeof(IOcrPlugin).IsAssignableFrom(x.PluginType))
        .Count();

    /// <summary>
    /// TTS插件数量
    /// </summary>
    public int TtsPluginCount => _pluginService.PluginMetaDatas
        .Where(x => typeof(ITtsPlugin).IsAssignableFrom(x.PluginType))
        .Count();

    /// <summary>
    /// 词汇表插件数量
    /// </summary>
    public int VocabularyPluginCount => _pluginService.PluginMetaDatas
        .Where(x => typeof(IVocabularyPlugin).IsAssignableFrom(x.PluginType))
        .Count();

    #region 插件市场相关字段

    private readonly CollectionViewSource _pluginsCollectionView;
    public ICollectionView PluginsView => _pluginsCollectionView.View;

    public ObservableCollection<PluginMarketInfo> Plugins { get; } = [];

    private const int PluginLoadMaxConcurrency = 6;
    private const int PluginUiBatchSize = 12;

    #endregion

    #region CDN URL 构建

    /// <summary>
    /// 构建插件市场CDN URL
    /// </summary>
    private string BuildCdnUrl(string author, string repo, string branch, string path)
    {
        var cdnSource = _settings.PluginMarketCdnSource;
        string baseUrl = cdnSource switch
        {
            PluginMarketCdnSourceType.JsDelivr => "https://fastly.jsdelivr.net/gh/{author}/{repo}@{branch}/{path}",
            PluginMarketCdnSourceType.GitHubRaw => "https://raw.githubusercontent.com/{author}/{repo}/{branch}/{path}",
            PluginMarketCdnSourceType.Custom => _settings.CustomPluginMarketCdnUrl,
            _ => "https://fastly.jsdelivr.net/gh/{author}/{repo}@{branch}/{path}"
        };

        return baseUrl
            .Replace("{author}", author)
            .Replace("{repo}", repo)
            .Replace("{branch}", branch)
            .Replace("{path}", path);
    }

    /// <summary>
    /// 构建插件下载URL - 始终使用GitHub Releases默认地址
    /// </summary>
    private static string BuildDownloadUrl(string author, string packageName, string version)
    {
        // 下载统一使用GitHub默认地址，确保可靠性
        return $"https://github.com/{author}/{packageName}/releases/download/v{version}/{packageName}.spkg";
    }

    /// <summary>
    /// 获取插件列表JSON的URL
    /// </summary>
    private string GetPluginsJsonUrl()
    {
        var cdnSource = _settings.PluginMarketCdnSource;
        return cdnSource switch
        {
            PluginMarketCdnSourceType.JsDelivr => "https://fastly.jsdelivr.net/gh/STranslate/STranslate-doc@main/vitepress/plugins.json",
            PluginMarketCdnSourceType.GitHubRaw => "https://raw.githubusercontent.com/STranslate/STranslate-doc/main/vitepress/plugins.json",
            PluginMarketCdnSourceType.Custom => _settings.CustomPluginMarketCdnUrl
                .Replace("{author}", "STranslate")
                .Replace("{repo}", "STranslate-doc")
                .Replace("{branch}", "main")
                .Replace("{path}", "vitepress/plugins.json"),
            _ => "https://fastly.jsdelivr.net/gh/STranslate/STranslate-doc@main/vitepress/plugins.json"
        };
    }

    #endregion

    #region 插件市场属性

    [ObservableProperty]
    public partial PluginType SelectedPluginType { get; set; } = PluginType.All;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? LoadingStatus { get; set; }

    [ObservableProperty]
    public partial int MarketTotalPluginCount { get; set; }

    [ObservableProperty]
    public partial int MarketTranslatePluginCount { get; set; }

    [ObservableProperty]
    public partial int MarketOcrPluginCount { get; set; }

    [ObservableProperty]
    public partial int MarketTtsPluginCount { get; set; }

    [ObservableProperty]
    public partial int MarketVocabularyPluginCount { get; set; }

    #endregion

    public PluginViewModel(
        PluginService pluginService,
        Internationalization i18n,
        DataProvider dataProvider,
        ISnackbar snackbar,
        Settings settings,
        IHttpService httpService
        )
    {
        _pluginService = pluginService;
        _i18n = i18n;
        DataProvider = dataProvider;
        _snackbar = snackbar;
        _settings = settings;
        _httpService = httpService;

        // 已安装插件集合视图
        _pluginCollectionView = new()
        {
            Source = _pluginService.PluginMetaDatas
        };
        _pluginCollectionView.Filter += OnPluginFilter;

        // 监听插件集合变化，更新计数
        _pluginService.PluginMetaDatas.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(TotalPluginCount));
            OnPropertyChanged(nameof(TranslatePluginCount));
            OnPropertyChanged(nameof(OcrPluginCount));
            OnPropertyChanged(nameof(TtsPluginCount));
            OnPropertyChanged(nameof(VocabularyPluginCount));

            // 同时更新市场插件状态
            UpdatePluginStatus();
        };

        // 插件市场集合视图
        _pluginsCollectionView = new CollectionViewSource
        {
            Source = Plugins
        };
        _pluginsCollectionView.Filter += OnPluginsFilter;
    }

    [ObservableProperty]
    public partial PluginType PluginType { get; set; } = PluginType.All;

    private void OnPluginFilter(object _, FilterEventArgs e)
    {
        if (e.Item is not PluginMetaData plugin)
        {
            e.Accepted = false;
            return;
        }

        e.Accepted = MatchesPluginType(plugin.PluginType) && MatchesText(FilterText, plugin.Name, plugin.Author, plugin.Description);
    }

    private void OnPluginsFilter(object _, FilterEventArgs e)
    {
        if (e.Item is not PluginMarketInfo plugin)
        {
            e.Accepted = false;
            return;
        }

        var typeMatch = SelectedPluginType switch
        {
            PluginType.Translate => plugin.Type == "Translate",
            PluginType.Ocr => plugin.Type == "Ocr",
            PluginType.Tts => plugin.Type == "Tts",
            PluginType.Vocabulary => plugin.Type == "Vocabulary",
            _ => true
        };

        e.Accepted = typeMatch && MatchesText(MarketFilterText, plugin.Name, plugin.Author, plugin.Description);
    }

    private bool MatchesPluginType(Type? pluginType)
    {
        return PluginType switch
        {
            PluginType.Translate => typeof(ITranslatePlugin).IsAssignableFrom(pluginType) || typeof(IDictionaryPlugin).IsAssignableFrom(pluginType),
            PluginType.Ocr => typeof(IOcrPlugin).IsAssignableFrom(pluginType),
            PluginType.Tts => typeof(ITtsPlugin).IsAssignableFrom(pluginType),
            PluginType.Vocabulary => typeof(IVocabularyPlugin).IsAssignableFrom(pluginType),
            _ => true
        };
    }

    private static bool MatchesText(string filter, string name, string author, string description)
    {
        if (string.IsNullOrEmpty(filter))
            return true;

        return name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || author.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || description.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    #region 插件市场 - 加载插件列表

    [RelayCommand]
    private async Task LoadPluginsAsync()
    {
        IsLoading = true;
        LoadingStatus = _i18n.GetTranslation("PluginMarketLoading");

        try
        {
            Plugins.Clear();

            // 1. 获取插件标识符列表
            var pluginIds = await _httpService.GetAsync<List<string>>(GetPluginsJsonUrl());

            if (pluginIds == null || pluginIds.Count == 0)
            {
                LoadingStatus = _i18n.GetTranslation("PluginMarketNoPlugins");
                return;
            }

            // 2. 有界并发获取每个插件的详细信息，避免瞬时并发过高导致 UI 抖动
            using var semaphore = new SemaphoreSlim(PluginLoadMaxConcurrency);
            var branchCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tasks = pluginIds.Select(async pluginId =>
            {
                await semaphore.WaitAsync();
                try
                {
                    return await GetPluginInfoAsync(pluginId, branchCache);
                }
                finally
                {
                    semaphore.Release();
                }
            });
            var plugins = await Task.WhenAll(tasks);

            // 3. 分批添加到集合，降低主线程一次性通知压力
            var availablePlugins = plugins.Where(p => p != null).Cast<PluginMarketInfo>().ToList();
            for (int i = 0; i < availablePlugins.Count; i += PluginUiBatchSize)
            {
                var batch = availablePlugins.Skip(i).Take(PluginUiBatchSize);
                foreach (var plugin in batch)
                {
                    Plugins.Add(plugin);
                }

                await Task.Yield();
            }

            // 4. 更新安装状态
            UpdatePluginStatus();

            LoadingStatus = null;
        }
        catch (Exception ex)
        {
            LoadingStatus = _i18n.GetTranslation("PluginMarketLoadError");
            _snackbar.ShowError($"{_i18n.GetTranslation("PluginMarketLoadError")}: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<PluginMarketInfo?> GetPluginInfoAsync(string pluginId,
        ConcurrentDictionary<string, string> branchCache)
    {
        try
        {
            // 解析插件ID格式: Author/STranslate.Plugin.Type.Name 或 STranslate.Plugin.Type.Name
            var parts = pluginId.Split('/');
            var packageName = parts.Length > 1 ? parts[1] : parts[0];
            var author = parts.Length > 1 ? parts[0] : "STranslate";

            // 解析包名: STranslate.Plugin.Type.Name
            var nameParts = packageName.Split('.');
            if (nameParts.Length < 4) return null;

            var type = nameParts[2]; // Translate, Ocr, Tts, Vocabulary

            // 尝试获取 plugin.json（先 main 分支，失败则回退到 master）
            // 并在单次加载内复用分支探测结果
            var (pluginInfo, branch) = await TryGetPluginInfoAsync(author, packageName, branchCache);
            if (pluginInfo == null) return null;

            // 尝试获取多语言资源（zh-cn.json）
            var (name, description) = await TryGetLocalizedInfoAsync(author, packageName,
                branch, pluginInfo.Name, pluginInfo.Description);

            // 构建下载 URL
            var downloadUrl = BuildDownloadUrl(author, packageName, pluginInfo.Version ?? "1.0.0");

            return new PluginMarketInfo
            {
                PluginId = pluginInfo.PluginID ?? string.Empty,
                Name = name ?? packageName,
                Author = pluginInfo.Author ?? author,
                Type = type,
                Version = pluginInfo.Version ?? "1.0.0",
                Description = description ?? string.Empty,
                Website = pluginInfo.Website ?? $"https://github.com/{author}/{packageName}",
                IconUrl = BuildCdnUrl(author, packageName, branch, $"{packageName}/icon.png"),
                DownloadUrl = downloadUrl,
                PackageName = packageName
            };
        }
        catch
        {
            // 单个插件加载失败不影响其他插件
            return null;
        }
    }

    private async Task<(PluginInfo? PluginInfo, string Branch)> TryGetPluginInfoAsync(
        string author,
        string packageName,
        ConcurrentDictionary<string, string> branchCache)
    {
        var cacheKey = $"{author}/{packageName}";

        // 优先尝试缓存分支
        if (branchCache.TryGetValue(cacheKey, out var cachedBranch))
        {
            var cachedUrl = BuildCdnUrl(author, packageName, cachedBranch, $"{packageName}/plugin.json");
            try
            {
                var cachedInfo = await _httpService.GetAsync<PluginInfo>(cachedUrl);
                if (cachedInfo != null)
                    return (cachedInfo, cachedBranch);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                branchCache.TryRemove(cacheKey, out _);
            }
            catch
            {
                // 缓存分支请求失败时继续走标准探测流程
            }
        }

        // 先尝试 main 分支
        var mainUrl = BuildCdnUrl(author, packageName, "main", $"{packageName}/plugin.json");
        try
        {
            var pluginInfo = await _httpService.GetAsync<PluginInfo>(mainUrl);
            if (pluginInfo != null)
            {
                branchCache[cacheKey] = "main";
                return (pluginInfo, "main");
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 404 则回退到 master 分支
        }
        catch
        {
            return (null, "main");
        }

        var masterUrl = BuildCdnUrl(author, packageName, "master", $"{packageName}/plugin.json");
        try
        {
            var pluginInfo = await _httpService.GetAsync<PluginInfo>(masterUrl);
            if (pluginInfo != null)
            {
                branchCache[cacheKey] = "master";
                return (pluginInfo, "master");
            }
        }
        catch
        {
            return (null, "master");
        }

        return (null, "main");
    }

    private async Task<(string Name, string Description)> TryGetLocalizedInfoAsync(
        string author,
        string packageName,
        string branch,
        string defaultName,
        string defaultDescription)
    {
        var langUrl = BuildCdnUrl(author, packageName, branch, $"{packageName}/Languages/zh-cn.json");

        try
        {
            var langInfo = await _httpService.GetAsync<PluginLanguageInfo>(langUrl);
            if (langInfo != null)
            {
                return (
                    !string.IsNullOrEmpty(langInfo.Name) ? langInfo.Name : defaultName,
                    !string.IsNullOrEmpty(langInfo.Description) ? langInfo.Description : defaultDescription
                );
            }
        }
        catch
        {
            // 多语言资源获取失败，使用默认值
        }

        return (defaultName, defaultDescription);
    }

    private void UpdatePluginStatus()
    {
        // 创建本地插件字典
        var localPlugins = _pluginService.PluginMetaDatas
            .ToDictionary(p => p.PluginID, p => p);

        foreach (var marketPlugin in Plugins)
        {
            if (localPlugins.TryGetValue(marketPlugin.PluginId, out var localPlugin))
            {
                if (!marketPlugin.IsInstalled)
                    marketPlugin.IsInstalled = true;
                marketPlugin.InstalledVersion = localPlugin.Version;

                // 比较版本
                var comparison = CompareVersions(localPlugin.Version, marketPlugin.Version);
                var canUpgrade = comparison < 0;
                if (marketPlugin.CanUpgrade != canUpgrade)
                    marketPlugin.CanUpgrade = canUpgrade;
            }
            else
            {
                if (marketPlugin.IsInstalled)
                    marketPlugin.IsInstalled = false;
                if (marketPlugin.CanUpgrade)
                    marketPlugin.CanUpgrade = false;
                marketPlugin.InstalledVersion = null;
            }
        }

        // 更新分类计数
        UpdatePluginCounts();
    }

    private void UpdatePluginCounts()
    {
        MarketTotalPluginCount = Plugins.Count;
        MarketTranslatePluginCount = Plugins.Count(p => p.Type == "Translate");
        MarketOcrPluginCount = Plugins.Count(p => p.Type == "Ocr");
        MarketTtsPluginCount = Plugins.Count(p => p.Type == "Tts");
        MarketVocabularyPluginCount = Plugins.Count(p => p.Type == "Vocabulary");
    }

    #endregion

    #region 插件市场 - 下载和安装

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadPluginAsync(PluginMarketInfo plugin)
    {
        if (plugin.IsDownloading || plugin.ActionStatus == PluginActionStatus.Installed) return;

        // 创建新的取消令牌源
        plugin.CancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = plugin.CancellationTokenSource.Token;

        plugin.IsDownloading = true;
        plugin.DownloadProgress = 0;
        plugin.DownloadStatus = "0%";

        try
        {
            var tempPath = Path.GetTempPath();
            var fileName = $"{plugin.PackageName}.zip";

            // 创建进度报告
            var progress = new Progress<DownloadProgress>(p =>
            {
                plugin.DownloadProgress = p.Percentage;
                plugin.DownloadStatus = $"{p.Percentage:F0}% ({p.Speed / 1024:F0} KB/s)";
            });

            // 应用下载代理
            var downloadUrl = _settings.PluginDownloadProxy switch
            {
                PluginDownloadProxyType.GhProxyMirror => $"https://mirror.ghproxy.com/{plugin.DownloadUrl}",
                PluginDownloadProxyType.GhProxyNet => $"https://ghproxy.net/{plugin.DownloadUrl}",
                PluginDownloadProxyType.Custom => $"{_settings.CustomDownloadProxyUrl.TrimEnd('/')}/{plugin.DownloadUrl}",
                _ => plugin.DownloadUrl // GitHub 直连
            };

            // 下载文件（支持取消）
            var downloadedPath = await _httpService.DownloadFileAsync(
                downloadUrl, tempPath, fileName, progress: progress, cancellationToken: cancellationToken);

            // 检查是否已取消
            cancellationToken.ThrowIfCancellationRequested();

            // 转换为 .spkg 临时文件
            var spkgPath = Path.ChangeExtension(downloadedPath, ".spkg");
            if (File.Exists(spkgPath))
                File.Delete(spkgPath);
            File.Move(downloadedPath, spkgPath);

            // 使用现有 PluginManager 安装
            var result = _pluginService.InstallPlugin(spkgPath);

            // 处理安装结果
            await HandleInstallResultAsync(plugin, result, spkgPath);
        }
        catch (OperationCanceledException)
        {
            _snackbar.ShowWarning($"{_i18n.GetTranslation("PluginDownloadCancelled")}");
        }
        catch (Exception ex)
        {
            _snackbar.ShowError($"{_i18n.GetTranslation("PluginInstallFailed")}: {ex.Message}");
        }
        finally
        {
            plugin.IsDownloading = false;
            plugin.DownloadStatus = null;
            plugin.CancellationTokenSource?.Dispose();
            plugin.CancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private void CancelDownload(PluginMarketInfo plugin)
    {
        if (plugin.CancellationTokenSource != null && !plugin.CancellationTokenSource.IsCancellationRequested)
        {
            plugin.CancellationTokenSource.Cancel();
        }
    }

    private async Task HandleInstallResultAsync(PluginMarketInfo plugin,
        PluginInstallResult result, string spkgPath)
    {
        if (result.RequiredUpgrade && result.ExistingPlugin != null)
        {
            // 询问是否升级
            var dialogResult = await new ContentDialog
            {
                Title = _i18n.GetTranslation("PluginUpgrade"),
                Content = string.Format(_i18n.GetTranslation("PluginUpgradeConfirm"),
                    result.ExistingPlugin.Name,
                    result.ExistingPlugin.Version,
                    result.NewPlugin?.Version),
                PrimaryButtonText = _i18n.GetTranslation("Confirm"),
                CloseButtonText = _i18n.GetTranslation("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            }.ShowAsync();

            if (dialogResult == ContentDialogResult.Primary)
            {
                // 执行升级
                if (_pluginService.UpgradePlugin(result.ExistingPlugin, spkgPath))
                {
                    _snackbar.ShowSuccess(_i18n.GetTranslation("PluginInstallSuccess"));
                    // 刷新所有插件的安装状态
                    UpdatePluginStatus();
                    // 提示重启
                    await PromptRestartAsync(plugin);
                }
                else
                {
                    _snackbar.ShowError(_i18n.GetTranslation("PluginUpgradeFailed"));
                }
            }
        }
        else if (!result.Succeeded)
        {
            await new ContentDialog
            {
                Title = _i18n.GetTranslation("PluginInstallFailed"),
                CloseButtonText = _i18n.GetTranslation("Ok"),
                DefaultButton = ContentDialogButton.Close,
                Content = result.Message
            }.ShowAsync();
        }
        else
        {
            // 安装成功（新安装不需要重启）
            _snackbar.ShowSuccess(_i18n.GetTranslation("PluginInstallSuccess"));
            // 刷新所有插件的安装状态
            UpdatePluginStatus();
        }

        // 清理临时文件
        try
        {
            if (File.Exists(spkgPath))
                File.Delete(spkgPath);
        }
        catch { }
    }

    private async Task PromptRestartAsync(PluginMarketInfo plugin)
    {
        var restartResult = await new ContentDialog
        {
            Title = _i18n.GetTranslation("Prompt"),
            Content = _i18n.GetTranslation("PluginUpgradeSuccess"),
            PrimaryButtonText = _i18n.GetTranslation("RestartNow"),
            CloseButtonText = _i18n.GetTranslation("RestartLater"),
            DefaultButton = ContentDialogButton.Primary,
        }.ShowAsync();

        if (restartResult == ContentDialogResult.Primary)
        {
            UACHelper.Restart(_settings.StartMode);
            App.Current.Shutdown();
        }
        else
        {
            // 用户选择稍后重启，标记为待重启状态
            plugin.IsPendingRestart = true;
        }
    }

    #endregion

    #region 辅助方法

    private static int CompareVersions(string? localVersion, string? marketVersion)
    {
        if (string.IsNullOrEmpty(localVersion)) return -1;
        if (string.IsNullOrEmpty(marketVersion)) return 1;

        // 尝试解析为 System.Version
        if (Version.TryParse(localVersion, out var v1) && Version.TryParse(marketVersion, out var v2))
            return v1.CompareTo(v2);

        // 回退：手动比较版本号各部分
        var parts1 = ParseVersionParts(localVersion);
        var parts2 = ParseVersionParts(marketVersion);

        for (int i = 0; i < Math.Max(parts1.Count(), parts2.Count()); i++)
        {
            int result = parts1.ElementAtOrDefault(i).CompareTo(parts2.ElementAtOrDefault(i));
            if (result != 0) return result;
        }

        return 0;
    }

    private static IEnumerable<int> ParseVersionParts(string version)
    {
        var cleanVersion = new string(version.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return cleanVersion.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var num) ? num : 0);
    }

    #endregion

    partial void OnPluginTypeChanged(PluginType value) => _pluginCollectionView.View?.Refresh();

    partial void OnFilterTextChanged(string value) => _pluginCollectionView.View?.Refresh();

    partial void OnMarketFilterTextChanged(string value) => _pluginsCollectionView.View?.Refresh();

    partial void OnSelectedPluginTypeChanged(PluginType value) => _pluginsCollectionView.View?.Refresh();

    [RelayCommand]
    private async Task AddPluginAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = _i18n.GetTranslation("SelectPluginFile"),
            Filter = "Spkg File (*.spkg)|*.spkg",
            Multiselect = true,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog() != true) return;

        await InstallPluginsAsync(dialog.FileNames);
    }

    [RelayCommand]
    private async Task InstallPluginsAsync(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        var spkgFiles = files.Where(f => f.EndsWith(".spkg", StringComparison.OrdinalIgnoreCase)).ToList();
        if (spkgFiles.Count == 0)
        {
            _snackbar.ShowError(_i18n.GetTranslation("NoValidPluginFile"));
            return;
        }

        await InstallPluginsAsync(spkgFiles);
    }

    private async Task InstallPluginsAsync(IEnumerable<string> files)
    {
        var needRestart = false;
        foreach (var spkgPluginFilePath in files)
        {
            var installResult = _pluginService.InstallPlugin(spkgPluginFilePath);

            if (installResult.RequiredUpgrade && installResult.ExistingPlugin != null)
            {
                // 插件已存在，询问是否升级
                var result = await new ContentDialog
                {
                    Title = _i18n.GetTranslation("PluginUpgrade"),
                    Content = string.Format(_i18n.GetTranslation("PluginUpgradeConfirm"), installResult.ExistingPlugin.Name, installResult.ExistingPlugin.Version, installResult.NewPlugin?.Version),
                    PrimaryButtonText = _i18n.GetTranslation("Confirm"),
                    CloseButtonText = _i18n.GetTranslation("Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                }.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    // 执行升级
                    if (_pluginService.UpgradePlugin(installResult.ExistingPlugin, spkgPluginFilePath))
                    {
                        needRestart = true;
                        _snackbar.ShowSuccess(_i18n.GetTranslation("PluginInstallSuccess"));
                    }
                    else
                    {
                        _snackbar.ShowError(_i18n.GetTranslation("PluginUpgradeFailed"));
                    }
                }
            }
            else if (!installResult.Succeeded)
            {
                await new ContentDialog
                {
                    Title = _i18n.GetTranslation("PluginInstallFailed"),
                    CloseButtonText = _i18n.GetTranslation("Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = installResult.Message
                }.ShowAsync();
            }
            else
            {
                _snackbar.ShowSuccess(_i18n.GetTranslation("PluginInstallSuccess"));
            }
        }

        if (needRestart)
        {
            var restartResult = await new ContentDialog
            {
                Title = _i18n.GetTranslation("Prompt"),
                Content = _i18n.GetTranslation("PluginUpgradeSuccess"),
                PrimaryButtonText = _i18n.GetTranslation("RestartNow"),
                CloseButtonText = _i18n.GetTranslation("RestartLater"),
                DefaultButton = ContentDialogButton.Primary,
            }.ShowAsync();

            if (restartResult == ContentDialogResult.Primary)
            {
                UACHelper.Restart(_settings.StartMode);
                App.Current.Shutdown();
            }
        }
    }

    [RelayCommand]
    private void OpenPluginDirectory(PluginMetaData plugin)
    {
        var directory = plugin.PluginDirectory;
        if (!string.IsNullOrEmpty(directory))
            Process.Start("explorer.exe", directory);
    }

    [RelayCommand]
    private async Task DeletePluginAsync(PluginMetaData plugin)
    {
        if (await new ContentDialog
        {
            Title = _i18n.GetTranslation("Prompt"),
            CloseButtonText = _i18n.GetTranslation("Cancel"),
            PrimaryButtonText = _i18n.GetTranslation("Confirm"),
            DefaultButton = ContentDialogButton.Primary,
            Content = string.Format(_i18n.GetTranslation("PluginDeleteConfirm"), plugin.Author, plugin.Version, plugin.Name),
        }.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (!_pluginService.UninstallPlugin(plugin))
        {
            _ = new ContentDialog
            {
                Title = _i18n.GetTranslation("Prompt"),
                CloseButtonText = _i18n.GetTranslation("Ok"),
                DefaultButton = ContentDialogButton.Close,
                Content = _i18n.GetTranslation("PluginDeleteFailed")
            }.ShowAsync().ConfigureAwait(false);

            return;
        }

        if (await new ContentDialog
        {
            Title = _i18n.GetTranslation("Prompt"),
            CloseButtonText = _i18n.GetTranslation("Cancel"),
            PrimaryButtonText = _i18n.GetTranslation("Confirm"),
            DefaultButton = ContentDialogButton.Primary,
            Content = _i18n.GetTranslation("PluginDeleteForRestart"),
        }.ShowAsync() == ContentDialogResult.Primary)
        {
            UACHelper.Restart(_settings.StartMode);
            App.Current.Shutdown();
        }
    }

    [RelayCommand]
    private void OpenOfficialLink(string url)
        => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    /// <summary>
    /// 打开插件市场高级配置对话框
    /// </summary>
    [RelayCommand]
    private async Task OpenMarketSettingsAsync()
    {
        var dialog = new PluginMarketSettingsDialog(_settings, DataProvider);
        var result = await dialog.ShowAsync();

        // 如果 CDN 设置发生变化且当前在市场视图，提示用户刷新以应用新设置
        if (dialog.IsCdnSettingsChanged && IsMarketView && IsMarketInitialized)
        {
            var refreshResult = await new ContentDialog
            {
                Title = _i18n.GetTranslation("Prompt"),
                Content = _i18n.GetTranslation("PluginMarketSettingsChanged"),
                PrimaryButtonText = _i18n.GetTranslation("PluginMarketRefresh"),
                CloseButtonText = _i18n.GetTranslation("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            }.ShowAsync();

            if (refreshResult == ContentDialogResult.Primary)
            {
                await LoadPluginsAsync();
            }
        }
    }
}

/// <summary>
/// 插件市场信息模型
/// </summary>
public partial class PluginMarketInfo : ObservableObject
{
    /// <summary>
    /// 插件唯一ID
    /// </summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>
    /// 插件名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 插件作者
    /// </summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// 插件类型 (Translate/Ocr/Tts/Vocabulary)
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// 市场版本号
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// 插件描述
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 项目网址
    /// </summary>
    public string Website { get; set; } = string.Empty;

    /// <summary>
    /// 图标URL
    /// </summary>
    public string IconUrl { get; set; } = string.Empty;

    /// <summary>
    /// 下载URL
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// 包名 (STranslate.Plugin.Type.Name)
    /// </summary>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>
    /// 是否已安装
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionStatus))]
    public partial bool IsInstalled { get; set; }

    /// <summary>
    /// 是否下载中
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionStatus))]
    public partial bool IsDownloading { get; set; }

    /// <summary>
    /// 下载进度 0-100
    /// </summary>
    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    /// <summary>
    /// 下载状态文本
    /// </summary>
    [ObservableProperty]
    public partial string? DownloadStatus { get; set; }

    /// <summary>
    /// 取消令牌源（用于取消下载）
    /// </summary>
    public CancellationTokenSource? CancellationTokenSource { get; set; }

    /// <summary>
    /// 是否可以升级
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionStatus))]
    public partial bool CanUpgrade { get; set; }

    /// <summary>
    /// 当前安装的版本（用于比较）
    /// </summary>
    public string? InstalledVersion { get; set; }

    /// <summary>
    /// 是否待重启（升级后选择延后重启）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionStatus))]
    public partial bool IsPendingRestart { get; set; }

    /// <summary>
    /// 获取操作状态
    /// </summary>
    public PluginActionStatus ActionStatus
    {
        get
        {
            if (IsDownloading)
                return PluginActionStatus.Downloading;
            if (IsPendingRestart)
                return PluginActionStatus.PendingRestart;
            if (!IsInstalled)
                return PluginActionStatus.Download;
            if (CanUpgrade)
                return PluginActionStatus.Upgrade;
            return PluginActionStatus.Installed;
        }
    }
}

/// <summary>
/// 插件操作状态
/// </summary>
public enum PluginActionStatus
{
    /// <summary>
    /// 可下载
    /// </summary>
    Download,

    /// <summary>
    /// 已安装最新版本
    /// </summary>
    Installed,

    /// <summary>
    /// 可升级
    /// </summary>
    Upgrade,

    /// <summary>
    /// 下载中
    /// </summary>
    Downloading,

    /// <summary>
    /// 待重启（升级后选择延后重启）
    /// </summary>
    PendingRestart
}

/// <summary>
/// 插件基本信息（对应 plugin.json）
/// </summary>
public class PluginInfo
{
    public string PluginID { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string ExecuteFileName { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
}

/// <summary>
/// 插件多语言信息（对应 Languages/zh-cn.json）
/// </summary>
public class PluginLanguageInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public enum PluginType
{
    All,
    Translate,
    Ocr,
    Tts,
    Vocabulary,
}
