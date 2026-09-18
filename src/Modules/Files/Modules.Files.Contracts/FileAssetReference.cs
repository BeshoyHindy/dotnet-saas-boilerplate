namespace Boilerplate.Modules.Files.Contracts;

/// <summary>Owning-feature handle to a FileAsset. Stored on an owning module's join table.</summary>
public sealed record FileAssetReference(Guid Id, string OwnerType, Guid? OwnerId);
