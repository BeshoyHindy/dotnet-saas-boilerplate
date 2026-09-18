using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Mediator;

namespace Boilerplate.Modules.Files.Contracts.v1.Commands;

public sealed record FinalizeUploadCommand(Guid FileAssetId) : ICommand<FileAssetDto>;
