using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DataService.Core.FileSystem;

public sealed partial class RootPathResolver
{
    private readonly StringComparison _pathComparison;
    private readonly string _rootPath;
    private readonly string _rootPrefix;

    public RootPathResolver(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Root path is required.", nameof(rootPath));
        }

        if (rootPath.Contains('\0'))
        {
            throw new ArgumentException("Root path contains a null byte.", nameof(rootPath));
        }

        _rootPath = Path.GetFullPath(rootPath);
        _rootPrefix = Path.EndsInDirectorySeparator(_rootPath)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        _pathComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    public ResolvedPath ResolveClientPath(string clientPath, bool percentDecode = true)
    {
        if (clientPath is null)
        {
            throw new ArgumentNullException(nameof(clientPath));
        }

        var decodedPath = percentDecode ? DecodeOnce(clientPath) : clientPath;
        var relativeSegments = NormalizeSegments(decodedPath);
        var fullPath = relativeSegments.Length == 0
            ? _rootPath
            : Path.GetFullPath(Path.Combine([_rootPath, .. relativeSegments]));

        if (!IsInsideRoot(fullPath))
        {
            throw new PathResolutionException("Path leaves the configured root folder.");
        }

        RejectExistingReparsePoints(relativeSegments);

        return new ResolvedPath(
            _rootPath,
            string.Join('/', relativeSegments),
            fullPath);
    }

    private static string DecodeOnce(string clientPath)
    {
        if (clientPath.Contains('\0'))
        {
            throw new PathResolutionException("Path contains a null byte.");
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(clientPath);
        }
        catch (UriFormatException ex)
        {
            throw new PathResolutionException($"Path contains invalid percent encoding: {ex.Message}");
        }

        if (decoded.Contains('\0'))
        {
            throw new PathResolutionException("Path contains a null byte.");
        }

        if (DangerousResidualEncoding().IsMatch(decoded))
        {
            throw new PathResolutionException("Path contains double-encoded traversal characters.");
        }

        return decoded;
    }

    private static string[] NormalizeSegments(string decodedPath)
    {
        if (decodedPath.Contains('\\'))
        {
            throw new PathResolutionException("Backslash path separators are not allowed.");
        }

        if (decodedPath.Contains(':'))
        {
            throw new PathResolutionException("Drive-qualified paths are not allowed.");
        }

        if (decodedPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new PathResolutionException("UNC-style paths are not allowed.");
        }

        var trimmed = decodedPath.TrimStart('/');
        var invalidChars = Path.GetInvalidFileNameChars();
        var segments = new List<string>();

        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                throw new PathResolutionException("Relative path traversal is not allowed.");
            }

            if (segment.IndexOfAny(invalidChars) >= 0)
            {
                throw new PathResolutionException("Path contains invalid file name characters.");
            }

            segments.Add(segment);
        }

        return [.. segments];
    }

    private bool IsInsideRoot(string fullPath)
        => string.Equals(fullPath, _rootPath, _pathComparison)
            || fullPath.StartsWith(_rootPrefix, _pathComparison);

    private void RejectExistingReparsePoints(string[] relativeSegments)
    {
        if (Path.Exists(_rootPath) && (File.GetAttributes(_rootPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new PathResolutionException("Root folder is a symbolic link or reparse point.");
        }

        var probe = _rootPath;
        foreach (var segment in relativeSegments)
        {
            probe = Path.Combine(probe, segment);

            if (!Directory.Exists(probe) && !File.Exists(probe))
            {
                break;
            }

            if ((File.GetAttributes(probe) & FileAttributes.ReparsePoint) != 0)
            {
                throw new PathResolutionException("Path contains a symbolic link or reparse point.");
            }
        }
    }

    [GeneratedRegex("%(?:00|2e|2f|5c)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousResidualEncoding();
}
