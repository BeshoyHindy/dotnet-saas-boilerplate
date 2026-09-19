using Boilerplate.Modules.Files.Authorization;
using Boilerplate.Modules.Files.Contracts;

namespace Files.Tests.Authorization;

public class DefaultUploaderOnlyPolicyTests
{
    private static FileAccessContext PublicFileOwnedBy(string uploaderId) =>
        new(Guid.NewGuid(), "MyFiles", null, uploaderId, Visibility: 0);

    private static FileAccessContext PrivateFileOwnedBy(string uploaderId) =>
        new(Guid.NewGuid(), "MyFiles", null, uploaderId, Visibility: 1);

    [Fact]
    public async Task CanAttachAsync_Should_AllowAuthenticated()
    {
        var p = new DefaultUploaderOnlyPolicy("MyFiles");
        (await p.CanAttachAsync(null, "user-1", default)).ShouldBeTrue();
    }

    [Fact]
    public async Task CanAttachAsync_Should_DenyAnonymous()
    {
        var p = new DefaultUploaderOnlyPolicy("MyFiles");
        (await p.CanAttachAsync(null, "", default)).ShouldBeFalse();
    }

    [Fact]
    public async Task CanAttachAsync_Should_AllowTheCallersOwnOwnerId()
    {
        var p = new DefaultUploaderOnlyPolicy("User");
        var caller = Guid.NewGuid();
        (await p.CanAttachAsync(caller, caller.ToString(), default)).ShouldBeTrue();
    }

    /// <summary>
    /// These owner types are self-owned, and an owner id from the body is caller-supplied: accepting
    /// somebody else's produced a file nobody could read, and — with a user id from another tenant —
    /// a row referencing a subject the caller cannot see.
    /// </summary>
    [Fact]
    public async Task CanAttachAsync_Should_DenySomebodyElsesOwnerId()
    {
        var p = new DefaultUploaderOnlyPolicy("User");
        (await p.CanAttachAsync(Guid.NewGuid(), Guid.NewGuid().ToString(), default)).ShouldBeFalse();
    }

    [Fact]
    public async Task CanReadAsync_Should_AllowAnyone_ForPublicFile()
    {
        var p = new DefaultUploaderOnlyPolicy("MyFiles");
        (await p.CanReadAsync(PublicFileOwnedBy("uploader"), "someone-else", default)).ShouldBeTrue();
    }

    [Fact]
    public async Task CanReadAsync_Should_AllowUploaderOnly_ForPrivateFile()
    {
        var p = new DefaultUploaderOnlyPolicy("MyFiles");
        (await p.CanReadAsync(PrivateFileOwnedBy("uploader"), "uploader", default)).ShouldBeTrue();
        (await p.CanReadAsync(PrivateFileOwnedBy("uploader"), "someone-else", default)).ShouldBeFalse();
    }

    [Fact]
    public async Task CanDeleteAsync_Should_AllowUploaderOnly()
    {
        var p = new DefaultUploaderOnlyPolicy("MyFiles");
        (await p.CanDeleteAsync(PublicFileOwnedBy("uploader"), "uploader", default)).ShouldBeTrue();
        (await p.CanDeleteAsync(PublicFileOwnedBy("uploader"), "someone-else", default)).ShouldBeFalse();
    }
}
