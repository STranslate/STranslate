using System.Globalization;
using System.Windows;
using STranslate.Converters;

namespace STranslate.Tests;

public class StringToVisibilityConverterTests
{
    [Theory]
    [InlineData(null, Visibility.Collapsed)]
    [InlineData("", Visibility.Collapsed)]
    [InlineData("error", Visibility.Visible)]
    [InlineData("   ", Visibility.Visible)]
    public void StringToVisibilityConverter_ConvertsCorrectly(string? input, Visibility expected)
    {
        var converter = new StringToVisibilityConverter();
        var result = converter.Convert(input!, typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, Visibility.Visible)]
    [InlineData("", Visibility.Visible)]
    [InlineData("error", Visibility.Collapsed)]
    [InlineData("   ", Visibility.Collapsed)]
    public void StringToVisibilityInverseConverter_ConvertsCorrectly(string? input, Visibility expected)
    {
        var converter = new StringToVisibilityInverseConverter();
        var result = converter.Convert(input!, typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }
}
