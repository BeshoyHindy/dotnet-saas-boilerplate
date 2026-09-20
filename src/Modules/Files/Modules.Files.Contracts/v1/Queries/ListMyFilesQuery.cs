using System.Collections.ObjectModel;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Mediator;

namespace Boilerplate.Modules.Files.Contracts.v1.Queries;

public sealed record ListMyFilesQuery(int Page = 1, int PageSize = 20) : IQuery<ReadOnlyCollection<FileAssetDto>>;
