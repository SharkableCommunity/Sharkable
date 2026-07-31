#pragma warning disable CS0618 // Internal use of legacy attribute-based endpoint system

using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace Sharkable;

internal static class SharkEndPointExtension
{
    public static void MapEndpoints(this WebApplication? app)
    {
        app.MapSharkEndpoints();
        
        //map shark endpoint attributes only when i non aot mode
        if (!Shark.SharkOption.AotMode)
        {
            MapAttributeEndpoints(Shark.Assemblies, app);
        }
    }

    internal static WebApplication MapSharkEndpoints(this WebApplication? app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var endpointServices = app.Services.GetServices<ISharkEndpoint>();
        // BUG-126: read from the single static options instance (the DI copy
        // was removed) so Format/ApiPrefix/RequireAuthenticatedByDefault can
        // never diverge from what the middleware sees.
        var options = Shark.SharkOption;
        ArgumentNullException.ThrowIfNull(options);

        // Phase 1: Collect all SharkEndpoint instances with metadata
        var collected = new List<(SharkEndpoint endpoint, Type classType)>();
        endpointServices.MyForEach(e =>
        {
            SharkEndpoint sharkEndpoint;

            if (e is SharkEndpoint endpoint)
            {
                sharkEndpoint = endpoint;
                sharkEndpoint.BuildAction = endpoint.AddRoutes;
            }
            else
            {
                sharkEndpoint = CreateSharkEndpoint(e, options.ApiPrefix);
            }

            var groupAttr = e.GetType().GetCustomAttribute<EndpointGroupAttribute>();
            if (groupAttr != null)
            {
                sharkEndpoint.groupName = groupAttr.Name;
            }

            var versionAttr = e.GetType().GetCustomAttribute<SharkVersionAttribute>();
            if (versionAttr != null)
            {
                sharkEndpoint.version = versionAttr.Version;
            }

            sharkEndpoint.version ??= options.DefaultApiVersion;

            // Only fill the default prefix when it was never set at all —
            // an explicit empty string (legacy [SharkEndpoint(ApiPrefix: null)])
            // must NOT be replaced with the configured default.
            sharkEndpoint.apiPrefix ??= options.ApiPrefix;

            collected.Add((sharkEndpoint, e.GetType()));
        });

        // Phase 2: Group by (version, groupName) tuple
        var grouped = new Dictionary<string, List<(SharkEndpoint, Type)>>();
        collected.MyForEach(item =>
        {
            var version = item.endpoint.version?.GetCaseFormat(options.Format);
            var groupName = item.endpoint.groupName?.GetCaseFormat(options.Format) ?? string.Empty;
            var key = string.IsNullOrWhiteSpace(version) ? groupName : $"{version}_{groupName}";
            if (!grouped.ContainsKey(key))
                grouped[key] = [];
            grouped[key].Add(item);
        });

        // Phase 3: One MapGroup per unique group name. Every endpoint routes
        // through a group — even with an empty apiPrefix — so the shared
        // filters (auto-wrap, validation, API key, authz interceptor) and
        // conventions always apply (BUG-105).
        foreach (var (groupName, endpoints) in grouped)
        {
            var first = endpoints.First().Item1;

            var version = first.version?.GetCaseFormat(options.Format);
            var basePath = first.apiPrefix ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(version))
                basePath = $"{basePath}/{version}";
            var groupNameForUrl = first.groupName?.GetCaseFormat(options.Format) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(groupNameForUrl))
                basePath = $"{basePath}/{groupNameForUrl}";

            var group = app.MapGroup(basePath).WithDisplayName(groupName);

            // User-defined group convention
            Shark.SharkOption.GroupConvention?.Invoke(group, groupName);

            // Shared filters (once per group)
            var autoWrap = Shark.UseSharkOptions?.EnableAutoWrap ?? Shark.SharkOption.EnableAutoWrap;
            if (autoWrap)
                group.AddEndpointFilter<UnifiedResultWrapFilter>();

