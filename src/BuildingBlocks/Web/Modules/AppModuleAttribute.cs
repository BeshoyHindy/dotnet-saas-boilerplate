using System;

namespace Boilerplate.BuildingBlocks.Web.Modules;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class AppModuleAttribute : Attribute
{
    public Type ModuleType { get; }

    /// <summary>
    /// Optional ordering hint that allows hosts to control module startup sequencing.
    /// Lower numbers execute first. The platform modules the template ships use values below 1000;
    /// a product module takes 1000 and up, in steps of 100 (1000, 1100, 1200, ...), so it always
    /// starts after the platform modules it depends on.
    /// </summary>
    public int Order { get; }

    public AppModuleAttribute(Type moduleType, int order = 0)
    {
        ModuleType = moduleType ?? throw new ArgumentNullException(nameof(moduleType));
        Order = order;
    }
}