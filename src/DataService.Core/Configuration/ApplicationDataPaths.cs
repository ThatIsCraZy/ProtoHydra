namespace DataService.Core.Configuration;

public static class ApplicationDataPaths
{
    public const string ProductDirectoryName = "ProtoHydra";

    public static string Root
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = AppContext.BaseDirectory;
            }

            return Path.Combine(localAppData, ProductDirectoryName);
        }
    }

    public static string GetDirectory(params string[] segments)
        => Path.Combine([Root, .. segments]);
}
