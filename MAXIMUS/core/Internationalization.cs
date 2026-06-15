using Microsoft.Extensions.Logging;
using STranslate.Plugin;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace STranslate.Core;

public class Internationalization(ILogger<Internationalization> logger, PluginManager pluginManager)
{
    private const string Folder = "Languages";
    private const string DefaultLanguageCode = "en";
    private const string DefaultFile = "en.xaml";
    private const string Extension = ".xaml";
    private const string MetaExtension = ".json";
    private readonly List<string> _languageDirectories = [];
    private readonly List<ResourceDictionary> _oldResources = [];
    public event Action? OnLanguageChanged;
    private I18nPair _lastLanguagePair = AvailableLanguages.English;

    private string SystemLanguageCode { get; set; } = null!;

    #region Initialization

    /// <summary>
    /// Initialize language. Will change app language and plugin language based on settings.
    /// </summary>
    public void InitializeLanguage(string languageCode)
    {
        InitSystemLanguageCode();
        if (languageCode != Constant.SystemLanguageCode)
        {
            ChangeCultureInfo(languageCode);
        }
        // Get actual language
        if (languageCode == Constant.SystemLanguageCode)
        {
            languageCode = SystemLanguageCode;
        }

        // Get language by language code and change language
        var language = GetLanguageByLanguageCode(languageCode);

        // Add App language directory
        AddAppLanguageDirectory();

        // Add plugin language directories first so that we can load language files from plugins
        AddPluginLanguageDirectories();

        // Load default language resources
        LoadDefaultLanguage();

        // Change language
        ChangeLanguage(language);
    }

