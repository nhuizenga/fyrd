using Fyrd.Core.Config;
using Fyrd.Core.Hub;

namespace Fyrd.Server.Endpoints;

/// <summary>The optional Anthropic API key and GitHub token. Responses say whether a value is set and where it comes from, never the value.</summary>
internal static class SecretEndpoints
{
    internal sealed record SecretView(string Name, bool Set, string Source);
    internal sealed record SecretsView(bool Supported, IReadOnlyList<SecretView> Secrets);
    internal sealed record SecretBody(string? Value);

    private static string? EnvFor(string name) => name switch
    {
        SecretStore.GitHubToken => Environment.GetEnvironmentVariable("GH_TOKEN") is { Length: > 0 } g ? g : Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
        SecretStore.AnthropicApiKey => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
        _ => null,
    };

    internal static SecretsView View(SecretStore store) => new(store.Supported, SecretStore.Names.Select(n =>
        store.Has(n) ? new SecretView(n, true, "saved")
        : string.IsNullOrWhiteSpace(EnvFor(n)) ? new SecretView(n, false, "none")
        : new SecretView(n, true, "env")).ToList());

    /// <summary>Applies the saved value (or its removal) to the live GitHub token and re-checks sign-in.</summary>
    internal static async Task ApplyAsync(SecretStore store, GitHubToken token, GitHubSignIn signIn, string name, CancellationToken ct)
    {
        if (name != SecretStore.GitHubToken) return;
        token.SetSaved(store.Get(name));
        await signIn.RefreshAsync(ct);
    }

    public static IEndpointRouteBuilder MapSecrets(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/secrets");

        group.MapGet("/", (SecretStore store) => Results.Ok(View(store)));

        group.MapPut("/{name}", (string name, SecretBody body, SecretStore store, GitHubToken token, GitHubSignIn signIn, CancellationToken ct) => ApiErrors.RunAsync(async () =>
        {
            if (!SecretStore.Names.Contains(name)) return Results.Problem("Unknown secret.", statusCode: StatusCodes.Status404NotFound);
            try { store.Set(name, body.Value ?? ""); }
            catch (SecretStoreException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest); }
            await ApplyAsync(store, token, signIn, name, ct);
            return Results.Ok(View(store));
        }));

        group.MapDelete("/{name}", (string name, SecretStore store, GitHubToken token, GitHubSignIn signIn, CancellationToken ct) => ApiErrors.RunAsync(async () =>
        {
            if (!SecretStore.Names.Contains(name)) return Results.Problem("Unknown secret.", statusCode: StatusCodes.Status404NotFound);
            store.Delete(name);
            await ApplyAsync(store, token, signIn, name, ct);
            return Results.Ok(View(store));
        }));

        return app;
    }
}
