using STranslate.Core;

namespace STranslate.Tests;

public class MainWindowPlacementNormalizerTests
{
    [Fact]
    public void NormalizeForDisplayChangeReturnsOriginalPlacementWhenPreviousSnapshotMissing()
    {
        var current = new MainWindowDisplaySnapshot(0, 0, 1536, 824, 120, 120);
        var placement = new MainWindowPlacement(320, 180, 470);

        var actual = MainWindowPlacementNormalizer.NormalizeForDisplayChange(
            default,
            current,
            placement,
            actualHeight: 260,
            minWidth: 400,
            edgePadding: 8);

        Assert.Equal(placement, actual);
    }

    [Fact]
    public void NormalizeForDisplayChangeScalesWidthByWorkAreaDipRatio()
    {
        var previous = new MainWindowDisplaySnapshot(0, 0, 2194, 1200, 168, 168);
        var current = new MainWindowDisplaySnapshot(0, 0, 1536, 824, 120, 120);
        var placement = new MainWindowPlacement(548.5, 300, 800);

        var actual = MainWindowPlacementNormalizer.NormalizeForDisplayChange(
            previous,
            current,
            placement,
            actualHeight: 260,
            minWidth: 400,
            edgePadding: 8);

        Assert.Equal(560.073, actual.Width, 3);
        Assert.Equal(384, actual.Left, 3);
        Assert.Equal(206, actual.Top, 3);
    }

    [Fact]
    public void NormalizeForDisplayChangeClampsRightAndBottomEdgesIntoWorkArea()
    {
        var previous = new MainWindowDisplaySnapshot(0, 0, 1000, 700, 96, 96);
        var current = new MainWindowDisplaySnapshot(0, 0, 1000, 700, 96, 96);
        var placement = new MainWindowPlacement(950, 650, 300);

        var actual = MainWindowPlacementNormalizer.NormalizeForDisplayChange(
            previous,
            current,
            placement,
            actualHeight: 100,
            minWidth: 100,
            edgePadding: 8);

        Assert.Equal(300, actual.Width);
        Assert.Equal(692, actual.Left);
        Assert.Equal(592, actual.Top);
    }

    [Fact]
    public void ClampToWorkAreaLimitsWidthToAvailableSpace()
    {
        var current = new MainWindowDisplaySnapshot(0, 0, 500, 700, 96, 96);
        var placement = new MainWindowPlacement(20, 20, 700);

        var actual = MainWindowPlacementNormalizer.ClampToWorkArea(
            current,
            placement,
            actualHeight: 200,
            minWidth: 400,
            edgePadding: 8);

        Assert.Equal(484, actual.Width);
        Assert.Equal(8, actual.Left);
        Assert.Equal(20, actual.Top);
    }

    [Fact]
    public void ClampToWorkAreaKeepsWidthAtLeastMinWidth()
    {
        var current = new MainWindowDisplaySnapshot(0, 0, 1000, 700, 96, 96);
        var placement = new MainWindowPlacement(20, 20, 120);

        var actual = MainWindowPlacementNormalizer.ClampToWorkArea(
            current,
            placement,
            actualHeight: 200,
            minWidth: 400,
            edgePadding: 8);

        Assert.Equal(400, actual.Width);
        Assert.Equal(20, actual.Left);
        Assert.Equal(20, actual.Top);
    }

    [Fact]
    public void HasDisplayChangedIncludesDpiChanges()
    {
        var previous = new MainWindowDisplaySnapshot(0, 0, 1000, 700, 168, 168);
        var current = new MainWindowDisplaySnapshot(0, 0, 1000, 700, 120, 120);

        Assert.True(MainWindowPlacementNormalizer.HasDisplayChanged(previous, current));
    }
}
