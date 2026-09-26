using Boilerplate.BuildingBlocks.Web.Modules;
using System.Runtime.CompilerServices;

[assembly: AppModule(typeof(Boilerplate.Modules.Auditing.AuditingModule), 300)]
[assembly: InternalsVisibleTo("Boilerplate.Auditing.Tests")]