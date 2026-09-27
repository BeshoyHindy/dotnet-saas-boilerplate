using System.Reflection;
using Boilerplate.Modules.Auditing;
using Boilerplate.Modules.Files;
using Boilerplate.Modules.Identity;
using Boilerplate.Modules.Multitenancy;
using Boilerplate.Modules.Notifications;

namespace Boilerplate.Api;

/// <summary>
/// The modules this host loads, handed to <c>AddModules</c> in <c>Program.cs</c>. A static rather
/// than a local so <c>HostModuleListTests</c> (Architecture.Tests) can compare it with every
/// assembly carrying <c>[AppModule]</c>: a module missing here fails that test instead of silently
/// never loading. The mediator list in <c>Program.cs</c> is the other list to edit.
/// </summary>
internal static class HostModules
{
    public static IReadOnlyList<Assembly> All { get; } =
    [
        typeof(IdentityModule).Assembly,
        typeof(MultitenancyModule).Assembly,
        typeof(AuditingModule).Assembly,
        typeof(FilesModule).Assembly,
        typeof(NotificationsModule).Assembly,
    ];
}