            if (Shark.SharkOption.EnableValidation)
                group.AddEndpointFilter<ValidationFilter>();

            if (Shark.SharkOption.ApiKeys?.Length > 0 && Shark.SharkOption.AuthorizationInterceptorFactory == null)
                group.AddEndpointFilter<ApiKeyFilter>();

            if (Shark.SharkOption.AuthorizationInterceptorFactory != null)
                group.AddEndpointFilter<AuthorizationInterceptorFilter>();

            // Resolve tags + class-level metadata from all endpoint types in this group
            var tags = ResolveGroupTags(endpoints, groupName);
            var summary = ResolveGroupSummary(endpoints);
            var description = ResolveGroupDescription(endpoints);
            var responseTypes = ResolveGroupResponseTypes(endpoints);
            var isDeprecated = ResolveGroupIsDeprecated(endpoints);
            var cacheProfile = ResolveGroupCacheProfile(endpoints);
            var rateLimitMetadata = ResolveGroupRateLimit(endpoints);

            if (cacheProfile != null)
                group.AddEndpointFilter<SharkCacheProfileFilter>();

            // Auto-Tags + OperationId + class-level metadata via Add() convention
            var capturedGroupName = !string.IsNullOrWhiteSpace(version) ? $"{version}_{groupName}" : groupName;
            var capturedBasePath = basePath;
            var capturedTags = tags;
            var capturedSummary = summary;
            var capturedDescription = description;
            var capturedResponseTypes = responseTypes;
            var capturedIsDeprecated = isDeprecated;
            ((IEndpointConventionBuilder)group).Add(builder =>
            {
                // Apply route pattern formatting based on SharkOption.Format
                var routeFormat = options.Format;
                if (routeFormat != EndpointFormat.UnChanged && builder is RouteEndpointBuilder routeBuilder)
                {
                    var rawText = routeBuilder.RoutePattern?.RawText;
                    if (!string.IsNullOrEmpty(rawText))
                    {
                        var segments = rawText.Split('/');
                        var changed = false;
                        for (var i = 0; i < segments.Length; i++)
                        {
                            if (!segments[i].Contains('{') && !segments[i].Contains('}'))
                            {
                                var formatted = segments[i].GetCaseFormat(routeFormat);
                                if (!string.Equals(formatted, segments[i]))
                                {
                                    segments[i] = formatted ?? segments[i];
                                    changed = true;
                                }
                            }
                        }
                        if (changed)
                        {
                            routeBuilder.RoutePattern = RoutePatternFactory.Parse(string.Join("/", segments));
                        }
                    }
                }

                // Auto-RequireAuthorization when RequireAuthenticatedByDefault is enabled
                if (options.RequireAuthenticatedByDefault
                    && !builder.Metadata.Any(m => m is IAuthorizeData or IAllowAnonymous))
                {
                    builder.Metadata.Add(new AuthorizeAttribute());
                }

                if (!builder.Metadata.Any(m => m is ITagsMetadata) && capturedTags.Count != 0)
                    builder.Metadata.Add(new TagsAttribute([.. capturedTags]));

                if (capturedSummary != null && !builder.Metadata.Any(m => m is EndpointSummaryAttribute))
                    builder.Metadata.Add(new EndpointSummaryAttribute(capturedSummary));

                if (capturedDescription != null && !builder.Metadata.Any(m => m is EndpointDescriptionAttribute))
                    builder.Metadata.Add(new EndpointDescriptionAttribute(capturedDescription));

                foreach (var rt in capturedResponseTypes)
                {
                    if (!builder.Metadata.Any(m => m is IProducesResponseTypeMetadata pm && pm.StatusCode == rt.StatusCode))
                        builder.Metadata.Add(rt);
                }

                if (capturedIsDeprecated && !builder.Metadata.Any(m => m is ObsoleteAttribute))
                    builder.Metadata.Add(new ObsoleteAttribute("This endpoint is deprecated."));

                if (cacheProfile != null && !builder.Metadata.Any(m => m is SharkCacheProfileAttribute))
                    builder.Metadata.Add(cacheProfile);

                if (rateLimitMetadata != null && !builder.Metadata.Any(m => m is SharkRateLimitMetadata))
                    builder.Metadata.Add(rateLimitMetadata);

                if (!builder.Metadata.Any(m => m is EndpointNameMetadata))
                {
                    var routePattern = (builder as RouteEndpointBuilder)?.RoutePattern?.RawText ?? string.Empty;
                    var httpMethod = builder.Metadata
                        .OfType<HttpMethodMetadata>()
                        .FirstOrDefault()?.HttpMethods?.FirstOrDefault() ?? "Unknown";

                    var relativePath = routePattern;
                    if (!string.IsNullOrEmpty(capturedBasePath) && relativePath.StartsWith(capturedBasePath + "/"))
                        relativePath = relativePath[(capturedBasePath.Length + 1)..];

                    var opId = $"{capturedGroupName}_{httpMethod}_{relativePath}"
                        .Replace('/', '_')
                        .Replace('-', '_')
                        .Replace(' ', '_');
                    builder.Metadata.Add(new EndpointNameMetadata(opId));
                }
            });

