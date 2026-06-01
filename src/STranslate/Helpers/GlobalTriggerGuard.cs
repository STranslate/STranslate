using CommunityToolkit.Mvvm.DependencyInjection;
using STranslate.Core;

namespace STranslate.Helpers;

public static class GlobalTriggerGuard
{
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
    }

    private static bool IsForegroundProcessExcluded(Settings settings)
    {
        if (settings.ExcludedGlobalTriggerProcesses.Count == 0)
            return false;

        var processName = Win32Helper.GetForegroundProcessName();
        return !string.IsNullOrWhiteSpace(processName) &&
            settings.ExcludedGlobalTriggerProcesses
                .Select(NormalizeProcessName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Contains(NormalizeProcessName(processName), StringComparer.OrdinalIgnoreCase);
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
