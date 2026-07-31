 
namespace Sharkable;

internal static class AutoCrudExtension
{
    /// <summary>
    /// Wires the SqlSugar AutoCrud generator when <c>ConfigureAutoCrud</c> is set.
    /// The generator lives in the companion <c>Sharkable.AutoCrud.SqlSugar</c>
    /// assembly, loaded lazily via reflection.
    /// </summary>
    [RequiresDynamicCode("AutoCrud loads the Sharkable.AutoCrud.SqlSugar companion assembly via reflection and is not supported under NativeAOT")]
    internal static IServiceCollection AddAutoCrud(this IServiceCollection services)
    {
        //only proceed when AutoCrud is configured
        var sqlSugarOptions = Shark.SharkOption.SqlSugarOptionsConfigure;
        if (sqlSugarOptions == null)
            return services;

        //get auto crud sqlsugar extensions
        //todo: will use regex extension to get all Sharkable.AutoCrud.* if more aot supported orms are comming out;
        var assembly = Shark.Assemblies?.FirstOrDefault(x=>x.GetName().Name!.Equals("Sharkable.AutoCrud.SqlSugar"));

        // Fallback: the assembly may not be loaded yet (lazy NuGet loading).
        // Trigger explicit load so the extension method is discoverable.
        if (assembly == null)
        {
            try
            {
                assembly = Assembly.Load("Sharkable.AutoCrud.SqlSugar");
            }
            catch
            {
                // BUG-120: warn loudly instead of silently disabling AutoCrud.
                // (Do not throw — NativeAOT publishes legitimately run without
                // the companion assembly; a loud warning keeps that visible.)
                Console.Error.WriteLine(
                    "[Sharkable] WARNING: ConfigureAutoCrud was set but the 'Sharkable.AutoCrud.SqlSugar' assembly could not be loaded. " +
                    "Add a PackageReference to Sharkable.AutoCrud.SqlSugar. Note: AutoCrud is not supported under NativeAOT.");
                return services;
            }
        }

        if(assembly != null)
        {
            var crudTypes = assembly.GetType("Sharkable.AutoCrud.SqlSugar.AutoCrudExtension");
            var method = crudTypes?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x => x.Name == "AddSqlSugar");

            if (method?.Invoke(null, [services, sqlSugarOptions]) is IServiceCollection s)
            {
                Utils.WriteDebug("auto crud generation added.");
                return s;
            }
        }
        Console.Error.WriteLine(
            "[Sharkable] WARNING: ConfigureAutoCrud was set but the 'Sharkable.AutoCrud.SqlSugar.AutoCrudExtension.AddSqlSugar' method could not be located. " +
            "Verify the Sharkable.AutoCrud.SqlSugar package version is compatible with this Sharkable version.");
        return services;
    }
}
