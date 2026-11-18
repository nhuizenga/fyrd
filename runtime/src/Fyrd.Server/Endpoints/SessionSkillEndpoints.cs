using Fyrd.Core.Config;
using Fyrd.Core.Hub;

namespace Fyrd.Server.Endpoints;

/// <summary>
/// The console's session skill API: the skills themselves, the setup script generated from them, and the owner's
/// confirmation that the script is pasted into the environment Fyrd uses.
/// </summary>
internal static class SessionSkillEndpoints
{
    private sealed record ConfirmBody(string? Hash);

    public static IEndpointRouteBuilder MapSessionSkills(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hub/session-skills");

        group.MapGet("/", (SessionSkills skills) => ApiErrors.Run(() => Results.Ok(skills.List())));

        group.MapGet("/setup-script", (SessionSkills skills, ConfigStore config) => ApiErrors.Run(() => Results.Ok(State(skills, config.Load()))));

        // The owner says the script is pasted. The hash they send is the hash of the script they copied: when the
        // skills changed since (in another tab, say), that script is not the current one and nothing is recorded.
        group.MapPost("/setup-script/confirm", (ConfirmBody? body, SessionSkills skills, ConfigStore config) => ApiErrors.Run(() =>
        {
            if (!FyrdConfig.IsValidSessionSkillsHash(body?.Hash)) return Bad("Body { hash } is required: the hash of the script you pasted.");
            var current = SessionSkills.Hash(skills.List());
            if (body!.Hash != current)
                return Results.Problem("The session skills changed after this script was made. Copy the current script, paste it, then confirm.", statusCode: StatusCodes.Status409Conflict);
            config.Save(config.Load() with { SessionSkillsHash = current });
            return Results.Ok(State(skills, config.Load()));
        }));

        group.MapDelete("/setup-script/confirm", (SessionSkills skills, ConfigStore config) => ApiErrors.Run(() =>
        {
            config.Save(config.Load() with { SessionSkillsHash = null });
            return Results.Ok(State(skills, config.Load()));
        }));

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
            var mode = SessionSkills.Mode(config.Load(), list);
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
            mode = Name(SessionSkills.Mode(config, list)),
            hash = SessionSkills.Hash(list),
            confirmedHash = config.SessionSkillsHash,
            environmentId = config.EnvironmentId,
            skills = list.Select(s => s.Name),
            script = SessionSkillScript.Render(list),
            skillsDir = SessionSkillScript.SkillsDir,
            hashFile = SessionSkillScript.HashFile,
        };
    }

    private static string Name(SessionSkillMode mode) => mode switch
    {
        SessionSkillMode.Installed => "installed",
        SessionSkillMode.OutOfDate => "out-of-date",
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
