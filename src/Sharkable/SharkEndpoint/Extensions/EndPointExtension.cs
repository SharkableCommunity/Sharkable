﻿using Microsoft.Extensions.Options;
using System.Linq.Expressions;

namespace Sharkable;

internal static class SharkEndPointExtension
{
    public static void MapEndpoints(this WebApplication? app)
    {
        AddAttributeEndpoints(Shark.Assemblies,  app);
        app.MapSharkEndpoints();
    }

    public static void MapEndpoints(this WebApplication? app, Assembly[] assemblies)
    {
        AddAttributeEndpoints(assemblies,  app);
        app.MapSharkEndpoints();
    }

    private static void AddAttributeEndpoints(Assembly[]? assemblies, WebApplication? app)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(assemblies);

        var lst = GetAttributeEndpoints(ref assemblies);
        lst.MyForEach(a =>
        {
            var group = app.MapGroup(a.Item1!);
            a.Item2.MyForEach(e =>
            {
                switch(e.Item2)
                {
                    case SharkHttpMethod.GET:
                        group.MapGet(e.Item1!, e.Item3);
                        break;
                    case SharkHttpMethod.POST:
                        group.MapPost(e.Item1!, e.Item3);
                        break;
                    case SharkHttpMethod.PUT:
                        group.MapPut(e.Item1!, e.Item3);
                        break;
                    case SharkHttpMethod.DELETE:
                        group.MapDelete(e.Item1!, e.Item3);
                        break;
                    case SharkHttpMethod.PATCH:
                        group.MapPatch(e.Item1!, e.Item3);
                        break;
                    default:
                        break;
                }
            });
        });
    }

    internal static WebApplication MapSharkEndpoints(this WebApplication? builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var endpointServices = builder.Services.GetServices<ISharkEndpoint>();
        var options = builder.Services.GetService<IOptions<SharkOption>>();
        
        ArgumentNullException.ThrowIfNull(options);
        
        endpointServices.MyForEach(e =>
        {
            SharkEndpoint sharkEndpoint;

            if(e is SharkEndpoint endpoint)
            {
                sharkEndpoint = endpoint;
                sharkEndpoint.BuildAction = endpoint.AddRoutes;
            }
            else
            {
                sharkEndpoint = CreateSharkEndpoint(e);
            }
            
            if (string.IsNullOrWhiteSpace(sharkEndpoint.apiPrefix))
                sharkEndpoint.apiPrefix = options.Value.ApiPrefix;

            string? groupName = null;

            if(sharkEndpoint.grouName != null)
            {
                groupName = sharkEndpoint.grouName.GetCaseFormat(options.Value.Format);
                sharkEndpoint.baseApiPath = $"{sharkEndpoint.apiPrefix}/{groupName}";
            }
            else
            {
                groupName = string.Empty;
            }
            
            if(string.IsNullOrWhiteSpace(sharkEndpoint.apiPrefix))
            {
                sharkEndpoint.BuildAction?.Invoke(builder);
            }
            else
            {
                if(string.IsNullOrWhiteSpace(groupName))
                {
                    sharkEndpoint.baseApiPath = sharkEndpoint.apiPrefix;
                }
                else
                {
                    sharkEndpoint.baseApiPath = $"{sharkEndpoint.apiPrefix}/{groupName}";
                }
                var group = builder.MapGroup(sharkEndpoint.baseApiPath);
                sharkEndpoint.BuildAction?.Invoke(group);
            }
            
        });
        return builder;
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

        var alist = assemblies.ToList();

        if (alist.Count == 0)
            return null;

        var endpoints = alist.Select(x => x.GetTypes()
                .Where(i => i.GetInterfaces()
                        .Where(x => x == typeof(ISharkEndpoint))
                        .Any()).ToList()).ToList();
        if (endpoints.Count == 0)
            return null;

        var lst = new List<Type>();

        endpoints.MyForEach(lst.AddRange);

        return lst;
    }

    [Obsolete("will not update above v0.0.5")]
    private static List<Tuple<string?, List<Tuple<string?, SharkHttpMethod, Delegate>>>>? GetAttributeEndpoints(ref Assembly[]? assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var lst = new List<Tuple<string?, List<Tuple<string?, SharkHttpMethod, Delegate>>>>();
        assemblies.MyForEach(a =>
        {
            a.GetTypes().MyForEach(t =>
            {
                var endpointAttrubute = t.GetCustomAttributes<SharkEndpointAttribute>()
                    .FirstOrDefault();
                var methods = new List<Tuple<string?, SharkHttpMethod, Delegate>>();

                if (endpointAttrubute == null)
                    return;

                endpointAttrubute.Group ??= t.Name.FormatAsGroupName();

                var taggedMethods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                   .Where(x => x.CustomAttributes
                       .Any(x => x.AttributeType == typeof(SharkMethodAttribute)))
                   .ToList();

                if (taggedMethods.Count == 0)
                    return;

                var instance = Activator.CreateInstance(t);
                taggedMethods.MyForEach(methodInfo =>
                {
                    //methods.Add(new Tuple<string?, SharkHttpMethod, Delegate>(methodAttribute.AddressName, methodAttribute.Method, methodDelegate));
                    var methodAttribute = methodInfo.GetCustomAttribute<SharkMethodAttribute>();

                    if (methodAttribute == null)
                        return;

                    //setup route address
                    if (string.IsNullOrWhiteSpace(methodAttribute.AddressName))
                        methodAttribute.AddressName ??= methodInfo.Name;

                    var parameters = methodInfo.GetParameters()
                                   .Select(p => p.ParameterType)
                                   .ToArray();

                    if (methodInfo.ReturnType == typeof(Task) || (methodInfo.ReturnType.IsGenericType && methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)))
                    {
                        var delegateType = Expression.GetDelegateType(parameters.Concat(new[] { methodInfo.ReturnType }).ToArray());
                        var del = methodInfo.CreateDelegate(delegateType, instance);
                        methods.Add(new Tuple<string?, SharkHttpMethod, Delegate>(methodAttribute.AddressName, methodAttribute.Method, del));
                    }
                    else if (methodInfo.ReturnType == typeof(void))
                    {
                        var delegateType = Expression.GetDelegateType(parameters.Concat(new[] { typeof(void) }).ToArray());
                        var del = methodInfo.CreateDelegate(delegateType, instance);
                        methods.Add(new Tuple<string?, SharkHttpMethod, Delegate>(methodAttribute.AddressName, methodAttribute.Method, del));
                    }
                });

                lst.Add(new Tuple<string?, List<Tuple<string?, SharkHttpMethod, Delegate>>>(endpointAttrubute?.Group, methods));
            });
        });

        return lst;
    }

    [Obsolete("due to aot incompatible, this method is not supported")]
    public static SharkEndpoint CreateSharkEndpointOld<T>(T shark, string? apiPrefix = "api") where T: ISharkEndpoint
    {
        var sharkEndpointType = typeof(SharkEndpoint);
        var instance = (SharkEndpoint)Activator.CreateInstance(sharkEndpointType, nonPublic: true)!;

        var grouNameField = sharkEndpointType.GetField("grouName", BindingFlags.Instance | BindingFlags.NonPublic);
        var apiPrefixField = sharkEndpointType.GetField("apiPrefix", BindingFlags.Instance | BindingFlags.NonPublic);
        var typeName = shark.GetType().Name;
        grouNameField?.SetValue(instance, typeName.FormatAsGroupName());
        apiPrefixField?.SetValue(instance, apiPrefix);
        var addRoutesMethod = typeof(T).GetMethod("AddRoutes");
        var addRoutesDelegate = (Action<IEndpointRouteBuilder>)Delegate.CreateDelegate(typeof(Action<IEndpointRouteBuilder>), shark, addRoutesMethod!);

        var addRoutesField = sharkEndpointType.GetField("AddRoutes", BindingFlags.Instance | BindingFlags.Public);
        addRoutesField?.SetValue(instance, addRoutesDelegate);
        return instance;
    }
    public static SharkEndpoint CreateSharkEndpoint<T>(T shark, string? apiPrefix = "api") where T : ISharkEndpoint
    {
        var sharkEndpointType = typeof(SharkEndpoint);
        var groupName = shark.GetType().Name.FormatAsGroupName();
        var instance = (SharkEndpoint)Activator.CreateInstance(sharkEndpointType, nonPublic: true)!;

        // Directly set fields using known properties or methods
        instance.grouName = groupName;
        instance.apiPrefix = apiPrefix;

        // Assign the delegate
        instance.BuildAction = shark.AddRoutes;

        return instance;
    }
}
