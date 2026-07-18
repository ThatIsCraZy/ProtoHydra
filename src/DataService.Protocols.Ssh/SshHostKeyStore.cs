using FxSsh;

namespace DataService.Protocols.Ssh;

internal sealed class SshHostKeyStore
{
    private readonly string _directoryPath;

    public SshHostKeyStore(string directoryPath)
    {
        _directoryPath = string.IsNullOrWhiteSpace(directoryPath)
            ? Path.Combine(AppContext.BaseDirectory, "ssh")
            : directoryPath;
    }

    public async Task<string> GetOrCreateRsaKeyPemAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directoryPath);
        var keyPath = Path.Combine(_directoryPath, "host-rsa-3072.pem");
        if (File.Exists(keyPath))
        {
            return await File.ReadAllTextAsync(keyPath, cancellationToken);
        }

        var pem = KeyGenerator.GenerateRsaKeyPem(3072);
        await File.WriteAllTextAsync(keyPath, pem, cancellationToken);
        TryRestrictWindowsAcl(keyPath);
        return pem;
    }

    private static void TryRestrictWindowsAcl(string keyPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetAttributes(keyPath, File.GetAttributes(keyPath) | FileAttributes.Hidden);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
