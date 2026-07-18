using DataService.Core.Events;
using DataService.Protocols.Ssh;
using SFTP.Enums;
using SFTP.Exceptions;
using SFTP.Models;

namespace DataService.IntegrationTests;

/// <summary>
/// READLINK must answer path-based like a POSIX server (OpenSSH): NoSuchFile for a
/// missing path, plain Failure for an existing non-symlink — never OP_UNSUPPORTED,
/// which some automated clients treat as a session-fatal capability failure.
/// </summary>
public sealed class RootedSftpHandlerReadLinkTests
{
    private static (RootedSftpHandler Handler, TransferEventBus Bus, string Root) CreateHandler()
    {
        var root = Path.Combine(Path.GetTempPath(), "hydra-readlink", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bus = new TransferEventBus();
        return (new RootedSftpHandler(root, bus, "test-user"), bus, root);
    }

    private static List<TransferEvent> DrainEvents(TransferEventBus bus)
    {
        var events = new List<TransferEvent>();
        while (bus.Reader.TryRead(out var transferEvent))
        {
            events.Add(transferEvent);
        }

        return events;
    }

    [Fact]
    public async Task ReadLink_MissingPath_ReturnsNoSuchFile_AndPublishesFailedLookup()
    {
        var (handler, bus, _) = CreateHandler();

        var exception = await Assert.ThrowsAsync<PathNotFoundException>(
            () => handler.ReadLink(new SFTPPath("/missing-link.txt")));

        Assert.Equal(Status.NoSuchFile, exception.Status);
        var events = DrainEvents(bus);
        Assert.Contains(events, item => item.Command == "READLINK"
            && item.Result == TransferResult.Failed
            && item.RelativePath == "missing-link.txt"
            && item.Message == "No such file or directory.");
    }

    [Fact]
    public async Task ReadLink_ExistingRegularFile_ReturnsFailure_NotOperationUnsupported()
    {
        var (handler, bus, root) = CreateHandler();
        await File.WriteAllTextAsync(Path.Combine(root, "regular.txt"), "not a link");

        var exception = await Assert.ThrowsAsync<SftpStatusException>(
            () => handler.ReadLink(new SFTPPath("/regular.txt")));

        Assert.Equal(Status.Failure, exception.Status);
        Assert.NotEqual(Status.OperationUnsupported, exception.Status);
        var events = DrainEvents(bus);
        Assert.Contains(events, item => item.Command == "READLINK"
            && item.Result == TransferResult.Failed
            && item.RelativePath == "regular.txt"
            && item.Message == "Not a symbolic link.");
    }

    [Fact]
    public async Task ReadLink_ExistingDirectory_ReturnsFailure()
    {
        var (handler, _, root) = CreateHandler();
        Directory.CreateDirectory(Path.Combine(root, "folder"));

        var exception = await Assert.ThrowsAsync<SftpStatusException>(
            () => handler.ReadLink(new SFTPPath("/folder")));

        Assert.Equal(Status.Failure, exception.Status);
    }

    [Fact]
    public async Task ReadLink_PathTraversal_IsRejectedWithPermissionDenied()
    {
        var (handler, _, _) = CreateHandler();

        var exception = await Assert.ThrowsAsync<SftpStatusException>(
            () => handler.ReadLink(new SFTPPath("/../outside.txt")));

        Assert.Equal(Status.PermissionDenied, exception.Status);
    }
}
