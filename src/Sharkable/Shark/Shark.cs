using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Sharkable;

/// <summary>
/// basic sharkable implements and properties
/// </summary>
public sealed partial class Shark
{
    //private fields

    //private|inernal statics

    internal static readonly object condition = new();
    //internal IServiceProvider services { get => GetApp(_factory); }
    //public statics
    /// <summary>
    /// Sharkable asseblies list for the application entry
    /// </summary>
    public static Assembly[]? Assemblies => AssemblyContext.Assemblies;
    /// <summary>The current <see cref="IHostEnvironment"/> from the application.</summary>
    public static IHostEnvironment HostEnvironment => InternalShark.HostEnvironment;
    /// <summary>The application-level <see cref="IServiceProvider"/>.</summary>
    public static IServiceProvider Services => InternalShark.ServiceProvider;
    /// <summary>The current <see cref="IWebHostEnvironment"/> from the application.</summary>
    public static IWebHostEnvironment WebHostEnvironment => InternalShark.WebHostEnvironment;
    /// <summary>The application <see cref="IConfiguration"/>.</summary>
    public static IConfiguration Configuration => InternalShark.Configuration;
    //public static HttpContext HttpContext { get; }
    /// <summary>
    /// Assembly context of application entry
    /// </summary>
    public static AssemblyContext? Context => AssemblyContext.Instance;
    /// <summary>The application-level <see cref="IServiceScopeFactory"/>.</summary>
    public static IServiceScopeFactory ServiceScopeFactory => InternalShark.ServiceScopeFactory;
    /// <summary>
    /// Sharkable options
    /// </summary>
    public static SharkOption SharkOption { get; internal set; } = new SharkOption();
    /// <summary>Runtime options configured via <c>app.UseShark(opt => ...)</c>.</summary>
    public static UseSharkOptions? UseSharkOptions { get; internal set; }
    //public properties

    /// <summary>Gets the application service provider.</summary>
    public static IServiceProvider GetServiceProvider(Type? serviceType = null)
    {
        return Services;
    }

    /// <summary>Sets the assemblies registered with Sharkable. Called during <c>AddShark()</c>.</summary>
    /// <remarks>Legacy spelling retained for binary compatibility — prefer <see cref="SetAssembly"/>.</remarks>
    public static void SetAssebly(Assembly[]? assemblies)
    {
        SetAssembly(assemblies);
    }

    /// <summary>Sets the assemblies registered with Sharkable. Called during <c>AddShark()</c>.</summary>
    public static void SetAssembly(Assembly[]? assemblies)
    {
        AssemblyContext.GetAssemblyContext(assemblies);
    }
    /// <summary>
    /// Gets a service of type <typeparamref name="T"/> from the application service provider.
    /// </summary>
    /// <param name="serviceProvider">Optional service provider. Defaults to the application's root provider.</param>
    /// <typeparam name="T">The service type to resolve.</typeparam>
    /// <returns>The service instance, or <c>null</c> if not registered.</returns>
    public static T? GetService<T>(IServiceProvider? serviceProvider = null) where T : class
    {
        var service = GetService(typeof(T), serviceProvider) as T;
        return service;
    }
    /// <summary>
    /// get services
    /// </summary>
    /// <param name="type">service type</param>
    /// <param name="serviceProvider">service provider</param>
    /// <returns>object of instance</returns>
    public static object? GetService(Type type, IServiceProvider? serviceProvider = null)
    {
        return (serviceProvider ?? GetServiceProvider(type)).GetService(type);
    }
    /// <summary>
    /// get keyed services
    /// </summary>
    /// <param name="serviceProvider">service provider</param>
    /// <param name="key">service key</param>
    /// <typeparam name="T">service type</typeparam>
    /// <returns>instance of service</returns>
    public static T? GetKeyedService<T>(string? key = null, IServiceProvider? serviceProvider = null) where T : class
    {
        return string.IsNullOrWhiteSpace(key) ? 
            null : (serviceProvider ?? GetServiceProvider(typeof(T))).GetKeyedService<T>(key);
    }
    /// <summary>
    /// Gets the current value of an <see cref="IOptions{TOptions}"/> from DI.
    /// </summary>
    /// <param name="serviceProvider">Optional service provider. Defaults to the application's root provider.</param>
    /// <typeparam name="TOptions">The options type to resolve.</typeparam>
    /// <returns>The options value, or <c>null</c> if not registered.</returns>
    public static TOptions? GetOptions<TOptions>(IServiceProvider? serviceProvider = null) where TOptions : class,new()
    {
        return GetService<IOptions<TOptions>>(serviceProvider)?.Value;
    }
}