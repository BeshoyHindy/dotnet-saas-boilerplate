using FluentValidation;
using Boilerplate.Modules.Files.Contracts.v1.Commands;

namespace Boilerplate.Modules.Files.Features.v1.DeleteFile;

public sealed class DeleteFileCommandValidator : AbstractValidator<DeleteFileCommand>
{
    public DeleteFileCommandValidator()
    {
        RuleFor(x => x.FileAssetId).NotEmpty();
    }
}
