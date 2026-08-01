using Sharkable;

var builder = WebApplication.CreateBuilder(args);

// ── Sharkable ─────────────────────────────────────────────
// AddShark: service registration, endpoint discovery, OpenAPI.
// Pass the app assembly explicitly for NativeAOT publish.
builder.Services.AddShark([typeof(Program).Assembly], opt =>
{
    opt.EnableAutoWrap = true;        // wrap plain returns in UnifiedResult<T>
    opt.EnableHealthChecks = true;    // /healthz readiness + /livez liveness
    opt.EnableIdempotency = true;     // Idempotency-Key support
});

var app = builder.Build();

// UseShark: middleware pipeline + endpoint mapping + Scalar UI
// (OpenAPI at /openapi/v1.json, Scalar at /scalar/v1)
app.UseShark();

app.Run();

// Declared for NativeAOT publishing (source-generated JSON).
// Add your endpoint request/response types here.
[System.Text.Json.Serialization.JsonSerializable(typeof(SharkableWebApi.HelloResponse))]
public partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
