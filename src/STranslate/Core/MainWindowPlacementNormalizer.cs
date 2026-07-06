namespace STranslate.Core;

internal readonly record struct MainWindowDisplaySnapshot(
    double WorkAreaLeft,
    double WorkAreaTop,
    double WorkAreaWidth,
    double WorkAreaHeight,
    double DpiX,
    double DpiY)
{
    internal bool IsInitialized =>
        IsPositiveFinite(WorkAreaWidth) &&
        IsPositiveFinite(WorkAreaHeight) &&
        IsPositiveFinite(DpiX) &&
        IsPositiveFinite(DpiY);

    internal double WorkAreaRight => WorkAreaLeft + WorkAreaWidth;

    internal double WorkAreaBottom => WorkAreaTop + WorkAreaHeight;

    private static bool IsPositiveFinite(double value) =>
        value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
}

internal readonly record struct MainWindowPlacement(double Left, double Top, double Width);

internal static class MainWindowPlacementNormalizer
{
    internal static bool HasDisplayChanged(
        MainWindowDisplaySnapshot previous,
        MainWindowDisplaySnapshot current)
    {
        if (!previous.IsInitialized || !current.IsInitialized)
            return false;

        return !NearlyEqual(previous.WorkAreaLeft, current.WorkAreaLeft) ||
               !NearlyEqual(previous.WorkAreaTop, current.WorkAreaTop) ||
               !NearlyEqual(previous.WorkAreaWidth, current.WorkAreaWidth) ||
               !NearlyEqual(previous.WorkAreaHeight, current.WorkAreaHeight) ||
               !NearlyEqual(previous.DpiX, current.DpiX) ||
               !NearlyEqual(previous.DpiY, current.DpiY);
    }

    internal static MainWindowPlacement NormalizeForDisplayChange(
        MainWindowDisplaySnapshot previous,
        MainWindowDisplaySnapshot current,
        MainWindowPlacement placement,
        double actualHeight,
        double minWidth,
        double edgePadding)
    {
        if (!previous.IsInitialized || !current.IsInitialized)
            return placement;

        var widthScale = current.WorkAreaWidth / previous.WorkAreaWidth;
        var heightScale = current.WorkAreaHeight / previous.WorkAreaHeight;
        var normalized = new MainWindowPlacement(
            current.WorkAreaLeft + (placement.Left - previous.WorkAreaLeft) * widthScale,
            current.WorkAreaTop + (placement.Top - previous.WorkAreaTop) * heightScale,
            placement.Width * widthScale);

        return ClampToWorkArea(current, normalized, actualHeight, minWidth, edgePadding);
    }

    internal static MainWindowPlacement ClampToWorkArea(
        MainWindowDisplaySnapshot current,
        MainWindowPlacement placement,
        double actualHeight,
        double minWidth,
        double edgePadding)
    {
        if (!current.IsInitialized)
            return placement;

        edgePadding = Math.Max(0, edgePadding);
        minWidth = Math.Max(1, minWidth);

        var maxWidth = Math.Max(minWidth, current.WorkAreaWidth - edgePadding * 2);
        var width = Math.Clamp(placement.Width > 0 ? placement.Width : minWidth, minWidth, maxWidth);

        var minLeft = current.WorkAreaLeft + edgePadding;
        var maxLeft = current.WorkAreaRight - width - edgePadding;
        if (maxLeft < minLeft)
            maxLeft = minLeft;

        var height = Math.Max(1, actualHeight);
        var minTop = current.WorkAreaTop + edgePadding;
        var maxTop = current.WorkAreaBottom - height - edgePadding;
        if (maxTop < minTop)
            maxTop = minTop;

        return new MainWindowPlacement(
            Math.Clamp(placement.Left, minLeft, maxLeft),
            Math.Clamp(placement.Top, minTop, maxTop),
            width);
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) < 0.01;
}
