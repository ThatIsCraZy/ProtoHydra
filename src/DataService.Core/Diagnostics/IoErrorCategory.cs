namespace DataService.Core.Diagnostics;

public enum IoErrorCategory
{
    AccessDenied,
    CloudFileUnavailable,
    DeviceUnavailable,
    DiskFull,
    SharingViolation,
    PathNotFound,
    Unknown
}
