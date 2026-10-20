using DataService.Core.FileSystem;

namespace DataService.Core.Tests.FileSystem;

public sealed class RootPathResolverTests : IDisposable
{
    private readonly string _rootPath;

    public RootPathResolverTests()
    {
        _rootPath = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    [Theory]
    [InlineData("firmware/image.iso", "firmware/image.iso")]
    [InlineData("/firmware/image.iso", "firmware/image.iso")]
    [InlineData("folder%20name/file.txt", "folder name/file.txt")]
    [InlineData(".", "")]
    [InlineData("/.", "")]
    [InlineData("/./", "")]
    [InlineData("./firmware/./image.iso", "firmware/image.iso")]
    [InlineData("/firmware/./image.iso", "firmware/image.iso")]
    [InlineData("/%2e/firmware/image.iso", "firmware/image.iso")]
    public void ResolveClientPath_ReturnsPathInsideRoot(string clientPath, string expectedRelativePath)
    {
        var resolved = new RootPathResolver(_rootPath).ResolveClientPath(clientPath);

        Assert.Equal(expectedRelativePath, resolved.RelativePath);
        Assert.StartsWith(_rootPath, resolved.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("%2e%2e/secret.txt")]
    [InlineData("%252e%252e%252fsecret.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("//server/share/file.txt")]
    [InlineData("folder\\file.txt")]
    [InlineData("folder/file.txt%00")]
    [InlineData("/./../secret.txt")]
    [InlineData("./folder/../../secret.txt")]
    [InlineData("/./%2e%2e/secret.txt")]
    [InlineData("/./%252e%252e%252fsecret.txt")]
    public void ResolveClientPath_BlocksRootEscapeInputs(string clientPath)
    {
        var resolver = new RootPathResolver(_rootPath);

        Assert.Throws<PathResolutionException>(() => resolver.ResolveClientPath(clientPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}

