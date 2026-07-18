using DataService.Core.Diagnostics;

namespace DataService.App.ViewModels;

public sealed class IoErrorEntryViewModel
{
    public IoErrorEntryViewModel(IoErrorEntry entry)
    {
        Time = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        Protocol = entry.Protocol?.ToString().ToUpperInvariant() ?? "-";
        Category = entry.Category.ToString();
        Path = entry.Path ?? "-";
        Message = entry.Message;
    }

    public string Time { get; }

    public string Protocol { get; }

    public string Category { get; }

    public string Path { get; }

    public string Message { get; }
}
