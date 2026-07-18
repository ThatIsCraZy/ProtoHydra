using DataService.Core.Configuration;

namespace DataService.Core.Tests.Configuration;

public sealed class ProtocolSettingsTests
{
    [Fact]
    public void CreateDefaults_UsesStandardPortsAndAllInterfaces()
    {
        var settings = ProtocolSettings.CreateDefaults();

        Assert.Equal(21, settings.Ftp.Port);
        Assert.Equal(990, settings.Ftps.Port);
        Assert.Equal(69, settings.Tftp.Port);
        Assert.Equal(22, settings.Sftp.Port);
        Assert.Equal(22, settings.Scp.Port);
        Assert.Equal(80, settings.Http.Port);
        Assert.Equal(443, settings.Https.Port);
        Assert.All(
            [
                settings.Ftp,
                settings.Ftps,
                settings.Tftp,
                settings.Sftp,
                settings.Scp,
                settings.Http,
                settings.Https
            ],
            endpoint => Assert.Equal("0.0.0.0", endpoint.BindAddress));
        Assert.Equal("50000-50100", settings.FtpPassivePortRange);
    }

    [Fact]
    public void ApplicationDataPaths_UsesLocalAppDataProductDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, ApplicationDataPaths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(ApplicationDataPaths.ProductDirectoryName, ApplicationDataPaths.Root, StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(ApplicationDataPaths.Root, "Logs"),
            ApplicationDataPaths.GetDirectory("Logs"));
    }
}
