using System.Net;

namespace Sharkable;

internal sealed class DefaultUnifiedResultFactory : IUnifiedResultFactory
{
    internal static readonly DefaultUnifiedResultFactory Instance = new();

    public IUnifiedResult Create(object? data, string? errorMessage, int statusCode)
    {
        return new UnifiedResult<object?>
        {
            StatusCode = (HttpStatusCode)statusCode,
            Data = data,
            ErrorMessage = errorMessage
        };
    }
}

internal static class UnifiedResultFactoryHelper
{
    /// <summary>
    /// Resolves the unified result factory with the documented precedence:
    /// 1) <see cref="SharkOption.UnifiedResultFactory"/> (the AGENTS.md
    /// factory-pattern extension point),
    /// 2) a DI-registered <see cref="IUnifiedResultFactory"/>,
    /// 3) the built-in default.
    /// </summary>
    internal static IUnifiedResultFactory ResolveFactory()
    {
        if (Shark.SharkOption.UnifiedResultFactory != null)
            return Shark.SharkOption.UnifiedResultFactory;

        var di = InternalShark.ServiceProvider?.GetService<IUnifiedResultFactory>();
        return di ?? DefaultUnifiedResultFactory.Instance;
    }
}
