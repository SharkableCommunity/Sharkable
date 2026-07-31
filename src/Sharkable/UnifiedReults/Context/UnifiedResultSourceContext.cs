using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Sharkable;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for <see cref="UnifiedResult{T}"/> types.
/// Registered in the <c>TypeInfoResolverChain</c> to enable AOT-compatible serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UnifiedResult<string>))]
[JsonSerializable(typeof(UnifiedResult<int>))]
[JsonSerializable(typeof(UnifiedResult<long>))]
[JsonSerializable(typeof(UnifiedResult<double>))]
[JsonSerializable(typeof(UnifiedResult<bool>))]
[JsonSerializable(typeof(UnifiedResult<object?>))]
[JsonSerializable(typeof(IUnifiedResult))]
[JsonSerializable(typeof(System.Threading.Tasks.Task))]
[JsonSerializable(typeof(ValidationProblemDetails))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HealthCheckResponse))]
[JsonSerializable(typeof(HealthCheckEndpoint.LivenessResponse))]
[JsonSerializable(typeof(CronAdminEndpoint.CronJobView))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<CronAdminEndpoint.CronJobView>))]
[JsonSerializable(typeof(SharkProfilerEndpoint.ProfilerSnapshot))]
[JsonSerializable(typeof(SharkProfilerEndpoint.ProfilerSlowEntry))]
internal partial class UnifiedResultSourceContext : JsonSerializerContext
{
}
