namespace Boilerplate.BuildingBlocks.Shared.Multitenancy;

public interface IAppTenantInfo
{
    string? ConnectionString { get; set; }
}
