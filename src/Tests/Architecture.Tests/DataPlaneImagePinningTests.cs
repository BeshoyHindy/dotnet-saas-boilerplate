using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Every tag-only image the stack still pulled by tag alone — Postgres, Valkey and Mailpit in the
/// AppHost and the compose stacks, plus Node and nginx in both clients' Dockerfiles — is now pinned
/// by tag AND digest, the same contract <see cref="ObjectStoreImagePinningTests"/> already holds the
/// object store and its aws-cli bootstrap to. A floating tag can resolve to different bytes on two
/// machines pulling on two different days; the digest is what makes every environment run the
/// identical image regardless of when it pulled.
///
/// <para>Text scans, like <see cref="ObjectStoreImagePinningTests"/>: no Docker needed in this gate.</para>
/// </summary>
public sealed class DataPlaneImagePinningTests
{
    private const string PostgresTag = "18-alpine";
    private const string PostgresDigest = "77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873";
    private const string PinnedPostgresImage = $"postgres:{PostgresTag}@sha256:{PostgresDigest}";

    private const string ValkeyTag = "9.1.2-alpine";
    private const string ValkeyDigest = "48332870af354a799964c0012ae1194a0bf2bf894eb508f945810596dc2d8d11";
    private const string PinnedValkeyImage = $"valkey/valkey:{ValkeyTag}@sha256:{ValkeyDigest}";

    // The AppHost runs Valkey under a bare "9.1.2" tag (no -alpine suffix, pre-existing), so its
    // digest differs from the compose stacks' "9.1.2-alpine" pin above.
    private const string AppHostValkeyTag = "9.1.2";
    private const string AppHostValkeyDigest = "418652cfb58ef879d4978c33553735d7147016032d5aefaa14c828e611eb9dfd";

    private const string MailpitTag = "v1.31";
    private const string MailpitDigest = "74d609a42ec279aa63c6b4622a6fa9b5408d1ad5b1d76a1c4be40a265ce0863d";
    private const string PinnedMailpitImage = $"axllent/mailpit:{MailpitTag}@sha256:{MailpitDigest}";

    private const string NodeTag = "24-alpine";
    private const string NodeDigest = "ebfe2f90462722a7a4de65e91990e97fe0d401c70e0e762c5b53302f905ec1c1";
    private const string PinnedNodeImage = $"node:{NodeTag}@sha256:{NodeDigest}";

    private const string NginxTag = "1.31-alpine";
    private const string NginxDigest = "6a23acdfca2b9cfbcec61419e3f1426bcbedb91362f2f19306a8567423bb4612";
    private const string PinnedNginxImage = $"nginxinc/nginx-unprivileged:{NginxTag}@sha256:{NginxDigest}";

    private static readonly string SolutionRoot = ModuleArchitectureTestsFixture.SolutionRoot;

    // Forward slashes on purpose: Path.Combine accepts them on every OS, and they read like the
    // paths the failure messages name.
//#if (aspire)
    private const string AppHostPath = "src/Host/Boilerplate.AppHost/AppHost.cs";
//#endif
    private const string LocalComposePath = "docker-compose.yml";
    private const string DokployComposePath = "deploy/dokploy/data-services.compose.yml";
    private const string DashboardDockerfilePath = "clients/dashboard/Dockerfile";
    private const string ConsoleDockerfilePath = "clients/console/Dockerfile";

    #region Happy Path

    [Theory]
    [InlineData(LocalComposePath)]
    [InlineData(DokployComposePath)]
    public void ComposeStacks_Should_PinPostgresAndValkey_When_DeclaringTheDataPlane(string composePath)
    {
        string compose = Read(composePath);

        compose.ShouldContain($"image: {PinnedPostgresImage}", customMessage:
            $"{composePath}: postgres must carry the tag-and-digest pin");
        compose.ShouldContain($"image: {PinnedValkeyImage}", customMessage:
            $"{composePath}: valkey must carry the tag-and-digest pin");
    }

    [Fact]
    public void LocalCompose_Should_PinMailpit_When_DeclaringTheMailCatcher()
    {
        Read(LocalComposePath).ShouldContain(
            $"image: {PinnedMailpitImage}",
            customMessage: $"{LocalComposePath}: mailpit must carry the tag-and-digest pin");
    }

//#if (aspire)
    [Fact]
    public void AppHost_Should_PinPostgresValkeyAndMailpit_When_DeclaringTheDataPlane()
    {
        string appHost = Read(AppHostPath);

        appHost.ShouldContain($"const string PostgresImageTag = \"{PostgresTag}\";");
        appHost.ShouldContain($"const string PostgresImageDigest = \"{PostgresDigest}\";");
        appHost.ShouldContain(".WithImageTag(PostgresImageTag)");
        appHost.ShouldContain(".WithImageSHA256(PostgresImageDigest)");

        appHost.ShouldContain($"const string ValkeyImageTag = \"{AppHostValkeyTag}\";");
        appHost.ShouldContain($"const string ValkeyImageDigest = \"{AppHostValkeyDigest}\";");
        appHost.ShouldContain(".WithImageSHA256(ValkeyImageDigest)");

        appHost.ShouldContain($"const string MailpitImageTag = \"{MailpitTag}\";");
        appHost.ShouldContain($"const string MailpitImageDigest = \"{MailpitDigest}\";");
        appHost.ShouldContain(".WithImageSHA256(MailpitImageDigest)");
    }
//#endif

//#if (frontend)
    [Theory]
    [InlineData(DashboardDockerfilePath)]
    [InlineData(ConsoleDockerfilePath)]
    public void ClientDockerfiles_Should_PinNodeAndNginx_When_BuildingTheImage(string dockerfilePath)
    {
        string dockerfile = Read(dockerfilePath);

        dockerfile.ShouldContain($"FROM {PinnedNodeImage} AS build", customMessage:
            $"{dockerfilePath}: the build stage must pin node by tag and digest");
        dockerfile.ShouldContain($"FROM {PinnedNginxImage} AS runtime", customMessage:
            $"{dockerfilePath}: the runtime stage must pin nginx by tag and digest");
    }
//#endif

    #endregion

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(SolutionRoot, relativePath));
}
