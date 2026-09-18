using System.Runtime.CompilerServices;
using Boilerplate.BuildingBlocks.Web.Modules;

[assembly: AppModule(typeof(Boilerplate.Modules.Chat.ChatModule), 800)]
[assembly: InternalsVisibleTo("Boilerplate.Chat.Tests")]
[assembly: InternalsVisibleTo("Boilerplate.Integration.Tests")]
