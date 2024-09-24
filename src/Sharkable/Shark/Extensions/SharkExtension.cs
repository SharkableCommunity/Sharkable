namespace Sharkable;

public static class SharkExtension
{
    internal static void AddCommon(this IServiceCollection services, Action<SharkOption>? setupOptions = null)
    {
        var option = new SharkOption();
        //invoke options
        setupOptions?.Invoke(option);
        //setup shark working mode
        services.Configure<SharkOption>((opt) => { opt = option; });
        //setup shark options
        Shark.SharkOption = option;
        //wire endpoints
        services.WireSharkEndpoint();
        //wire service lifelime
        services.AddServicesWithAttributeOfTypeFromAssembly(Shark.Assemblies);
        //setup swagger gen
        services.SharkSwagger();
        //setup auto crud services
        services.AddAutoCrud();
    }
    internal static void UseCommon(this WebApplication app, Action<UseSharkOptions>? setupOptions = null)
    {
        var opt = new UseSharkOptions();
        //invoke options
        setupOptions?.Invoke(opt);
        //configure internal shark 
        InternalShark.Configuration = app.Configuration;
        InternalShark.HostEnvironment = app.Environment;
        InternalShark.ServiceScopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
        InternalShark.ServiceProvider = InternalShark.ServiceScopeFactory.CreateScope().ServiceProvider;
        //setup swagger
        app.UseSharkSwagger();
    }
}
