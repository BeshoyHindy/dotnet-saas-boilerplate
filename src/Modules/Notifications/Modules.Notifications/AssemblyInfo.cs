using System.Runtime.CompilerServices;
using Boilerplate.BuildingBlocks.Web.Modules;

[assembly: AppModule(typeof(Boilerplate.Modules.Notifications.NotificationsModule), 750)]
[assembly: InternalsVisibleTo("Boilerplate.Notifications.Tests")]
[assembly: InternalsVisibleTo("Boilerplate.Integration.Tests")]
