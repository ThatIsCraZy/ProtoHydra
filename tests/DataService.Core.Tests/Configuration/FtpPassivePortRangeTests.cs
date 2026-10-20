using DataService.Core.Configuration;

namespace DataService.Core.Tests.Configuration;

public sealed class FtpPassivePortRangeTests
{
    [Theory]
    [InlineData("50000-50100", 50000, 50100)]
    [InlineData("41000-41000", 41000, 41000)]
    [InlineData(" 1024 - 65535 ", 1024, 65535)]
    public void TryParse_AcceptsValidRanges(string value, int min, int max)
    {
        Assert.True(FtpPassivePortRange.TryParse(value, out var range));
        Assert.Equal(min, range.MinPort);
        Assert.Equal(max, range.MaxPort);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("50000")]
    [InlineData("50000-50100-50200")]
    [InlineData("50100-50000")]
    [InlineData("1023-50100")]
    [InlineData("50000-65536")]
    [InlineData("0-0")]
    [InlineData("-1-50100")]
    [InlineData("50000-999999999999999")]
    [InlineData("invalid-ports")]
    public void TryParse_RejectsInvalidRanges(string? value)
    {
        Assert.False(FtpPassivePortRange.TryParse(value, out _));
    }
}
