namespace Boilerplate.BuildingBlocks.Shared.Multitenancy;

public static class MultitenancyConstants
{
    public static class Root
    {
        public const string Id = "root";
        public const string Name = "Root";
        public const string EmailAddress = "admin@root.com";
        public const string DefaultProfilePicture = "assets/defaults/profile-picture.webp";
        public const string Issuer = "boilerplate";
    }

    // No `Identifier` constant: there is no caller-supplied tenant identifier any more (ADR-0002).
    // The tenant comes from the token's `tenant` claim (ClaimConstants.Tenant) or, for the anonymous
    // auth routes only, from the {tenant} route value (TenantRoute.ValueKey).
    public const string Schema = "tenant";
}