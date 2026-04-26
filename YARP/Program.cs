var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "YARP Reverse Proxy",
    targets = new[]
    {
        "http://localhost:5066",
        "http://localhost:5067",
        "http://localhost:5068",
        "http://localhost:5069",
        "http://localhost:5070"
    }
}));

app.MapReverseProxy();

app.Run();
