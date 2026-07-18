using DataService.Infrastructure.Certificates;

namespace DataService.Core.Tests.Infrastructure;

public sealed class CertificateManagerTests : IDisposable
{
    private readonly string _certificateDirectory;

    public CertificateManagerTests()
    {
        _certificateDirectory = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task GetOrCreateAsync_PersistsAndReusesCertificate()
    {
        var manager = new CertificateManager();
        var settings = new CertificateSettings(_certificateDirectory);

        using var first = await manager.GetOrCreateAsync(CertificatePurpose.Https, settings, CancellationToken.None);
        using var second = await manager.GetOrCreateAsync(CertificatePurpose.Https, settings, CancellationToken.None);

        Assert.True(first.HasPrivateKey);
        Assert.Equal(first.Thumbprint, second.Thumbprint);
        Assert.True(File.Exists(Path.Combine(_certificateDirectory, "https.pfx")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_certificateDirectory))
        {
            Directory.Delete(_certificateDirectory, recursive: true);
        }
    }
}

