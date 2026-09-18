using Boilerplate.BuildingBlocks.Web.Modules;
using System.Runtime.CompilerServices;

[assembly: AppModule(typeof(Boilerplate.Modules.Identity.IdentityModule), 100)]
[assembly: InternalsVisibleTo("Boilerplate.Identity.Tests")]