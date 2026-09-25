using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Every place that starts MinIO pulls the same Chainguard image, pinned by digest.
///
/// <para>Docker Hub stopped carrying MinIO first; then quay.io withdrew anonymous pulls (401 even for
/// <c>:latest</c>). The failure that left was silent and total: the AppHost's <c>minio</c> container was
/// never created, <c>minio-init</c> waited for it forever, and through <c>WaitForCompletion</c> the API
/// and both clients hung with no error — while the compose stacks and the Testcontainers suites
/// failed to pull. Chainguard's free tier publishes only <c>:latest</c>, so the digest is the only
/// reproducible pin, and it is kept identical everywhere so a bump is one value, not five drifting ones.</para>
///
/// <para>The image runs as uid 65532 where the old ones ran as root, so a volume the old image wrote is
/// root-owned and the server refuses it. Each stack with a persistent volume therefore runs a root
/// <c>minio-volume-owner</c> one-shot before MinIO starts; that wiring is pinned here for the AppHost and
/// the root compose file, and by <c>deploy/dokploy/tests/compose-contract.test.sh</c> for Dokploy.</para>
///
/// <para>Text scans, like <see cref="StorageKeyOwnershipTests"/>: no Docker needed in this gate.</para>
/// </summary>
public sealed class MinioImagePinningTests
{
    private const string MinioImage = "cgr.dev/chainguard/minio";
    private const string MinioImageDigest = "bd014394a80898e68c149f2311fdf8d5a2c2f3bb2c33b9327ae6d02b4b065ae1";
    private const string PinnedImageReference = $"{MinioImage}@sha256:{MinioImageDigest}";

    private static readonly string SolutionRoot = ModuleArchitectureTestsFixture.SolutionRoot;

    // Forward slashes on purpose: Path.Combine accepts them on every OS, and they read like the
    // paths the failure messages name.
    private const string AppHostPath = "src/Host/Boilerplate.AppHost/AppHost.cs";
    private const string LocalComposePath = "docker-compose.yml";
    private const string DokployComposePath = "deploy/dokploy/data-services.compose.yml";
    private const string IntegrationFactoryPath = "src/Tests/Integration.Tests/Infrastructure/AppWebApplicationFactory.cs";
    private const string MiddlewareFactoryPath =
        "src/Tests/Integration.Middleware.Tests/Infrastructure/MiddlewareWebApplicationFactory.cs";

    #region Happy Path

    [Fact]
    public void AppHost_Should_PinEveryMinioContainerToTheChainguardDigest_When_DeclaringMinio()
    {
        string appHost = Read(AppHostPath);

        appHost.ShouldContain($"const string MinioImage = \"{MinioImage}\";");
        appHost.ShouldContain($"const string MinioImageDigest = \"{MinioImageDigest}\";");
        appHost.ShouldContain("builder.AddContainer(\"minio-volume-owner\", MinioImage)");
        appHost.ShouldContain("builder.AddContainer(\"minio\", MinioImage)");
        appHost.ShouldContain("builder.AddContainer(\"minio-init\", MinioImage)");
        CountOf(appHost, ".WithImageSHA256(MinioImageDigest)")
            .ShouldBe(3, "minio-volume-owner, minio and minio-init must each pin the digest");
    }

    [Fact]
    public void AppHostMinio_Should_WaitForTheVolumeOwner_When_TheImageRunsAsNonRoot()
    {
        string appHost = Read(AppHostPath);

        appHost.ShouldContain(".WithArgs(\"-c\", \"chown -R 65532:65532 /data\")");
        appHost.ShouldContain(".WithContainerRuntimeArgs(\"--user\", \"0\")");
        appHost.ShouldContain(".WaitForCompletion(minioVolumeOwner)");
        CountOf(appHost, ".WithVolume($\"{appPrefix}-minio-data\", \"/data\")")
            .ShouldBe(2, "the volume owner must chown the very volume the server mounts, not a new one");
    }

    [Theory]
    [InlineData(LocalComposePath)]
    [InlineData(DokployComposePath)]
    public void ComposeStacks_Should_PinEveryMinioServiceToTheChainguardDigest_When_DeclaringMinio(string composePath)
    {
        string compose = Read(composePath);

        // minio-volume-owner, minio, minio-init and minio-public-prefix.
        CountOf(compose, $"image: {PinnedImageReference}")
            .ShouldBe(4, $"every MinIO service in {composePath} must pin the Chainguard digest");
        compose.ShouldContain("minio-volume-owner:");
        compose.ShouldContain("command: [\"-R\", \"65532:65532\", \"/data\"]");
    }

    [Fact]
    public void LocalCompose_Should_StartMinioAfterTheVolumeOwner_When_TheImageRunsAsNonRoot()
    {
        string compose = Read(LocalComposePath);

        compose.ShouldContain("minio-volume-owner: { condition: service_completed_successfully }");
        compose.ShouldContain("user: \"0:0\"");
    }

    [Fact]
    public void IntegrationFactories_Should_PinTheChainguardDigest_When_StartingMinioTestcontainers()
    {
        foreach (string factoryPath in new[] { IntegrationFactoryPath, MiddlewareFactoryPath })
        {
            Read(factoryPath).ShouldContain(
                $"private const string MinioImage = \"{PinnedImageReference}\";",
                customMessage: $"{factoryPath} must start MinIO from the pinned Chainguard image");
        }
    }

    [Fact]
    public void MinioSites_Should_NotReferenceWithdrawnRegistries_When_DockerHubAndQuayNoLongerServeMinio()
    {
        string[] sites = [AppHostPath, LocalComposePath, DokployComposePath, IntegrationFactoryPath, MiddlewareFactoryPath];

        foreach (string site in sites)
        {
            string source = Read(site);
            source.ShouldNotContain("quay.io/minio", Case.Sensitive, $"{site}: quay.io withdrew anonymous MinIO pulls");
            source.ShouldNotContain("minio/minio", Case.Sensitive, $"{site}: Docker Hub no longer carries MinIO");
            source.ShouldNotContain("minio/mc", Case.Sensitive, $"{site}: Docker Hub no longer carries mc");
        }
    }

    #endregion

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(SolutionRoot, relativePath));

    private static int CountOf(string text, string value) =>
        text.Split(value).Length - 1;
}
