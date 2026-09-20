namespace Integration.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class AppCollectionDefinition : ICollectionFixture<AppWebApplicationFactory>
{
    public const string Name = "AppIntegration";
}
