using Boilerplate.BuildingBlocks.Shared.Storage;
using System.Text.Json.Serialization;
using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Users.UpdateUser;

public class UpdateUserCommand : ICommand<Unit>
{
    public string Id { get; set; } = default!;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    public FileUploadRequest? Image { get; set; }
    public bool DeleteCurrentImage { get; set; }

    /// <summary>
    /// The request's raw <c>If-Match</c> header, set by the endpoint — never read from the body.
    /// Null when the caller sent none; optimistic concurrency checking is optional.
    /// </summary>
    [JsonIgnore]
    public string? IfMatch { get; set; }
}