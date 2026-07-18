namespace DataService.Protocols.Abstractions;

public sealed record ProtocolCapabilities(
    bool SupportsDownload,
    bool SupportsUpload,
    bool SupportsListing,
    bool SupportsAuthentication,
    bool UsesEncryption);

