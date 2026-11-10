using System.Text.Json.Serialization;
using Fyrd.Core;
using Fyrd.Core.Config;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Hub;
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
builder.Services.AddSingleton<OwnerProxy>();
builder.Services.AddSingleton<InboxStore>();
builder.Services.AddSingleton<ClaudeCredentials>();
builder.Services.AddSingleton<ICloudSessions>(_ => new CloudSessionClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, _.GetRequiredService<ClaudeCredentials>()));
builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
builder.Services.AddSingleton<SecretStore>();
// The token is read from GH_TOKEN (else GITHUB_TOKEN) and kept in memory only; a token saved in Settings overrides it.
builder.Services.AddSingleton(sp =>
{
    var token = GitHubToken.FromEnvironment();
    token.SetSaved(sp.GetRequiredService<SecretStore>().Get(SecretStore.GitHubToken));
    return token;
});
builder.Services.AddHttpClient("github", c =>
{
    c.BaseAddress = new Uri("https://api.github.com/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("fyrd");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<IGitHub>(sp =>
{
    var token = sp.GetRequiredService<GitHubToken>();
    var octokit = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("fyrd"));
    void Apply() => octokit.Credentials = token.Present ? new Octokit.Credentials(token.Value) : Octokit.Credentials.Anonymous;
    Apply();
    token.Changed += Apply;
    return new GitHubApi(token, sp.GetRequiredService<IHttpClientFactory>().CreateClient("github"), octokit, sp.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<GitHubSignIn>();
builder.Services.AddSingleton<IOwnerMessages, ClaudeCliOwnerMessages>();
builder.Services.AddSingleton<IFiles>(sp => new FilesApi(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, sp.GetRequiredService<ClaudeCredentials>()));
builder.Services.AddSingleton<UploadStore>();
builder.Services.AddSingleton<OwnedPullRequests>();
builder.Services.AddSingleton(sp => new StackMergeJobs(sp.GetRequiredService<IGitHub>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<OwnedPullRequests>(), sp.GetRequiredService<HubState>(),
    sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
builder.Services.AddSingleton<RunnerState>();
builder.Services.AddSingleton<SpecRunner>();
builder.Services.AddSingleton<LeadRelay>();
if (!builder.Configuration.GetValue<bool>("Fyrd:DisableRunner"))
    builder.Services.AddHostedService<SpecRunnerService>();
builder.Services.AddSingleton(sp => new HubPersonas(sp.GetRequiredService<FyrdPaths>(),
    Path.GetFullPath(builder.Configuration["Fyrd:HubPersonasSeedDir"]
        ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "..", "personas", "hub"))));
builder.Services.AddSingleton(_ => new HubSkills(Path.GetFullPath(builder.Configuration["Fyrd:HubSkillsDir"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "..", "skills", "controller"))));
builder.Services.AddSingleton<HubMemory>();
builder.Services.AddSingleton<PendingMemory>();
builder.Services.AddSingleton<ControllerManager>();
builder.Services.AddSingleton<HubState>();
builder.Services.AddSingleton<CostLedger>();
builder.Services.AddSingleton<CostGuard>();
builder.Services.AddSingleton<HubRunner>();
builder.Services.AddSingleton<SessionProgress>();
builder.Services.AddSingleton<SessionProgressWatcher>();
if (!builder.Configuration.GetValue<bool>("Fyrd:DisableRunner"))
{
    builder.Services.AddHostedService<HubRunnerService>();
    builder.Services.AddHostedService<GitHubSignInService>();
    builder.Services.AddHostedService<SessionProgressService>();
}
builder.Services.AddSingleton(sp =>
{
    var dir = Path.GetFullPath(builder.Configuration["Fyrd:PersonasDir"]
        ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "..", "personas"));
    try { return new PersonasSource(Personas.Load(dir), dir); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        sp.GetRequiredService<ILogger<PersonasSource>>().LogError("Personas not loaded from {Dir}: {Message}", dir, e.Message);
        return new PersonasSource(null, dir);
    }
});
builder.Services.AddSingleton(sp => new SpecLauncher(sp.GetRequiredService<SpecStore>(),
    sp.GetRequiredService<ConfigStore>(), sp.GetRequiredService<DecisionMemory>(),
    sp.GetRequiredService<ICloudSessions>(),
    sp.GetRequiredService<PersonasSource>().Value ?? throw new InvalidOperationException("Personas not loaded."),
    sp.GetRequiredService<OwnerProxy>()));

var app = builder.Build();

app.UseExceptionHandler(e => e.Run(async ctx =>
{
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    ctx.Response.ContentType = "application/problem+json";
    await ctx.Response.WriteAsJsonAsync(new { title = "Internal server error", status = 500 }, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
}));

app.UseSameOriginApiWrites();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapSpecs();
app.MapInbox();
app.MapConfig();
app.MapSecrets();
app.MapBoard();
app.MapMemory();
app.MapHub();
app.Map("/api/{**rest}", () => Results.Problem("Not found.", statusCode: StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;

internal sealed record PersonasSource(Personas? Value, string Dir);
