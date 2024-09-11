using SqlSugar;
namespace Microsoft.Extensions.DependencyInjection;

public static  class AddSampleDataServiceExtension
{
    public static void AddSampleDataService(this IServiceCollection services)
    {
        StaticConfig.EnableAot = true;
        var conf = new ConnectionConfig
        {
            IsAutoCloseConnection = true,
            DbType = DbType.Sqlite,
            ConnectionString = $"Data Source={AppContext.BaseDirectory}/test1.db",
            ConfigId = "cnf1"
        };
        SqlSugarScope sqlSugar = new(conf);

        services.AddSingleton<ISqlSugarClient>(sqlSugar);
       // services.AddScoped(typeof(SqlSugarRepository<>));
    }
}
