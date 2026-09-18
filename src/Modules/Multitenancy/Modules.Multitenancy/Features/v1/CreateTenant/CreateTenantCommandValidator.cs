using FluentValidation;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.Modules.Multitenancy.Contracts;
using Boilerplate.Modules.Multitenancy.Contracts.v1.CreateTenant;

namespace Boilerplate.Modules.Multitenancy.Features.v1.CreateTenant;

public sealed class CreateTenantCommandValidator : AbstractValidator<CreateTenantCommand>
{
    public CreateTenantCommandValidator(
        ITenantService tenantService,
        IConnectionStringValidator connectionStringValidator,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        RuleFor(t => t.Id).Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MustAsync(async (id, ct) => !await tenantService.ExistsWithIdAsync(id, ct).ConfigureAwait(false))
            .WithMessage((_, id) => $"Tenant {id} already exists.");

        RuleFor(t => t.Name).Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MustAsync(async (name, ct) => !await tenantService.ExistsWithNameAsync(name!, ct).ConfigureAwait(false))
            .WithMessage((_, name) => $"Tenant {name} already exists.");

        RuleFor(t => t.ConnectionString).Cascade(CascadeMode.Stop)
            .Must((_, cs) => string.IsNullOrWhiteSpace(cs) || connectionStringValidator.TryValidate(cs))
            .WithMessage("Connection string invalid.");

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