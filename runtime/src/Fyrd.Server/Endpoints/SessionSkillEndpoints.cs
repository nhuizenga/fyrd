using Fyrd.Core.Config;
using Fyrd.Core.Hub;

namespace Fyrd.Server.Endpoints;

/// <summary>
/// The console's session skill API: the skills themselves, the setup script that installs the fyrd plugin, and the
/// owner's confirmation that the script is pasted into the environment Fyrd uses.
/// </summary>
internal static class SessionSkillEndpoints
{
    private const string GuideFile = "session-skills.md";

    private static string DocsDir(IConfiguration configuration, IWebHostEnvironment environment) =>
        Path.GetFullPath(configuration["Fyrd:DocsDir"] ?? ServerLayout.DefaultDir(environment.ContentRootPath, AppContext.BaseDirectory, "docs"));

    public static IEndpointRouteBuilder MapSessionSkills(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hub/session-skills");

        group.MapGet("/", (SessionSkills skills) => ApiErrors.Run(() => Results.Ok(skills.List())));

        group.MapGet("/setup-script", (SessionSkills skills, ConfigStore config) => ApiErrors.Run(() => Results.Ok(State(skills, config.Load()))));

        // The owner says the script is pasted. The script is the same for any skills, so there is nothing to compare.
        group.MapPost("/setup-script/confirm", (SessionSkills skills, ConfigStore config) => ApiErrors.Run(() =>
            Results.Ok(State(skills, config.Update(c => c with { SessionSkillsPlugin = true })))));

        group.MapDelete("/setup-script/confirm", (SessionSkills skills, ConfigStore config) => ApiErrors.Run(() =>
            Results.Ok(State(skills, config.Update(c => c with { SessionSkillsPlugin = false })))));

        // The onboarding guide, read from the repo's docs so the console shows the text that ships with this version.
        group.MapGet("/guide", (IConfiguration configuration, IWebHostEnvironment environment) =>
        {
            var file = Path.Combine(DocsDir(configuration, environment), GuideFile);
            return File.Exists(file)
                ? Results.Ok(new { text = File.ReadAllText(file) })
                : Results.Problem($"The guide is not on this computer (docs/{GuideFile}).", statusCode: StatusCodes.Status404NotFound);
        });

        group.MapGet("/{name}", (string name, SessionSkills skills) => ApiErrors.Run(() =>
            skills.Get(name) is { } s ? Results.Ok(s) : NotFound(name)));

        group.MapPut("/", (SessionSkill? body, SessionSkills skills, HubPersonas personas) => Save(null, body, skills, personas));
        group.MapPut("/{name}", (string name, SessionSkill? body, SessionSkills skills, HubPersonas personas) => Save(name, body, skills, personas));

        group.MapPost("/{name}/reset", (string name, SessionSkills skills) => ApiErrors.Run(() => Results.Ok(skills.Reset(name))));

        group.MapDelete("/{name}", (string name, SessionSkills skills) => ApiErrors.Run(() =>
            skills.Delete(name) ? Results.NoContent() : NotFound(name)));

        // What a session of this role is given as its persona text right now: the stored persona with its session skills,
        // as lines or as whole bodies.
        app.MapGet("/api/hub/personas/{id}/prompt", (string id, HubPersonas personas, SessionSkills skills, ConfigStore config) => ApiErrors.Run(() =>
        {
            if (personas.Get(id) is not { } persona) return Results.Problem($"Persona '{id}' not found.", statusCode: StatusCodes.Status404NotFound);
            var list = skills.List();
            var mode = SessionSkills.Mode(config.Load());
            var text = SessionSkills.Compose(persona, list, mode == SessionSkillMode.Installed);
            return Results.Ok(new { id = persona.Id, mode = Name(mode), words = SessionSkills.WordCount(text), text });
        }));

        return app;
    }

    private static object State(SessionSkills skills, FyrdConfig config)
    {
        var list = skills.List();
        return new
        {
            mode = Name(SessionSkills.Mode(config)),
            confirmed = config.SessionSkillsPlugin,
            environmentId = config.EnvironmentId,
            skills = list.Select(s => s.Name),
            // The skills the plugin ships as they are here; a session with the plugin loads these by their plugin name.
            pluginSkills = list.Where(s => s.InPlugin).Select(s => SessionSkillScript.PluginSkillName(s.Name)),
            // Skills edited or added in the console: the plugin doesn't hold this text, so sessions always get it inline.
            inlineSkills = list.Where(s => !s.InPlugin).Select(s => s.Name),
            // Defaults the owner deleted: POST /{name}/reset brings one back.
            missingDefaults = skills.MissingSeeds(),
            script = SessionSkillScript.Render(),
            repository = SessionSkillScript.Repository,
        };
    }

    private static string Name(SessionSkillMode mode) => mode switch
    {
        SessionSkillMode.Installed => "installed",
        _ => "inline",
    };

    private static IResult Save(string? name, SessionSkill? body, SessionSkills skills, HubPersonas personas) => ApiErrors.Run(() =>
    {
        if (body is null) return Bad("Body is required.");
        var skill = SessionSkills.Normalize(body with { Name = name ?? body.Name ?? "" });
        // A role nobody has would leave the skill unused without a word.
        foreach (var role in skill.Roles)
            if (personas.Get(role) is null) return Bad($"There is no persona '{role}'.");
        return Results.Ok(skills.Save(skill));
    });

    private static IResult NotFound(string name) =>
        Results.Problem($"Session skill '{name}' not found.", statusCode: StatusCodes.Status404NotFound);

    private static IResult Bad(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);
}
