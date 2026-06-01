using CommunityToolkit.Mvvm.DependencyInjection;
using STranslate.Core;

namespace STranslate.Helpers;

public static class GlobalTriggerGuard
{
    private static readonly Lock _cacheLock = new();
    private static HashSet<string>? _excludedProcessCache;

    public static bool ShouldSkipGlobalTrigger()
    {
        try
        {
            var settings = Ioc.Default.GetRequiredService<Settings>();

            if (settings.DisableGlobalHotkeys)
                return true;

            if (settings.IgnoreHotkeysOnFullscreen &&
                Win32Helper.IsForegroundWindowFullscreen())
                return true;

            if (IsForegroundProcessExcluded(settings))
                return true;
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _excludedProcessCache = null;
        }
    }

    private static bool IsForegroundProcessExcluded(Settings settings)
    {
        var excludedProcesses = GetExcludedProcesses(settings);
        if (excludedProcesses.Count == 0)
            return false;

        var processName = Win32Helper.GetForegroundProcessName();
        return !string.IsNullOrWhiteSpace(processName) &&
            excludedProcesses.Contains(NormalizeProcessName(processName));
    }

    private static HashSet<string> GetExcludedProcesses(Settings settings)
    {
        lock (_cacheLock)
        {
            return _excludedProcessCache ??= settings.ExcludedGlobalTriggerProcesses
                .Select(NormalizeProcessName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeProcessName(string processName)
    {
        var normalized = processName.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".exe";
    }
}
