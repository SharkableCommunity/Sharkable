# Sharkable
a dotnet minimal api framework collection \
\
[![Sharkable](https://img.shields.io/nuget/v/Sharkable.svg?color=red&style=flat-square)](https://www.nuget.org/packages/Sharkable/)
[![Sharkable](https://img.shields.io/nuget/dt/Sharkable.svg?style=flat-square)](https://www.nuget.org/packages/Sharkable/)
## Usage

### automatic dependency injection
```csharp
//first add extension
using Sharkable
builder.Services.AddShark();
//for aot users please specify assemblies by youself and avoid code trim
build.Services.AddShark([typeof(Program).Assembly]);

[ScopedService] //inject class as a scoped service by the given attribute
public class Monitor : IMonitor
{
    public void Show()
    {
        ...
    }
}

//map an endpoint and it works!
app.MapGet("/monitor",([FromServices]IMonitor monitor) =>
{
    monitor.Show();
});
```
### endpoint auto mapper (new style)
create a new class
```csharp
public class TestEndpoint : ISharkEndpoint
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("show", ()=>"test result");
    }
}

//will now generate a http get method with the url
// api/test/show
```

### endpoint auto mapper (old style)
create a new class
```csharp
[SharkEndpoint]
public class TestEndpoint
{
    [SharkMethod("show", SharkHttpMethod.GET)]
    public void Show()
    {
        ...
    }
}

//will now generate a http get method with the url
// api/test/show
```
for more use sample please see Sharkable.Sample project
