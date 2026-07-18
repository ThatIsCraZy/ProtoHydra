using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DataService.Infrastructure.Certificates;

public sealed class CertificateManager : ICertificateManager
{
    public async Task<X509Certificate2> GetOrCreateAsync(
        CertificatePurpose purpose,
        CertificateSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.DirectoryPath))
        {
            throw new ArgumentException("Certificate directory is required.", nameof(settings));
        }

        Directory.CreateDirectory(settings.DirectoryPath);
        var certificatePath = Path.Combine(settings.DirectoryPath, $"{purpose.ToString().ToLowerInvariant()}.pfx");

        if (File.Exists(certificatePath))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath,
                (string?)null,
                X509KeyStorageFlags.Exportable);
        }

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN=ProtoHydra {purpose}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")],
                critical: false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        foreach (var address in GetLocalAddresses())
        {
            san.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(san.Build());

        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));
        var certificate = X509CertificateLoader.LoadPkcs12(
            created.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable);

        await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pfx), cancellationToken);
        return certificate;
    }

    private static IEnumerable<IPAddress> GetLocalAddresses()
        => Dns.GetHostAddresses(Dns.GetHostName())
            .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
}
