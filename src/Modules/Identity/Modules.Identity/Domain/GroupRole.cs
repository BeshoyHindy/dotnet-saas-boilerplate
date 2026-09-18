namespace Boilerplate.Modules.Identity.Domain;

public class GroupRole
{
    public Guid GroupId { get; private set; }
    public string RoleId { get; private set; } = default!;

    // Navigation properties (init for EF Core materialization)
    public virtual Group? Group { get; init; }
    public virtual AppRole? Role { get; init; }

    private GroupRole() { } // EF Core

    public static GroupRole Create(Guid groupId, string roleId)
    {
        return new GroupRole
        {
            GroupId = groupId,
            RoleId = roleId
        };
    }
}