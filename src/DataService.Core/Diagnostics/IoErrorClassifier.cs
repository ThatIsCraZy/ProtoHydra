namespace DataService.Core.Diagnostics;

/// <summary>
/// Maps IO exceptions to actionable causes (missing permissions, OneDrive placeholder
/// not hydrated, device unplugged, disk full, …) so operators can diagnose transfer
/// failures without reading raw Win32 messages.
/// </summary>
public static class IoErrorClassifier
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorWriteProtect = 19;
    private const int ErrorNotReady = 21;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorDevNotExist = 55;
    private const int ErrorDiskFull = 112;
    private const int ErrorIoDevice = 1117;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorDeviceRemoved = 1617;
    private const int ErrorFileOffline = 4350;

    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    public static IoErrorCategory Classify(Exception exception, string? fullPath = null)
    {
        if (exception is UnauthorizedAccessException)
        {
            return IoErrorCategory.AccessDenied;
        }

        if (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return IoErrorCategory.PathNotFound;
        }

        var win32Code = GetWin32Code(exception);
        var category = win32Code switch
        {
            ErrorAccessDenied or ErrorWriteProtect => IoErrorCategory.AccessDenied,
            ErrorNotReady or ErrorDevNotExist or ErrorIoDevice
                or ErrorDeviceNotConnected or ErrorDeviceRemoved => IoErrorCategory.DeviceUnavailable,
            ErrorDiskFull or ErrorHandleDiskFull => IoErrorCategory.DiskFull,
            ErrorSharingViolation or ErrorLockViolation => IoErrorCategory.SharingViolation,
            2 or 3 => IoErrorCategory.PathNotFound,
            ErrorFileOffline => IoErrorCategory.CloudFileUnavailable,
            _ => IoErrorCategory.Unknown
        };

        // OneDrive/cloud placeholders surface as generic IO errors; the placeholder
        // attributes on the file identify the real cause.
        if (category is IoErrorCategory.Unknown or IoErrorCategory.DeviceUnavailable
            && exception is IOException
            && IsCloudPlaceholder(fullPath))
        {
            return IoErrorCategory.CloudFileUnavailable;
        }

        return category;
    }

    public static string Describe(IoErrorCategory category)
        => category switch
        {
            IoErrorCategory.AccessDenied => "Access denied — check file/folder permissions",
            IoErrorCategory.CloudFileUnavailable => "Cloud placeholder (e.g. OneDrive) could not be downloaded",
            IoErrorCategory.DeviceUnavailable => "Storage device is not available (removed or offline)",
            IoErrorCategory.DiskFull => "Disk is full",
            IoErrorCategory.SharingViolation => "File is locked by another process",
            IoErrorCategory.PathNotFound => "File or folder no longer exists",
            _ => "IO error"
        };

    public static string BuildMessage(Exception exception, string? fullPath = null)
        => $"{Describe(Classify(exception, fullPath))}: {exception.Message}";

    private static int GetWin32Code(Exception exception)
    {
        var hresult = exception.HResult;
        return (hresult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
            ? hresult & 0xFFFF
            : -1;
    }

    private static bool IsCloudPlaceholder(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        try
        {
            var attributes = File.GetAttributes(fullPath);
            return (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
