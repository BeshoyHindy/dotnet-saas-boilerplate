using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Every place that starts the object store pulls the same RustFS image, pinned by tag and digest.
///
/// <para>The store used to be MinIO. Docker Hub stopped carrying its images, quay.io withdrew
/// anonymous pulls, Chainguard's free tier publishes only <c>:latest</c>, and Testcontainers dropped
/// its MinIO module — so the object store is RustFS (issue #95). The pin carries the tag, so a reader
/// can tell the version, and the digest, so every machine runs the same bytes; it is kept identical
/// in all five sites so a bump is one value, not five drifting ones. The bucket bootstrap's
/// <c>amazon/aws-cli</c> image is pinned the same way in the AppHost and both compose stacks.</para>
///
/// <para>Text scans, like <see cref="StorageKeyOwnershipTests"/>: no Docker needed in this gate.</para>
/// </summary>
public sealed class ObjectStoreImagePinningTests
{
    private const string StoreImage = "rustfs/rustfs";
    private const string StoreImageTag = "1.0.0";
    private const string StoreImageDigest = "8cc9801755448b71a786705ce76692c77e14936cccd87cf2fc31842e58f4d1ff";
    private const string PinnedStoreImage = $"{StoreImage}:{StoreImageTag}@sha256:{StoreImageDigest}";

    private const string AwsCliImage = "amazon/aws-cli";
    private const string AwsCliImageTag = "2.37.4";
    private const string AwsCliImageDigest = "fdd8d1fcbea9c371678dee5a40df8b178c7a781b4586605756ee28114c97ead6";
    private const string PinnedAwsCliImage = $"{AwsCliImage}:{AwsCliImageTag}@sha256:{AwsCliImageDigest}";

    private static readonly string SolutionRoot = ModuleArchitectureTestsFixture.SolutionRoot;

    // Forward slashes on purpose: Path.Combine accepts them on every OS, and they read like the
    // paths the failure messages name.
//#if (aspire)
    private const string AppHostPath = "src/Host/Boilerplate.AppHost/AppHost.cs";
//#endif
    private const string LocalComposePath = "docker-compose.yml";
    private const string DokployComposePath = "deploy/dokploy/data-services.compose.yml";
    private const string IntegrationFactoryPath = "src/Tests/Integration.Tests/Infrastructure/AppWebApplicationFactory.cs";
    private const string MiddlewareFactoryPath =
        "src/Tests/Integration.Middleware.Tests/Infrastructure/MiddlewareWebApplicationFactory.cs";

    #region Happy Path

    // The AppHost is dropped from a scaffold made with --aspire false, and its checks with it.
//#if (aspire)
    [Fact]
    public void AppHost_Should_PinTheStoreAndTheBootstrap_When_DeclaringObjectStorage()
    {
        string appHost = Read(AppHostPath);

        appHost.ShouldContain($"const string StorageImage = \"{StoreImage}\";");
        appHost.ShouldContain($"const string StorageImageTag = \"{StoreImageTag}\";");
        appHost.ShouldContain($"const string StorageImageDigest = \"{StoreImageDigest}\";");
        appHost.ShouldContain("builder.AddContainer(\"storage\", StorageImage, StorageImageTag)");
        CountOf(appHost, ".WithImageSHA256(StorageImageDigest)")
            .ShouldBe(1, "the one store container must pin the digest");

        appHost.ShouldContain($"const string AwsCliImage = \"{AwsCliImage}\";");
        appHost.ShouldContain($"const string AwsCliImageTag = \"{AwsCliImageTag}\";");
        appHost.ShouldContain($"const string AwsCliImageDigest = \"{AwsCliImageDigest}\";");
        appHost.ShouldContain("builder.AddContainer(\"storage-init\", AwsCliImage, AwsCliImageTag)");
        appHost.ShouldContain(".WithImageSHA256(AwsCliImageDigest)");
    }
//#endif

    [Theory]
    [InlineData(LocalComposePath, 1)]
    [InlineData(DokployComposePath, 2)]
    public void ComposeStacks_Should_PinTheStoreAndEveryAwsCliService_When_DeclaringObjectStorage(
        string composePath, int awsCliServices)
    {
        string compose = Read(composePath);

        CountOf(compose, $"image: {PinnedStoreImage}")
            .ShouldBe(1, $"the store in {composePath} must carry the tag-and-digest pin");
        // storage-init everywhere; Dokploy adds postgres-backup-upload.
        CountOf(compose, $"image: {PinnedAwsCliImage}")
            .ShouldBe(awsCliServices, $"every aws-cli service in {composePath} must carry the same pin");
        CountOf(compose, "image: rustfs/")
            .ShouldBe(1, $"{composePath} must not start a second, differently pinned store");
        CountOf(compose, "image: amazon/aws-cli")
            .ShouldBe(awsCliServices, $"{composePath} must not run an unpinned aws-cli");
    }

    [Fact]
    public void IntegrationFactories_Should_PinTheStore_When_StartingTheStorageTestcontainer()
    {
        foreach (string factoryPath in new[] { IntegrationFactoryPath, MiddlewareFactoryPath })
        {
            Read(factoryPath).ShouldContain(
                $"private const string StorageImage = \"{PinnedStoreImage}\";",
                customMessage: $"{factoryPath} must start the store from the pinned RustFS image");
        }
    }

    [Fact]
    public void StoreSites_Should_NotReferenceMinioImages_When_NoRegistryServesThemAnonymously()
    {
        string[] sites =
        [
//#if (aspire)
            AppHostPath,
//#endif
            LocalComposePath,
            DokployComposePath,
            IntegrationFactoryPath,
            MiddlewareFactoryPath,
        ];

        foreach (string site in sites)
        {
            string source = Read(site);
            source.ShouldNotContain("minio/", Case.Insensitive, $"{site}: Docker Hub no longer carries MinIO or mc");
            source.ShouldNotContain("quay.io/minio", Case.Insensitive, $"{site}: quay.io withdrew anonymous MinIO pulls");
            source.ShouldNotContain("cgr.dev/chainguard/minio", Case.Insensitive, $"{site}: Chainguard publishes only :latest");
        }
    }

    #endregion

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(SolutionRoot, relativePath));

    private static int CountOf(string text, string value) =>
        text.Split(value).Length - 1;
}
