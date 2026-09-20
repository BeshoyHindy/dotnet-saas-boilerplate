using System.ComponentModel.DataAnnotations;

namespace Boilerplate.Modules.Identity;

/// <summary>
/// Lifetime ceiling for every token that crosses — or borrows — an identity: the operator token
/// exchange (#9) and impersonation both mint through the same issuer, so there is exactly one
/// number to reason about (config section <c>"OperatorExchange"</c>).
/// </summary>
public sealed class OperatorExchangeOptions : IValidatableObject
{
    public const string SectionName = "OperatorExchange";

    /// <summary>Lifetime used when the caller does not request one.</summary>
    [Range(1, 1440)]
    public int DefaultMinutes { get; set; } = 15;

    /// <summary>Hard ceiling. A larger request is clamped to this, never honoured.</summary>
    [Range(1, 1440)]
    public int MaxMinutes { get; set; } = 60;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DefaultMinutes > MaxMinutes)
        {
            yield return new ValidationResult(
                $"{nameof(DefaultMinutes)} ({DefaultMinutes}) must not exceed {nameof(MaxMinutes)} ({MaxMinutes}).",
                [nameof(DefaultMinutes), nameof(MaxMinutes)]);
        }
    }
}
