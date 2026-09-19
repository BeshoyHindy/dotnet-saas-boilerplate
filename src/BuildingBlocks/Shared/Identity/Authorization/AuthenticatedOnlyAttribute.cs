namespace Boilerplate.BuildingBlocks.Shared.Identity.Authorization;

/// <summary>
/// Marks an endpoint whose only gate is "be signed in" — self-service routes that operate on the
/// caller's own row (own profile, own password, own 2FA enrollment). It exists so the permission
/// policy can fail closed: an endpoint with no permission, no <c>AllowAnonymous</c> and no marker
/// is a missing decision, not an open door.
/// </summary>
public interface IAuthenticatedOnlyMetadata;

/// <inheritdoc cref="IAuthenticatedOnlyMetadata"/>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AuthenticatedOnlyAttribute : Attribute, IAuthenticatedOnlyMetadata;
