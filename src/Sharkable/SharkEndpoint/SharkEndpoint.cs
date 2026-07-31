
namespace Sharkable;

/// <summary>
/// Mininal api endpoint
/// </summary>
[SuppressMessage("ReSharper", "InconsistentNaming")]
public class SharkEndpoint : ISharkEndpoint
{
    //internal string corsName;
    internal string? groupName;
    internal string? apiPrefix;
    internal string? baseApiPath;
    internal string? version;
    internal Action<IEndpointRouteBuilder>? BuildAction { get; set; }
    /// <summary>
    /// add routes to build endpoints
    /// </summary>
    public virtual void AddRoutes(IEndpointRouteBuilder app)
    {
    }

    /// <summary>Creates an endpoint group with the group name auto-derived from the class name.</summary>
    public SharkEndpoint()
    {
        var name = GetType().Name;
        groupName = name.FormatAsGroupName()!;
    }

    /// <summary>Creates an endpoint group with an explicit group name and API prefix.</summary>
    /// <param name="groupName">The URL segment for this endpoint group.</param>
    /// <param name="apiPrefix">Prefix prepended to all routes (default <c>"api"</c>).</param>
    public SharkEndpoint(string? groupName, string apiPrefix = "api")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        this.groupName = groupName;
        this.apiPrefix = apiPrefix;
    }
}
