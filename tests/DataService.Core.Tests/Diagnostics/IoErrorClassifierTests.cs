using DataService.Core.Diagnostics;

namespace DataService.Core.Tests.Diagnostics;

public sealed class IoErrorClassifierTests
{
    private static IOException WithWin32Code(int code)
        => new("test", unchecked((int)0x80070000) | code);

    [Fact]
    public void Classify_UnauthorizedAccess_IsAccessDenied()
        => Assert.Equal(IoErrorCategory.AccessDenied, IoErrorClassifier.Classify(new UnauthorizedAccessException()));

    [Theory]
    [InlineData(21)]   // ERROR_NOT_READY (USB stick removed)
    [InlineData(1117)] // ERROR_IO_DEVICE
    [InlineData(1167)] // ERROR_DEVICE_NOT_CONNECTED
    [InlineData(1617)] // ERROR_DEVICE_REMOVED
    public void Classify_DeviceErrors_AreDeviceUnavailable(int code)
        => Assert.Equal(IoErrorCategory.DeviceUnavailable, IoErrorClassifier.Classify(WithWin32Code(code)));

    [Theory]
    [InlineData(112)] // ERROR_DISK_FULL
    [InlineData(39)]  // ERROR_HANDLE_DISK_FULL
    public void Classify_DiskFullErrors_AreDiskFull(int code)
        => Assert.Equal(IoErrorCategory.DiskFull, IoErrorClassifier.Classify(WithWin32Code(code)));

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public void Classify_SharingViolations_AreSharingViolation(int code)
        => Assert.Equal(IoErrorCategory.SharingViolation, IoErrorClassifier.Classify(WithWin32Code(code)));

    [Fact]
    public void Classify_FileNotFound_IsPathNotFound()
        => Assert.Equal(IoErrorCategory.PathNotFound, IoErrorClassifier.Classify(new FileNotFoundException()));

    [Fact]
    public void Classify_FileOffline_IsCloudFileUnavailable()
        => Assert.Equal(IoErrorCategory.CloudFileUnavailable, IoErrorClassifier.Classify(WithWin32Code(4350)));

    [Fact]
    public void Classify_UnknownIoErrorOnPlaceholderFile_IsCloudFileUnavailable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hydra-stub-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "stub");
        try
        {
            // The Offline attribute marks cloud placeholders (e.g. OneDrive files on demand).
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);

            var category = IoErrorClassifier.Classify(WithWin32Code(87), path);

            Assert.Equal(IoErrorCategory.CloudFileUnavailable, category);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    [Fact]
    public void Classify_UnknownIoErrorOnRegularFile_IsUnknown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hydra-regular-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "regular");
        try
        {
            Assert.Equal(IoErrorCategory.Unknown, IoErrorClassifier.Classify(WithWin32Code(87), path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildMessage_PrefixesFriendlyCause()
    {
        var message = IoErrorClassifier.BuildMessage(new UnauthorizedAccessException("raw"));

        Assert.StartsWith("Access denied", message);
        Assert.Contains("raw", message);
    }
}
