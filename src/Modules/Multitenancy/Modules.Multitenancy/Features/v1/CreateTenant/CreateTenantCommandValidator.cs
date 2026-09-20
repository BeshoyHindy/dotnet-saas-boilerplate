using FluentValidation;
using Boilerplate.Modules.Multitenancy.Contracts;
using Boilerplate.Modules.Multitenancy.Contracts.v1.CreateTenant;
using System.Text.RegularExpressions;

namespace Boilerplate.Modules.Multitenancy.Features.v1.CreateTenant;

public sealed partial class CreateTenantCommandValidator : AbstractValidator<CreateTenantCommand>
{
    /// <summary>
    /// A tenant Id is a lowercase slug: 2–63 characters of <c>a-z0-9-</c>, not starting with a
    /// hyphen. It is not just a database key — it is interpolated into the tenant-scoped auth
    /// routes and into the refresh cookie's <c>Path</c>, and it is the prefix of every refresh
    /// token. Keeping it to a slug means none of those sinks can be escaped, and the value stays
    /// legible in a URL. It is also immutable, so this is the only place to enforce it.
    /// </summary>
    public const string IdPattern = "^[a-z0-9][a-z0-9-]{1,62}$";

    [GeneratedRegex(IdPattern, RegexOptions.CultureInvariant)]
    private static partial Regex IdSlug();

    public CreateTenantCommandValidator(
        ITenantService tenantService,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        RuleFor(t => t.Id).Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(id => IdSlug().IsMatch(id))
            .WithMessage("Tenant id must be 2-63 characters of lowercase letters, digits and hyphens, and may not start with a hyphen.")
            .MustAsync(async (id, ct) => !await tenantService.ExistsWithIdAsync(id, ct).ConfigureAwait(false))
            .WithMessage((_, id) => $"Tenant {id} already exists.");

        RuleFor(t => t.Name).Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MustAsync(async (name, ct) => !await tenantService.ExistsWithNameAsync(name!, ct).ConfigureAwait(false))
            .WithMessage((_, name) => $"Tenant {name} already exists.");

        RuleFor(t => t.AdminEmail).Cascade(CascadeMode.Stop)
            .NotEmpty()
            .EmailAddress();

        // Admin password is operator-supplied. The 8-char floor matches the Identity policy; mixed-character
        // rules (digit/upper/non-alpha) are enforced later by Identity's PasswordValidators at seed time.
        RuleFor(t => t.AdminPassword).Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MinimumLength(8)
            .WithMessage("Admin password must be at least 8 characters.");

        // Optional — null falls back to the configured default validity term. When supplied it must
        // grant the tenant a window that is still open.
        // Normalized as UTC, matching how TenantService stores the date.
        RuleFor(t => t.ValidUpto)
            .Must(validUpto => DateTime.SpecifyKind(validUpto!.Value, DateTimeKind.Utc)
                > timeProvider.GetUtcNow().UtcDateTime)
            .When(t => t.ValidUpto.HasValue)
            .WithMessage("Valid upto must be in the future.");
    }
}