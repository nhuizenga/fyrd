using System.Text.Json.Serialization;
using Fyrd.Core;
using Fyrd.Core.Config;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Orchestration;
using Fyrd.Core.Specs;
using Fyrd.Server;
using Fyrd.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddProblemDetails();

// The stores serialize writes with per-instance locks, so each must be a singleton.
builder.Services.AddSingleton(FyrdPaths.Resolve(builder.Configuration["Fyrd:Home"]));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<SpecStore>();
builder.Services.AddSingleton<DecisionMemory>();
builder.Services.AddSingleton<InboxStore>();
builder.Services.AddSingleton<ClaudeCredentials>();
builder.Services.AddSingleton<ICloudSessions>(_ => new CloudSessionClient(new HttpClient(), _.GetRequiredService<ClaudeCredentials>()));
builder.Services.AddSingleton<RunnerState>();
builder.Services.AddSingleton<SpecRunner>();
builder.Services.AddHostedService<SpecRunnerService>();

var app = builder.Build();

app.UseSameOriginApiWrites();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapSpecs();
app.MapInbox();
app.MapConfig();
app.Map("/api/{**rest}", () => Results.Problem("Not found.", statusCode: StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
