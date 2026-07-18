using System.Net;
using DataService.Core.Diagnostics;

namespace DataService.Core.Events;

public sealed record TransferEvent(
    DateTimeOffset Timestamp,
    ProtocolKind Protocol,
    TransferEventKind EventKind,
    IPAddress? SourceAddress,
    string? Username,
    string? Command,
    string? RelativePath,
    TransferDirection? Direction,
    long? ByteCount,
    TimeSpan? Duration,
    TransferResult Result,
    string? Message,
    string? CorrelationId,
    IoErrorCategory? IoError = null);

