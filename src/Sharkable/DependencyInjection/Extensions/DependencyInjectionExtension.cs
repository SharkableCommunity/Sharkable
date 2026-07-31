#pragma warning disable CS0618 // Internal use of legacy attribute-based endpoint system

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sharkable;

internal static class DependencyInjectionExtension
{
    internal static void AddDiFactory(this IServiceCollection services)
    {
        services.AddSingleton<IDependencyReflectorFactory, DependencyReflectorFactory>();
#pragma warning restore CS0618
        // BUG-124: TryAdd so a user-registered IUnifiedResultFactory (before
        // AddShark) wins instead of being shadowed by the framework default.
        services.TryAddSingleton<IUnifiedResultFactory, DefaultUnifiedResultFactory>();
    }
}