    /// <summary>
    /// Initialize the system language code based on the current culture.
    /// </summary>
    private void InitSystemLanguageCode()
    {
        var availableLanguages = AvailableLanguages.GetAvailableLanguages();

        // Retrieve the language identifiers for the current culture.
        // ChangeLanguage method overrides the CultureInfo.CurrentCulture, so this needs to
        // be called at startup in order to get the correct lang code of system. 
        var currentCulture = CultureInfo.CurrentCulture;
        var twoLetterCode = currentCulture.TwoLetterISOLanguageName;
        var threeLetterCode = currentCulture.ThreeLetterISOLanguageName;
        var fullName = currentCulture.Name;

        // Try to find a match in the available languages list
        foreach (var language in availableLanguages)
        {
            var languageCode = language.LanguageCode;

            if (string.Equals(languageCode, twoLetterCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(languageCode, threeLetterCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(languageCode, fullName, StringComparison.OrdinalIgnoreCase))
            {
                SystemLanguageCode = languageCode;
            }
        }

        // Default to English if no match is found
        if (string.IsNullOrEmpty(SystemLanguageCode))
            SystemLanguageCode = DefaultLanguageCode;
    }

    private void AddAppLanguageDirectory()
    {
        // Check if App language directory exists
        var directory = Path.Combine(Constant.ProgramDirectory, Folder);
        if (!Directory.Exists(directory))
        {
            logger.LogError($"App language directory can't be found <{directory}>");
            return;
        }

        _languageDirectories.Add(directory);
    }

    private void AddPluginLanguageDirectories()
    {
        foreach (var pluginsDir in DataLocation.PluginDirectories)
        {
            if (!Directory.Exists(pluginsDir)) continue;

            // Enumerate all top directories in the plugin directory
            foreach (var dir in Directory.GetDirectories(pluginsDir))
            {
                // Check if the directory contains a language folder
                var pluginLanguageDir = Path.Combine(dir, Folder);
                if (!Directory.Exists(pluginLanguageDir)) continue;

                // Check if the language directory contains default language file since it will be checked later
                _languageDirectories.Add(pluginLanguageDir);
            }
        }
    }

    private void LoadDefaultLanguage()
    {
        // Removes language files loaded before any plugins were loaded.
        // Prevents the language Flow started in from overwriting English if the user switches back to English
        RemoveOldLanguageFiles();
        LoadLanguage(AvailableLanguages.English);
        _oldResources.Clear();
    }

    public void LoadInstalledPluginLanguages(string pluginDirectory)
    {
        var pluginLanguageDir = Path.Combine(pluginDirectory, Folder);
        if (Directory.Exists(pluginLanguageDir))
        {
            _languageDirectories.Add(pluginLanguageDir);
        }

        // 更新语言资源字典
        var dicts = Application.Current.Resources.MergedDictionaries;
        var filename = $"{_lastLanguagePair.LanguageCode}{Extension}";
        var file = LanguageFile(pluginLanguageDir, filename);
        if (string.IsNullOrEmpty(file))
            return;

        var r = new ResourceDictionary
        {
            Source = new Uri(file, UriKind.Absolute)
        };
        dicts.Add(r);
        _oldResources.Add(r);

        // 更新插件元数据语言
        var jsonPath = Path.Combine(pluginDirectory, Folder, $"{_lastLanguagePair.LanguageCode}{MetaExtension}");

        if (!File.Exists(jsonPath))
            return;

        var plugin = pluginManager.AllPluginMetaDatas
            .FirstOrDefault(p => p.PluginDirectory == pluginDirectory);
        if (plugin == null)
            return;
        try
        {
            var jsonContent = File.ReadAllText(jsonPath);
            var meta = JsonSerializer.Deserialize<PluginMetaData>(jsonContent)
                ?? throw new InvalidOperationException($"Failed to deserialize {jsonPath}");
            plugin.Name = string.IsNullOrWhiteSpace(meta.Name) ? plugin.Name : meta.Name;
            plugin.Description = string.IsNullOrWhiteSpace(meta.Description) ? plugin.Description : meta.Description;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to update metadata for <{plugin.Name}> from {jsonPath}");
        }
    }

    #endregion

    #region Change Language

    /// <summary>
    /// Change language during runtime. Will change app language and plugin language & save settings.
    /// </summary>
    /// <param name="languageCode"></param>
    public void ChangeLanguage(string languageCode)
    {
        languageCode = languageCode.NonNull();

        // Get actual language if language code is system
        if (languageCode == Constant.SystemLanguageCode)
        {
            languageCode = SystemLanguageCode;
        }

        // Get language by language code and change language
        var language = GetLanguageByLanguageCode(languageCode);

        // Change language
        ChangeLanguage(language);
    }

    private I18nPair GetLanguageByLanguageCode(string languageCode)
    {
        var language = AvailableLanguages.GetAvailableLanguages().
            FirstOrDefault(o => o.LanguageCode.Equals(languageCode, StringComparison.OrdinalIgnoreCase));
        if (language == null)
        {
            logger.LogError($"Language code can't be found <{languageCode}>");
            _lastLanguagePair = AvailableLanguages.English;
            return AvailableLanguages.English;
        }
        else
        {
            _lastLanguagePair = language;
            return language;
        }
    }

    private void ChangeLanguage(I18nPair language)
    {
        // Remove old language files and load language
        RemoveOldLanguageFiles();
        if (language != AvailableLanguages.English)
        {
            LoadLanguage(language);
        }

        // Change culture info
        ChangeCultureInfo(language.LanguageCode);

        // Raise event for plugins after culture is set
        UpdatePluginMetadataTranslations(language.LanguageCode);

        OnLanguageChanged?.Invoke();
    }

    private void ChangeCultureInfo(string languageCode)
    {
        // Culture of main thread
        // Use CreateSpecificCulture to preserve possible user-override settings in Windows, if Flow's language culture is the same as Windows's
        CultureInfo currentCulture;
        try
        {
            currentCulture = CultureInfo.CreateSpecificCulture(languageCode);
        }
        catch (CultureNotFoundException)
        {
            currentCulture = CultureInfo.CreateSpecificCulture(SystemLanguageCode);
        }
        CultureInfo.CurrentCulture = currentCulture;
        CultureInfo.CurrentUICulture = currentCulture;
        var thread = Thread.CurrentThread;
        thread.CurrentCulture = currentCulture;
        thread.CurrentUICulture = currentCulture;
    }

    #endregion

    #region Language Resources Management

    private void RemoveOldLanguageFiles()
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var tmp = new List<ResourceDictionary>(_oldResources);
        foreach (var r in tmp)
        {
            dicts.Remove(r);
            _oldResources.Remove(r);
        }
    }

    private void LoadLanguage(I18nPair language)
    {
        var appEnglishFile = Path.Combine(Constant.ProgramDirectory, Folder, DefaultFile);
        var dicts = Application.Current.Resources.MergedDictionaries;
        var filename = $"{language.LanguageCode}{Extension}";
        var files = _languageDirectories
            .Select(d => LanguageFile(d, filename))
            // Exclude Flow's English language file since it's built into the binary, and there's no need to load
            // it again from the file system.
            .Where(f => !string.IsNullOrEmpty(f) && f != appEnglishFile)
            .ToArray();

        if (files.Length > 0)
        {
            foreach (var f in files)
            {
                var r = new ResourceDictionary
                {
                    Source = new Uri(f, UriKind.Absolute)
                };
                dicts.Add(r);
                _oldResources.Add(r);
            }
        }
    }

    private string LanguageFile(string folder, string language)
    {
        if (Directory.Exists(folder))
        {
            var path = Path.Combine(folder, language);
            if (File.Exists(path))
            {
                return path;
            }
            else
            {
                logger.LogError($"Language path can't be found <{path}>");
                var english = Path.Combine(folder, DefaultFile);
                if (File.Exists(english))
                {
                    return english;
                }
                else
                {
                    logger.LogError($"Default English Language path can't be found <{path}>");
                    return string.Empty;
                }
            }
        }
        else
        {
            return string.Empty;
        }
    }

    #endregion

    #region Available Languages

    public List<I18nPair> LoadAvailableLanguages()
    {
        var list = AvailableLanguages.GetAvailableLanguages();
        list.Insert(0, new I18nPair(Constant.SystemLanguageCode, AvailableLanguages.GetSystemTranslation(SystemLanguageCode)));
        return list;
    }

    #endregion

    #region Get Translations

    public string GetTranslation(string key)
    {
        var translation = Application.Current.TryFindResource(key);
        if (translation is string str)
        {
            return str;
        }
        else
        {
            logger.LogError($"No Translation for key {key}");
            return $"No Translation for key {key}";
        }
    }

    #endregion

    #region Update Metadata

    public void UpdatePluginMetadataTranslations(string languageCode)
    {
        foreach (var plugin in pluginManager.AllPluginMetaDatas)
        {
            var jsonPath = Path.Combine(plugin.PluginDirectory, Folder, $"{languageCode}{MetaExtension}");

            if (!File.Exists(jsonPath))
                continue;

            try
            {
                var jsonContent = File.ReadAllText(jsonPath);
                var meta = JsonSerializer.Deserialize<PluginMetaData>(jsonContent)
                    ?? throw new InvalidOperationException($"Failed to deserialize {jsonPath}");
                plugin.Name = string.IsNullOrWhiteSpace(meta.Name) ? plugin.Name : meta.Name;
                plugin.Description = string.IsNullOrWhiteSpace(meta.Description) ? plugin.Description : meta.Description;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to update metadata for <{plugin.Name}> from {jsonPath}");
            }
        }
    }


    #endregion
}

internal static class AvailableLanguages
{
    public static I18nPair English = new("en", "English");
    public static I18nPair Chinese = new("zh-cn", "中文");
    public static I18nPair Chinese_TW = new("zh-tw", "中文（繁体）");
    public static I18nPair Japanese = new("ja", "日本語");
    public static I18nPair Korean = new("ko", "한국어");
    public static I18nPair Turkish = new("tr", "Türkçe");

    public static List<I18nPair> GetAvailableLanguages()
    {
        List<I18nPair> languages =
        [
            English,
            Chinese,
            Chinese_TW,
            Japanese,
            Korean,
            Turkish,
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
            "tr" => "Sistem",
            _ => "System",
        };
    }
}
