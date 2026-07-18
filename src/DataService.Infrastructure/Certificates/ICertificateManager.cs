using System.Security.Cryptography.X509Certificates;

namespace DataService.Infrastructure.Certificates;

public interface ICertificateManager
{
    Task<X509Certificate2> GetOrCreateAsync(
        CertificatePurpose purpose,
        CertificateSettings settings,
        CancellationToken cancellationToken);
}