            // Per-endpoint-class routing: AutoCrud (generated before user routes)
            // and user-defined AddRoutes. Class-level opt-outs are expressed as
            // endpoint metadata — ASP.NET Core nested groups inherit parent-group
            // filters, so a MapGroup("") subgroup cannot exclude them (BUG-106).
            var crudGenerator = app.Services.GetService<IAutoCrudGenerator>();
            foreach (var (endpoint, classType) in endpoints)
            {
                // Class-level opt-outs are expressed as endpoint metadata on a
                // per-class subgroup so they never leak to sibling classes that
                // share the same [EndpointGroup] (BUG-106/audit nit). The
                // subgroup inherits the parent group's filters — that is
                // intended; the metadata is what opts a class out.
                var classGroup = group.MapGroup("");
                if (autoWrap && classType.GetCustomAttribute<SharkDontWrapAttribute>() != null)
                    classGroup.WithMetadata(new DisableAutoWrapMetadata());
                if (classType.GetCustomAttribute<SharkNoIdempotencyAttribute>() != null)
                    classGroup.WithMetadata(new NoIdempotencyMetadata());
                var idempotent = classType.GetCustomAttribute<SharkIdempotentAttribute>();
                if (idempotent != null)
                    classGroup.WithMetadata(new SharkIdempotentMetadata(idempotent.TtlSeconds));

                // User-defined endpoint convention
                Shark.SharkOption.EndpointConvention?.Invoke(classGroup, classType);

                if (crudGenerator != null)
                {
                    var entityInterface = classType.GetInterfaces()
                        .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAutoCrudEntity<>));
                    if (entityInterface != null)
                    {
                        var entityType = entityInterface.GetGenericArguments()[0];
                        // BUG-132: reuse the DI-resolved instance instead of
                        // Activator.CreateInstance — no constructor side effects,
                        // no AOT trim risk.
                        var operations = (endpoint as IAutoCrudEntityMarker)?.GetOperations() ?? CrudOperations.All;

                        crudGenerator.GenerateRoutes(classGroup, entityType, classType, operations);
                    }
                }

                // Add user-defined routes (after AutoCrud so overrides take precedence)
                endpoint.BuildAction?.Invoke(classGroup);
            }
        }

        // health check endpoint
        if (Shark.SharkOption.EnableHealthChecks)
        {
            HealthCheckEndpoint.Map(app);
            HealthCheckEndpoint.MapLiveness(app);
        }

        return app;
    }

    private static List<string> ResolveGroupTags(List<(SharkEndpoint, Type)> endpoints, string defaultTag)
    {
        var tags = endpoints
            .SelectMany(ep => ep.Item2.GetCustomAttributes<SharkTagAttribute>())
            .Select(attr => attr.Tag)
            .Distinct()
            .ToList();

        if (tags.Count == 0)
            tags.Add(defaultTag);

        return tags;
    }

    private static string? ResolveGroupSummary(List<(SharkEndpoint, Type)> endpoints)
    {
        return endpoints
            .Select(ep => ep.Item2.GetCustomAttribute<SharkDescriptionAttribute>()?.Summary)
            .FirstOrDefault(s => s != null);
    }

    private static string? ResolveGroupDescription(List<(SharkEndpoint, Type)> endpoints)
    {
        return endpoints
            .Select(ep => ep.Item2.GetCustomAttribute<SharkDescriptionAttribute>()?.Description)
            .FirstOrDefault(d => d != null);
    }

    private static List<IProducesResponseTypeMetadata> ResolveGroupResponseTypes(List<(SharkEndpoint, Type)> endpoints)
    {
        return endpoints
            .SelectMany(ep => ep.Item2.GetCustomAttributes<SharkResponseTypeAttribute>())
            .Select(attr => (IProducesResponseTypeMetadata)new SharkResponseMetadata
            {
                StatusCode = attr.StatusCode,
                Type = attr.ResponseType,
                ContentTypes = ["application/json"],
            })
            .ToList();
    }

    private static bool ResolveGroupIsDeprecated(List<(SharkEndpoint, Type)> endpoints)
    {
        return endpoints.Any(ep =>
            ep.Item2.GetCustomAttribute<SharkDeprecatedAttribute>() != null);
    }

    private static SharkCacheProfileAttribute? ResolveGroupCacheProfile(List<(SharkEndpoint, Type)> endpoints)
    {
        return endpoints
            .Select(ep => ep.Item2.GetCustomAttribute<SharkCacheProfileAttribute>())
            .FirstOrDefault(a => a != null);
    }

    private static SharkRateLimitMetadata? ResolveGroupRateLimit(List<(SharkEndpoint, Type)> endpoints)
    {
        var attr = endpoints
            .Select(ep => ep.Item2.GetCustomAttribute<SharkRateLimitAttribute>())
            .FirstOrDefault(a => a != null);

        if (attr == null)
            return null;

        return new SharkRateLimitMetadata(attr.Limit, TimeSpan.FromSeconds(attr.WindowSeconds));
    }

    internal static void WireSharkEndpoint(this IServiceCollection services)
    {
        var endpoints = GetSharkEndpint(Shark.Assemblies);

        endpoints.MyForEach(e =>
        {
            Utils.WriteDebug($"wiring {e.FullName}");
            services.AddSingleton(typeof(ISharkEndpoint), e);
        });
    }

    private static List<Type>? GetSharkEndpint(Assembly[]? assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var assemblyList = assemblies.ToList();

        if (assemblyList.Count == 0)
            return null;

        var endpoints = assemblyList.Select(x => x.GetTypes()
            .Where(i => i
                .GetInterfaces()
                .Any(type => type == typeof(ISharkEndpoint))).ToList()).ToList();
        if (endpoints.Count == 0)
            return null;

        var lst = new List<Type>();

        endpoints.MyForEach(lst.AddRange);

        return lst;
    }

    private static SharkEndpoint CreateSharkEndpoint<T>(T shark, string? apiPrefix = "api") where T : ISharkEndpoint
    {
        var sharkAttribute = shark.GetType().GetCustomAttribute<SharkEndpointAttribute>();
        var instance = new SharkEndpoint();

        if (sharkAttribute != null)
        {
            instance.groupName = sharkAttribute.Group;
            instance.version = sharkAttribute.Version;
            // BUG-131/145: honor the attribute's prefix contract. Explicit
            // ApiPrefix: null omits the prefix entirely; an explicit non-empty
            // value wins; otherwise fall back to the configured default.
            if (string.IsNullOrWhiteSpace(sharkAttribute.ApiPrefix))
            {
                instance.apiPrefix = string.Empty;
            }
            else
            {
                instance.apiPrefix = sharkAttribute.ApiPrefix;
            }
        }
        else
        {
            // Directly set fields using known properties or methods
            instance.groupName = shark.GetType().Name.FormatAsGroupName();
            instance.apiPrefix = apiPrefix;
        }

        var endpointGroupAttr = shark.GetType().GetCustomAttribute<EndpointGroupAttribute>();
        if (endpointGroupAttr != null)
        {
            instance.groupName = endpointGroupAttr.Name;
            instance.apiPrefix = apiPrefix;
        }

        var versionAttr = shark.GetType().GetCustomAttribute<SharkVersionAttribute>();
        if (versionAttr != null)
        {
            instance.version = versionAttr.Version;
        }

        // Assign the delegate
        instance.BuildAction = shark.AddRoutes;

        return instance;
    }
    private static JsonSerializerOptions? ResolveSerializerOptions(HttpContext ctx)
    {
        // Attempt to resolve options from DI then fallback to default options
        return ctx.RequestServices.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value?.SerializerOptions;
    }
    [RequiresDynamicCode("https://github.com/SharkableIO/Sharkable/issues/53")]
    private static void MapAttributeEndpoints(
        Assembly[]? assemblies, WebApplication? app)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(app);

        assemblies.MyForEach(a =>
        {
            a.GetTypes().MyForEach(t =>
            {
                var endpointAttribute = t.GetCustomAttributes<SharkEndpointAttribute>()
                    .FirstOrDefault();
                
                if (endpointAttribute == null)
                    return;

                var groupName = endpointAttribute.Group ?? t.Name;
                var formattedGroupName = groupName
                    .FormatAsGroupName()
                    .GetCaseFormat(Shark.SharkOption.Format)
                    .GetVersionFormat();
                
                var taggedMethods = t
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(x => !x.GetCustomAttributes<NonActionAttribute>().Any())
                    .ToList();

                if (taggedMethods.Count == 0)
                    return;
                var factory = app.Services.GetService<IDependencyReflectorFactory>();
                var instance = factory?.CreateInstance(t) ??
                               throw new InvalidOperationException($"error when creating an instance of {t.Name}");
                    
                var group = app.MapGroup(formattedGroupName!);

                // Resolve tags for this endpoint class
                var tagAttrs = t.GetCustomAttributes<SharkTagAttribute>();
                var tags = tagAttrs.Any()
                    ? tagAttrs.Select(a => a.Tag).ToArray()
                    : [formattedGroupName!];
                ((RouteGroupBuilder)group).WithTags(tags);

                taggedMethods.MyForEach(methodInfo =>
                {
                    //methods.Add(new Tuple<string?, SharkHttpMethod, Delegate>(methodAttribute.AddressName, methodAttribute.Method, methodDelegate));
                    var attribute = methodInfo.GetCustomAttribute<SharkMethodAttribute>();
                    var methodAttribute = attribute ?? new SharkMethodAttribute();

                    //setup route address
                    var methodPattern = methodAttribute.Pattern ?? methodInfo.Name;
                    var formattedMethodPattern = methodPattern
                        .GetCaseFormat(Shark.SharkOption.Format)
                        .GetVersionFormat()!;

                    var methodDelegate = instance.GetDelegate(methodInfo);
                    if (methodDelegate == null)
                        return;

                    group.MapMethods(formattedMethodPattern!, [methodAttribute.Method.ToString()], methodDelegate)
                         .WithMetadata(new EndpointNameMetadata($"{t.Name}_{methodInfo.Name}"));
                });
            });
        });
    }
}
