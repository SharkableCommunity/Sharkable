
namespace Sharkable;

/// <summary>
/// Singleton that holds the set of assemblies registered with Sharkable.
/// Used internally for endpoint discovery, service scanning, and validator registration.
/// </summary>
public sealed class AssemblyContext
{
    /// <summary>Assemblies currently registered with Sharkable.</summary>
    public static Assembly[]? Assemblies => Instance?.InternalAssemblies;
    /// <summary>Internal storage for registered assemblies.</summary>
    public Assembly[]? InternalAssemblies { get; internal set; }

    internal static AssemblyContext? Instance;

    private static readonly object locker = new();

    internal static void SetAssembly(Assembly[]? assemblies)
    {
        if(Instance != null)
            Instance.InternalAssemblies = assemblies;
    }
    /// <summary>Gets or creates the singleton <see cref="AssemblyContext"/> with the given assemblies.</summary>
    public static AssemblyContext GetAssemblyContext(Assembly[]? assemblies)
    {
        lock(locker)
        {
            if (Instance == null)
                Instance = new AssemblyContext(assemblies);
            else
                // BUG-140: a second AddShark/host in the same process must
                // observe the new assembly set — previously the parameter was
                // silently ignored once the singleton existed.
                Instance.InternalAssemblies = assemblies;
            return Instance;
        }
    }

    /// <summary>Gets the existing <see cref="AssemblyContext"/> or creates one from <see cref="Assemblies"/>.</summary>
    public static AssemblyContext GetAssemblyContext()
    {
        lock(locker)
        {
            Instance ??= new AssemblyContext(Assemblies);
            return Instance;
        }
    }

    internal AssemblyContext()
    {
        
    }
    /// <summary>Creates a context with the given set of assemblies.</summary>
    public AssemblyContext(Assembly[]? assemblies)
    {
        InternalAssemblies = assemblies;
    }
}
