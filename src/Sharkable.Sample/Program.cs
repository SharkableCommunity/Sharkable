using System.Text.Json.Serialization;
using Sharkable.Sample;
using Microsoft.AspNetCore.Mvc;
using Sharkable;

var builder = WebApplication.CreateSlimBuilder(args);//.Sharkable([typeof(App).Assembly]);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(1, AppJsonSerializerContext2.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(2, AppJsonSerializerContext3.Default);
});

builder.Services.AddShark([typeof(App).Assembly], opt=>{
    opt.Format = EndpointFormat.SnakeCase;
});
builder.Services.AddSampleDataService();

var app = builder.Build();

var sampleTodos = new Todo[] {
            new(1, "Walk the dog",DateTime.Now),
            new(2, "Do the dishes", DateTime.Now),
            new(3, "Do the laundry", DateTime.Now.AddDays(1)),
            new(4, "Clean the bathroom",DateTime.Now),
            new(5, "Clean the car", DateTime.Now)
        };
app.AddMongoGroup();
app.UseShark();
var todosApi = app.MapGroup("/todos");
todosApi.MapGet("/init", async ([FromServices] IMonitor monitor) =>
{
    await monitor.InitUser();
});
todosApi.MapGet("/", () => sampleTodos);
todosApi.MapGet("/{id}", (int id, [FromServices] IMonitor monitor) =>
{
    var todo = monitor.GetTodo(id);
    return todo;
});

todosApi.MapGet("/love", ([FromServices]IMonitor monitor)=>
{
    monitor.Show();
});

app.Run();



[JsonSerializable(typeof(Todo[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
    
}

[JsonSerializable(typeof(List<TaskInfo>))]
internal partial class AppJsonSerializerContext2 : JsonSerializerContext
{

}

[JsonSerializable(typeof(TaskInterval))]
internal partial class AppJsonSerializerContext3 : JsonSerializerContext
{

}