using STranslate.Core;
using System.Globalization;
using System.Text;

namespace STranslate.Tests;

public class TextReplacementDiffTests
{
    [Fact]
    public void ServiceRequestsShareHighlightRangesButKeepTheirOwnReplacementValues()
    {
        var changes = TextReplacementDiff.CreateForRequests("aa KEEP bb", [new("xx KEEP yy", "A"), new("zz KEEP yy", "B")]);
        Assert.Equal(2, changes.Count);
        Assert.Equal(["xx", "zz"], changes[0].Variants!.Select(variant => variant.Replacement));
        Assert.Equal(["yy", "yy"], changes[1].Variants!.Select(variant => variant.Replacement));
        Assert.Equal(0, changes[0].Start);
        Assert.Equal(8, changes[1].Start);
    }

    [Fact]
    public void OverlappingServiceEditsAreProjectedOntoTheSameOriginalRange()
    {
        var change = Assert.Single(TextReplacementDiff.CreateForRequests("abcdef", [new("Xdef", "A"), new("aYef", "B")]));
        Assert.Equal(0, change.Start);
        Assert.Equal(4, change.Length);
        Assert.Equal(["Xd", "aY"], change.Variants!.Select(variant => variant.Replacement));
    }

    [Fact]
    public void InsertionsDistinguishAnUnchangedServiceAndMissingRequestMetadata()
    {
        var change = Assert.Single(TextReplacementDiff.CreateForRequests("ab", [new("a+b", "A"), new("ab", "B"), new(null, "C")]));
        Assert.Equal(1, change.Start);
        Assert.Equal(0, change.Length);
        Assert.Equal(new TextReplacementVariant("A", "+", true), change.Variants![0]);
        Assert.Equal(new TextReplacementVariant("B", "", false), change.Variants[1]);
        Assert.Equal(new TextReplacementVariant("C", null, false), change.Variants[2]);
        Assert.False(change.IsDeletion);
    }

    [Fact]
    public void OnlyADeletionSharedByAllServicesUsesDeletionStyling()
    {
        var mixed = Assert.Single(TextReplacementDiff.CreateForRequests("aa", [new("", "A"), new("aa", "B")]));
        Assert.False(mixed.IsDeletion);
        var deleted = Assert.Single(TextReplacementDiff.CreateForRequests("aa", [new("", "A"), new("", "B")]));
        Assert.True(deleted.IsDeletion);
        Assert.Empty(TextReplacementDiff.CreateForRequests("aa", [new(null, "A"), new(null, "B")]));
    }

    [Theory]
    [InlineData("keep secret today", "keep public today")]
    [InlineData("one secret and another secret", "one public and another public")]
    [InlineData("remove this", "remove ")]
    [InlineData("", "inserted")]
    [InlineData("unchanged", "unchanged")]
    [InlineData("ab", "a++b")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("😀 e\u0301 👨‍👩‍👧", "😁 e 👩‍👩‍👧")]
    public void ChangesReconstructTheSentTextWithoutSplittingTextElements(string original, string request)
    {
        var changes = TextReplacementDiff.Create(original, request);
        Assert.Equal(request, Reconstruct(original, changes));
        var boundaries = StringInfo.ParseCombiningCharacters(original).Append(original.Length).ToHashSet();
        foreach (var change in changes)
        {
            Assert.Contains(change.Start, boundaries);
            Assert.Contains(change.Start + change.Length, boundaries);
        }
    }

    [Fact]
    public void SeparateEditsKeepUnchangedTextBetweenThemUnmarked()
    {
        var changes = TextReplacementDiff.Create("a KEEP z", "b KEEP y");
        Assert.Equal(2, changes.Count);
        Assert.Equal(new TextReplacementChange(0, 1, "b"), changes[0]);
        Assert.Equal(new TextReplacementChange(7, 1, "y"), changes[1]);
    }

    [Fact]
    public void LargeUnrelatedTextsUseBoundedFallbackAndKeepCommonEdges()
    {
        var original = "start " + new string('a', 2000) + " end";
        var request = "start " + new string('b', 2000) + " end";
        var change = Assert.Single(TextReplacementDiff.Create(original, request));
        Assert.Equal(6, change.Start);
        Assert.Equal(2000, change.Length);
        Assert.Equal(request, Reconstruct(original, [change]));
    }

    [Fact]
    public void RepeatedAndRandomEditsAlwaysReconstructTheActualRequest()
    {
        var random = new Random(109);
        for (var sample = 0; sample < 200; sample++)
        {
            var original = new string(Enumerable.Range(0, random.Next(30)).Select(_ => (char)('a' + random.Next(4))).ToArray());
            var request = new string(Enumerable.Range(0, random.Next(30)).Select(_ => (char)('a' + random.Next(4))).ToArray());
            Assert.Equal(request, Reconstruct(original, TextReplacementDiff.Create(original, request)));
        }
    }

    private static string Reconstruct(string original, IReadOnlyList<TextReplacementChange> changes)
    {
        var builder = new StringBuilder();
        var last = 0;
        foreach (var change in changes)
        {
            Assert.InRange(change.Start, last, original.Length);
            builder.Append(original.AsSpan(last, change.Start - last)).Append(change.Replacement);
            last = change.Start + change.Length;
        }
        return builder.Append(original.AsSpan(last)).ToString();
    }
}
