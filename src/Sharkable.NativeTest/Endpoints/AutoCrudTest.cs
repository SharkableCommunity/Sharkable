using Sharkable;
using Sharkable.AutoCrud.SqlSugar;
using SqlSugar;

namespace Sharkable.NativeTest;

[SugarTable("test_items")]
public class TestItem
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [CrudAllow]
    public string Name { get; set; } = "";

    // DATA-04: tenant column — AutoCrud filters queries and force-fills writes
    // when EnableAutoCrudTenantFilter is on. Not [CrudAllow]: the generator
    // excludes it from client-controlled writes. Nullable so inserts succeed
    // even when the tenant filter is disabled.
    [SugarColumn(IsNullable = true)]
    public string? TenantId { get; set; }
}

public class AutoCrudTestEndpoint : ISharkEndpoint, IAutoCrudEntity<TestItem>
{
    // No AddRoutes needed — AutoCrud generates all routes
    // Table is created via CodeFirst in Program.cs
}
