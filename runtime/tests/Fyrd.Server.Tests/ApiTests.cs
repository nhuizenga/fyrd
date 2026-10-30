using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fyrd.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Fyrd.Server.Tests;

public class ApiTests
{
    private const string Repo = "servicetitan/titan-lens-ossie";

    private static async Task<JsonElement> ReadJson(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static Task<HttpResponseMessage> CreateSpec(HttpClient c, string title = "Add export", string markdown = "# x") =>
        c.PostAsJsonAsync("/api/specs", new { title, repo = Repo, markdown });

    [Fact]
    public async Task Health_Ok()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", (await ReadJson(r)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateSpec_ThenList_ShowsIt()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var created = await CreateSpec(c);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var list = await ReadJson(await c.GetAsync("/api/specs"));
        var entry = Assert.Single(list.EnumerateArray());
        Assert.Equal("Add export", entry.GetProperty("run").GetProperty("title").GetString());
        Assert.Equal("Draft", entry.GetProperty("run").GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateSpec_EmptyMarkdown_Returns400()
    {
        using var f = new FyrdFactory();
        var r = await CreateSpec(f.CreateClient(), markdown: "  ");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetSpecs_WithOneMalformed_Returns200()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await CreateSpec(c, "One");
        var second = await ReadJson(await CreateSpec(c, "Two"));
        var id = second.GetProperty("id").GetString()!;
        File.WriteAllText(Path.Combine(f.Home.Paths.SpecDir(id), "run.json"), "{ not json");

        var r = await c.GetAsync("/api/specs");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var entries = (await ReadJson(r)).EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Single(entries, e => e.GetProperty("error").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task GetSpec_ReturnsRunMarkdownUnits_AndMalformedIs422()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var id = (await ReadJson(await CreateSpec(c))).GetProperty("id").GetString()!;
        var body = await ReadJson(await c.GetAsync($"/api/specs/{id}"));
        Assert.Equal("# x", body.GetProperty("markdown").GetString());
        Assert.Equal(id, body.GetProperty("run").GetProperty("id").GetString());
        Assert.Equal(0, body.GetProperty("units").GetArrayLength());

        File.WriteAllText(Path.Combine(f.Home.Paths.SpecDir(id), "units.json"), "[ nope");
        var bad = await c.GetAsync($"/api/specs/{id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Contains("units.json", (await ReadJson(bad)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task GetSpec_Unknown_Returns404()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().GetAsync("/api/specs/20260101-nope");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task GetSpec_TraversalId_Returns400()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().GetAsync("/api/specs/..%2F..%2Fx");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Attack_SetsQueued_ThenSecondAttackIs409()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var id = (await ReadJson(await CreateSpec(c))).GetProperty("id").GetString()!;

        var first = await c.PostAsync($"/api/specs/{id}/attack", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("Queued", (await ReadJson(first)).GetProperty("status").GetString());

        var second = await c.PostAsync($"/api/specs/{id}/attack", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var missing = await c.PostAsync("/api/specs/20260101-nope/attack", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static async Task<InboxCard> SeedCard(FyrdFactory f)
    {
        var specId = (await ReadJson(await CreateSpec(f.CreateClient()))).GetProperty("id").GetString()!;
        return f.Services.GetRequiredService<InboxStore>()
            .Add(specId, null, "Which test framework?", ["Unit tests", "E2E"], null, null, null, true);
    }

    [Fact]
    public async Task AnswerCard_RemembersByDefault()
    {
        using var f = new FyrdFactory();
        var card = await SeedCard(f);
        var c = f.CreateClient();

        var r = await c.PostAsJsonAsync($"/api/inbox/{card.Id}/answer", new { text = "Unit tests" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Empty((await ReadJson(await c.GetAsync("/api/inbox"))).EnumerateArray());

        var memory = File.ReadAllText(Path.Combine(f.Home.Paths.MemoryDir, "servicetitan__titan-lens-ossie.md"));
        Assert.Contains("**A:** Unit tests", memory);
    }

    [Fact]
    public async Task AnswerCard_Twice_Returns409()
    {
        using var f = new FyrdFactory();
        var card = await SeedCard(f);
        var c = f.CreateClient();
        await c.PostAsJsonAsync($"/api/inbox/{card.Id}/answer", new { text = "Unit tests" });
        var again = await c.PostAsJsonAsync($"/api/inbox/{card.Id}/answer", new { text = "E2E" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task AnswerCard_BadInput_Returns400Or404()
    {
        using var f = new FyrdFactory();
        var card = await SeedCard(f);
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync($"/api/inbox/{card.Id}/answer", new { text = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await c.PostAsJsonAsync("/api/inbox/abcdef123456/answer", new { text = "x" })).StatusCode);
    }

    [Fact]
    public async Task PutConfig_OutOfRange_Returns400_AndKeepsOld()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var bad = await c.PutAsJsonAsync("/api/config", new { squadCap = 9 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(4, (await ReadJson(await c.GetAsync("/api/config"))).GetProperty("squadCap").GetInt32());

        var ok = await c.PutAsJsonAsync("/api/config", new { squadCap = 6 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(6, (await ReadJson(await c.GetAsync("/api/config"))).GetProperty("squadCap").GetInt32());
    }

    [Fact]
    public async Task UnknownApiPath_Returns404_NotIndexHtml()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.DoesNotContain("<html", await r.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
