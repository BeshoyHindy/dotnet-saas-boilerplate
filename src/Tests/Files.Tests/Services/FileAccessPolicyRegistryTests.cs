using Boilerplate.Modules.Files.Contracts;
using Boilerplate.Modules.Files.Services;
using NSubstitute;

namespace Files.Tests.Services;

public class FileAccessPolicyRegistryTests
{
    [Fact]
    public void Resolve_Should_ReturnPolicy_When_Registered()
    {
        var p = Substitute.For<IFileAccessPolicy>();
        p.OwnerType.Returns("Document");
        var reg = new FileAccessPolicyRegistry([p]);
        reg.Resolve("Document").ShouldBe(p);
    }

    [Fact]
    public void Resolve_Should_BeCaseInsensitive()
    {
        var p = Substitute.For<IFileAccessPolicy>();
        p.OwnerType.Returns("MyFiles");
        var reg = new FileAccessPolicyRegistry([p]);
        reg.Resolve("MYFILES").ShouldBe(p);
    }

    [Fact]
    public void Resolve_Should_ReturnNull_When_NotRegistered()
    {
        var reg = new FileAccessPolicyRegistry([]);
        reg.Resolve("Unknown").ShouldBeNull();
    }

    [Fact]
    public void Resolve_Should_TakeLastWinsOnDuplicateOwnerType()
    {
        var first = Substitute.For<IFileAccessPolicy>();
        first.OwnerType.Returns("Document");
        var second = Substitute.For<IFileAccessPolicy>();
        second.OwnerType.Returns("Document");

        var reg = new FileAccessPolicyRegistry([first, second]);
        reg.Resolve("Document").ShouldBe(second);
    }
}
