using FluentValidation;
using Boilerplate.BuildingBlocks.Shared.Storage;
using Boilerplate.BuildingBlocks.Storage;
using Boilerplate.Modules.Multitenancy.Contracts.Dtos;

namespace Boilerplate.Modules.Multitenancy.Features.v1.UpdateTenantTheme;

/// <summary>
/// The extension/size gate for the brand assets a theme save carries, and the mirror of the avatar
/// one (<c>UserImageValidator</c> in the Identity module).
///
/// <para>Without it a <c>.svg</c> or a 40 MB <c>.png</c> went straight to
/// <c>IStorageService.UploadAsync</c>, whose own check throws <see cref="InvalidOperationException"/>
/// — a 500 for what is a caller's mistake. Validating here answers it as a 400 ProblemDetails, with
/// the offending slot named, and nothing reaches storage: the whole theme save is rejected before
/// the handler runs, so the logo already stored stays where it is.</para>
///
/// <para>The limits are not restated here. Both sides read
/// <see cref="FileTypeMetadata.GetRules(FileType)"/> for <see cref="FileType.Image"/>, which is the
/// same list <c>UploadAsync</c> enforces — the point being that a rule change in
/// <c>BuildingBlocks/Storage/FileType.cs</c> moves this gate with it.</para>
/// </summary>
public sealed class BrandAssetUploadsValidator : AbstractValidator<BrandAssetUploadsDto>
{
    public BrandAssetUploadsValidator()
    {
        var image = new ImageUploadValidator(FileType.Image);

        // Each slot is optional: a save that only changes the palette carries none of them, and a
        // removal carries a flag rather than a file. Only a slot that actually holds bytes is checked.
        When(x => x.Logo is not null, () => RuleFor(x => x.Logo!).SetValidator(image));
        When(x => x.LogoDark is not null, () => RuleFor(x => x.LogoDark!).SetValidator(image));
        When(x => x.Favicon is not null, () => RuleFor(x => x.Favicon!).SetValidator(image));
    }

    /// <summary>
    /// The same two rules <c>UserImageValidator</c> applies, over the same
    /// <see cref="FileValidationRules"/>. It lives in this module rather than in a shared place
    /// because the one location both modules could reach is <c>BuildingBlocks/</c>, which
    /// <c>.agents/rules/buildingblocks-protection.md</c> puts behind explicit approval.
    /// </summary>
    private sealed class ImageUploadValidator : AbstractValidator<FileUploadRequest>
    {
        public ImageUploadValidator(FileType fileType)
        {
            var rules = FileTypeMetadata.GetRules(fileType);

            RuleFor(x => x.FileName)
                .NotEmpty()
                .Must(file => rules.AllowedExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .WithMessage($"Only these extensions are allowed: {string.Join(", ", rules.AllowedExtensions)}");

            RuleFor(x => x.Data)
                .NotEmpty()
                .Must(data => data.Count <= rules.MaxSizeInMB * 1024 * 1024)
                .WithMessage($"File must be <= {rules.MaxSizeInMB} MB.");
        }
    }
}
