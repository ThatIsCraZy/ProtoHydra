using DataService.Core.Configuration;

namespace DataService.App;

public static class AppConfigurationDefaults
{
    public static AppConfiguration Create()
    {
        var dataRoot = ApplicationDataPaths.GetDirectory("Root");
        Directory.CreateDirectory(dataRoot);

        return new AppConfiguration
        {
            RootPath = dataRoot,
            Protocols = new ProtocolSettings
            {
                Http = new ProtocolEndpointSettings { BindAddress = "0.0.0.0", Port = 80 },
                Https = new ProtocolEndpointSettings { BindAddress = "0.0.0.0", Port = 443 },
                Ftp = new ProtocolEndpointSettings { BindAddress = "0.0.0.0", Port = 21 },
                Ftps = new ProtocolEndpointSettings { BindAddress = "0.0.0.0", Port = 990 },
                Tftp = new ProtocolEndpointSettings { BindAddress = "0.0.0.0", Port = 69 },
                Sftp = new ProtocolEndpointSettings { BindAddress = "0.0.0.0", Port = 22 },
                Scp = new ProtocolEndpointSettings { BindAddress = "0.0.0.0", Port = 22 }
            },
            CertificateSettings = new CertificatePathSettings
            {
                DirectoryPath = ApplicationDataPaths.GetDirectory("Certificates")
            },
            SshHostKeySettings = new SshHostKeySettings
            {
                DirectoryPath = ApplicationDataPaths.GetDirectory("Ssh")
            },
            LogSettings = new LogSettings
            {
                DirectoryPath = ApplicationDataPaths.GetDirectory("Logs")
            }
        };
    }
}
