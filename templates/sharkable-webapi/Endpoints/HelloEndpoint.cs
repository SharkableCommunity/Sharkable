using Sharkable;

namespace SharkableWebApi;

/// <summary>
/// Example endpoint — URL becomes <c>/api/hello</c> (class name prefix,
/// <c>Endpoint</c> suffix stripped, camelCase).
/// </summary>
public class HelloEndpoint : ISharkEndpoint
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        // Group prefix comes from the class name: "hello" → /api/hello.
        app.MapGet("", () => new HelloResponse("Hello from Sharkable!"))
            .WithSummary("Returns a greeting");
    }
}

/// <summary>Greeting payload.</summary>
public sealed record HelloResponse(string Message);
