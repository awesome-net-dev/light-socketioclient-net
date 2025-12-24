using Light.SocketIoClient.Demo;
using Light.SocketIoClient.Net;
using Light.SocketIoClient.Net.DependencyInjection;
using Light.SocketIoClient.Net.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<SocketClientOptions>(builder.Configuration.GetSection("clientSocket"));

builder.Services.AddSocketClient();
builder.Services.AddSingleton<ISocketClientsSentinel, SocketClientsSentinel>();
builder.Services.AddHostedService<ISocketClientsSentinel>(sp => sp.GetRequiredService<ISocketClientsSentinel>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/broadcast", async context =>
{
    var client = context.RequestServices.GetRequiredService<ISocketIoClient>();
    await client.Connect(context.RequestAborted);
    await Task.Delay(500000);
    await context.Response.WriteAsJsonAsync(new { ok = "ok" });
}).WithName("Test-SocketIo-client").WithOpenApi();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast")
.WithOpenApi();

app.Run();

internal record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
