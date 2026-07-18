using System.Globalization;
using Avalonia.Media;
using DataService.Core.Events;

namespace DataService.App.ViewModels;

public sealed class LogEventViewModel
{
    public LogEventViewModel(TransferEvent transferEvent)
    {
        Time = transferEvent.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        Protocol = transferEvent.Protocol.ToString().ToUpperInvariant();
        Event = transferEvent.EventKind.ToString();
        Source = transferEvent.SourceAddress?.ToString() ?? "";
        User = transferEvent.Username ?? "";
        Command = transferEvent.Command ?? "";
        Path = transferEvent.RelativePath ?? "";
        Direction = transferEvent.Direction?.ToString() ?? "";
        Bytes = transferEvent.ByteCount?.ToString("N0", CultureInfo.InvariantCulture) ?? "";
        Result = transferEvent.Result.ToString();
        IsFailed = transferEvent.Result == TransferResult.Failed;
        IsRejected = transferEvent.Result == TransferResult.Rejected;
        Duration = transferEvent.Duration is null
            ? ""
            : $"{transferEvent.Duration.Value.TotalMilliseconds:N0} ms";
        Message = transferEvent.Message ?? "";
        ProtocolBrush = CreateProtocolBrush(transferEvent.Protocol);
        CommandBrush = string.IsNullOrWhiteSpace(Command)
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromRgb(45, 116, 211));
        SourceBrush = string.IsNullOrWhiteSpace(Source)
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromRgb(18, 132, 89));
    }

    public string Time { get; }

    public string Protocol { get; }

    public string Event { get; }

    public string Source { get; }

    public string User { get; }

    public string Command { get; }

    public string Path { get; }

    public string Direction { get; }

    public string Bytes { get; }

    public string Result { get; }

    public bool IsFailed { get; }

    public bool IsRejected { get; }

    public string Duration { get; }

    public string Message { get; }

    public IBrush ProtocolBrush { get; }

    public IBrush CommandBrush { get; }

    public IBrush SourceBrush { get; }

    public string ExportLine
        => string.Join(
            '\t',
            Time,
            Protocol,
            Event,
            Source,
            User,
            Command,
            Path,
            Direction,
            Bytes,
            Result,
            Duration,
            Message);

    private static IBrush CreateProtocolBrush(ProtocolKind protocol)
        => protocol switch
        {
            ProtocolKind.Http => new SolidColorBrush(Color.FromRgb(14, 120, 170)),
            ProtocolKind.Https => new SolidColorBrush(Color.FromRgb(33, 144, 93)),
            ProtocolKind.Ftp => new SolidColorBrush(Color.FromRgb(178, 104, 19)),
            ProtocolKind.Ftps => new SolidColorBrush(Color.FromRgb(124, 112, 28)),
            ProtocolKind.Tftp => new SolidColorBrush(Color.FromRgb(132, 88, 196)),
            ProtocolKind.Sftp => new SolidColorBrush(Color.FromRgb(186, 83, 142)),
            ProtocolKind.Scp => new SolidColorBrush(Color.FromRgb(194, 72, 62)),
            _ => Brushes.Transparent
        };
}
