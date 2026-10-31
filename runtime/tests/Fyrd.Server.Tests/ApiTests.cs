using Fyrd.Core.Specs;
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
    public async Task Attack_MalformedRunJson_Returns422()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var id = (await ReadJson(await CreateSpec(c))).GetProperty("id").GetString()!;
        File.WriteAllText(Path.Combine(f.Home.Paths.SpecDir(id), "run.json"), "{ nope");
        var r = await c.PostAsync($"/api/specs/{id}/attack", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
    }

    [Fact]
    public async Task GetInbox_WithCorruptCard_Returns200WithGoodCard()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var specId = (await ReadJson(await CreateSpec(c))).GetProperty("id").GetString()!;
        var inbox = f.Services.GetRequiredService<InboxStore>();
        var good = inbox.Add(specId, null, "Which?", ["a", "b"], null, null, null, true);
        File.WriteAllText(Path.Combine(f.Home.Paths.InboxDir, "badbadbadbad.json"), "{ nope");
        var r = await c.GetAsync("/api/inbox");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(good.Id, Assert.Single((await ReadJson(r)).EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Post_WithForeignOrigin_Returns403()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = (await ReadJson(await CreateSpec(c))).GetProperty("id").GetString()!;
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/specs/{id}/attack");
        req.Headers.Add("Origin", "http://evil.example");
        var r = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("Draft", (await ReadJson(await c.GetAsync($"/api/specs/{id}"))).GetProperty("run").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Post_WithForeignReferer_Returns403()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/specs/x/attack");
        req.Headers.Add("Referer", "http://evil.example/page");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Post_WithMatchingOrigin_IsAllowed()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = (await ReadJson(await CreateSpec(c))).GetProperty("id").GetString()!;
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/specs/{id}/attack");
        req.Headers.Add("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Request_WithForeignHost_Returns400()
    {
        using var f = new FyrdFactory();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/specs");
        req.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await f.CreateClient().SendAsync(req)).StatusCode);
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
    public async Task Attack_SetsBuilding_ThenSecondAttackIs409()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = (await ReadJson(await CreateSpec(c))).GetProperty("id").GetString()!;

        var first = await c.PostAsync($"/api/specs/{id}/attack", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("Building", (await ReadJson(first)).GetProperty("status").GetString());

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
    public async Task AnswerEndpoint_RelayFails_StillAnswered_Returns200WithRelayError()
    {
        using var f = new FyrdFactory();
        var specId = (await ReadJson(await CreateSpec(f.CreateClient()))).GetProperty("id").GetString()!;
        f.Services.GetRequiredService<SpecStore>().SetLead(specId, "sess-1");
        var card = f.Services.GetRequiredService<InboxStore>()
            .Add(specId, null, "Which?", ["a"], null, null, null, true, sourceKey: specId + ":q1");
        f.Cloud.ThrowOnNextCall(new HttpRequestException("boom"));
        var c = f.CreateClient();

        var r = await c.PostAsJsonAsync($"/api/inbox/{card.Id}/answer", new { text = "a" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await ReadJson(r);
        Assert.Equal("boom", json.GetProperty("relayError").GetString());
        Assert.Equal("a", json.GetProperty("answer").GetProperty("text").GetString());
        Assert.Empty((await ReadJson(await c.GetAsync("/api/inbox"))).EnumerateArray());
    }

    [Fact]
    public async Task RelayRoute_ResetsAndSends()
    {
        using var f = new FyrdFactory();
        var specId = (await ReadJson(await CreateSpec(f.CreateClient()))).GetProperty("id").GetString()!;
        f.Services.GetRequiredService<SpecStore>().SetLead(specId, "sess-1");
        var inbox = f.Services.GetRequiredService<InboxStore>();
        var card = inbox.Add(specId, null, "Which?", ["a"], null, null, null, true, sourceKey: specId + ":q1");
        var c = f.CreateClient();

        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/inbox/{card.Id}/relay", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/inbox/abcdef123456/relay", null)).StatusCode);

        f.Cloud.ThrowOnNextCall(new HttpRequestException("boom"));
        await c.PostAsJsonAsync($"/api/inbox/{card.Id}/answer", new { text = "a" });
        Assert.Equal("failed", inbox.Get(card.Id).Answer!.RelayStatus);
        Assert.Empty(f.Cloud.Sent);

        var r = await c.PostAsync($"/api/inbox/{card.Id}/relay", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Single(f.Cloud.Sent);
        var after = inbox.Get(card.Id).Answer!;
        Assert.Equal(("sent", 0), (after.RelayStatus, after.RelayAttempts));
    }

    [Fact]
    public async Task GetUndelivered_ListsFailedRelays()
    {
        using var f = new FyrdFactory();
        var specId = (await ReadJson(await CreateSpec(f.CreateClient()))).GetProperty("id").GetString()!;
        f.Services.GetRequiredService<SpecStore>().SetLead(specId, "sess-1");
        var inbox = f.Services.GetRequiredService<InboxStore>();
        var card = inbox.Add(specId, null, "Which?", ["a"], null, null, null, true, sourceKey: specId + ":q1");
        var c = f.CreateClient();
        Assert.Empty((await ReadJson(await c.GetAsync("/api/inbox/undelivered"))).EnumerateArray());

        f.Cloud.ThrowOnNextCall(new HttpRequestException("boom"));
        await c.PostAsJsonAsync($"/api/inbox/{card.Id}/answer", new { text = "a" });
        var list = (await ReadJson(await c.GetAsync("/api/inbox/undelivered"))).EnumerateArray().ToList();
        var item = Assert.Single(list);
        Assert.Equal(card.Id, item.GetProperty("id").GetString());
        Assert.Equal("failed", item.GetProperty("answer").GetProperty("relayStatus").GetString());
        Assert.Empty((await ReadJson(await c.GetAsync("/api/inbox"))).EnumerateArray());
    }

    [Fact]
    public async Task PutConfig_SameCap_DoesNotRelay_ChangedCapRelays()
    {
        using var f = new FyrdFactory();
        var specId = (await ReadJson(await CreateSpec(f.CreateClient()))).GetProperty("id").GetString()!;
        var specs = f.Services.GetRequiredService<SpecStore>();
        specs.SetLead(specId, "sess-1");
        specs.UpdateStatus(specId, SpecStatus.Building);
        var c = f.CreateClient();

        var same = await c.PutAsJsonAsync("/api/config", new { squadCap = 4 });
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Empty(f.Cloud.Sent);

        var changed = await c.PutAsJsonAsync("/api/config", new { squadCap = 6 });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Contains("\"squadCap\":6", Assert.Single(f.Cloud.Sent).Text);

        f.Cloud.ThrowOnNextCall(new HttpRequestException("down"));
        var failed = await c.PutAsJsonAsync("/api/config", new { squadCap = 7 });
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal("down", (await ReadJson(failed)).GetProperty("relayError").GetString());
        Assert.Equal(7, (await ReadJson(await c.GetAsync("/api/config"))).GetProperty("squadCap").GetInt32());
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

        var memory = File.ReadAllText(Path.Combine(f.Home.Paths.MemoryDir, "servicetitan__titan-lens-ossie.md"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(memory, @"\*\*A:\*\* Unit tests"));
        Assert.DoesNotContain("**A:** E2E", memory);
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

    private static async Task<string> SeedSpec(HttpClient c) =>
        (await ReadJson(await CreateSpec(c))).GetProperty("id").GetString()!;

    private static Task<HttpResponseMessage> SetEnv(HttpClient c) =>
        c.PutAsJsonAsync("/api/config", new { environmentId = "env_abc" });

    [Fact]
    public async Task Attack_WithoutEnvironment_Returns409()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var id = await SeedSpec(c);
        var r = await c.PostAsync($"/api/specs/{id}/attack", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("Set the cloud environment in Settings", await r.Content.ReadAsStringAsync());
        Assert.Empty(f.Cloud.Created);
    }

    [Fact]
    public async Task Attack_Succeeds_ReturnsBuildingRun()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = await SeedSpec(c);
        var r = await c.PostAsync($"/api/specs/{id}/attack", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Building", (await ReadJson(r)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Attack_Concurrent_CreatesOneLead()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = await SeedSpec(c);
        var rs = await Task.WhenAll(
            Task.Run(() => c.PostAsync($"/api/specs/{id}/attack", null)),
            Task.Run(() => c.PostAsync($"/api/specs/{id}/attack", null)));
        Assert.Single(rs, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(rs, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Single(f.Cloud.Created);
    }

    [Fact]
    public async Task Attack_LaunchFails_Returns502_AndSpecIsDraft()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = await SeedSpec(c);
        f.Cloud.ThrowOnNextCall(new Fyrd.Core.CloudSessions.CloudSessionApiException(500, "secret-body"));
        var r = await c.PostAsync($"/api/specs/{id}/attack", null);
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-body", text);
        Assert.Contains("HTTP 500", text);
        var spec = await ReadJson(await c.GetAsync($"/api/specs/{id}"));
        Assert.Equal("Draft", spec.GetProperty("run").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Attack_TraversalId_Returns400()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var r = await c.PostAsync("/api/specs/..%2F..%2Fx/attack", null);
        Assert.True(r.StatusCode is HttpStatusCode.BadRequest, $"got {(int)r.StatusCode}");
    }

    [Fact]
    public async Task Attack_Timeout_Returns502_AndSpecIsDraft()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = await SeedSpec(c);
        f.Cloud.ThrowOnNextCall(new TaskCanceledException("timeout"));
        var r = await c.PostAsync($"/api/specs/{id}/attack", null);
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Contains("Timed out creating the lead session", await r.Content.ReadAsStringAsync());
        var spec = await ReadJson(await c.GetAsync($"/api/specs/{id}"));
        Assert.Equal("Draft", spec.GetProperty("run").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Board_IncludesUnitsLeadAndPeerLinks()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var specs = f.Services.GetRequiredService<SpecStore>();
        var run = specs.Create("Board spec", Repo, "# x");
        specs.SetLead(run.Id, "session_lead");
        specs.WriteUnits(run.Id, [new Unit("u1", run.Id, "Unit", UnitStage.Implement, UnitState.Running)]);
        var state = f.Services.GetRequiredService<Fyrd.Core.Orchestration.RunnerState>();
        state.LastError = "poll failed";
        state.Record(new Fyrd.Core.CloudSessions.CloudSession("session_lead", "Lead", "running", "connected", null,
            ["fyrd", $"fyrd-spec-{run.Id}", "fyrd-role-lead"],
            JsonDocument.Parse("""{"external_metadata":{"context_usage":{"used_tokens":52000,"max_tokens":1000000},"rate_limit_info":{"status":"allowed","resetsAt":1793491200}}}""").RootElement.Clone()));
        state.Record(new Fyrd.Core.CloudSessions.CloudSession("session_x", "Peer", "idle", "connected", null,
            ["fyrd", $"fyrd-spec-{run.Id}", "fyrd-role-implementer", "fyrd-unit-u1"], default));

        state.Record(new Fyrd.Core.CloudSessions.CloudSession("session_old", "Old", "archived", "connected", null,
            ["fyrd", $"fyrd-spec-{run.Id}", "fyrd-role-reviewer", "fyrd-unit-u1"], default));

        var board = await ReadJson(await c.GetAsync("/api/board"));
        Assert.Equal("poll failed", board.GetProperty("lastError").GetString());
        Assert.False(board.GetProperty("signedOut").GetBoolean());
        Assert.Equal("allowed", board.GetProperty("rateLimit").GetProperty("status").GetString());
        Assert.Equal("2026-11-01T00:00:00.0000000+00:00", board.GetProperty("rateLimit").GetProperty("resetsAt").GetString());
        var spec = Assert.Single(board.GetProperty("specs").EnumerateArray());
        Assert.Equal(1, spec.GetProperty("units").GetArrayLength());
        var lead = spec.GetProperty("lead");
        Assert.Equal("https://claude.ai/code/session_lead", lead.GetProperty("url").GetString());
        Assert.Equal(52000, lead.GetProperty("contextUsed").GetInt64());
        Assert.Equal(1000000, lead.GetProperty("contextMax").GetInt64());
        var peer = Assert.Single(spec.GetProperty("peers").EnumerateArray());
        Assert.Equal("https://claude.ai/code/session_x", peer.GetProperty("url").GetString());
        Assert.Equal("implementer", peer.GetProperty("role").GetString());
        Assert.Equal("u1", peer.GetProperty("unitId").GetString());
    }

    [Fact]
    public async Task Board_ArchivedPeerNotListed()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var specs = f.Services.GetRequiredService<SpecStore>();
        var run = specs.Create("Archived board", Repo, "# x");
        specs.SetLead(run.Id, "session_lead");
        var state = f.Services.GetRequiredService<Fyrd.Core.Orchestration.RunnerState>();
        state.Record(new Fyrd.Core.CloudSessions.CloudSession("session_old", "Old", "archived", "connected", null,
            ["fyrd", $"fyrd-spec-{run.Id}", "fyrd-role-reviewer", "fyrd-unit-u1"], default));
        var board = await ReadJson(await c.GetAsync("/api/board"));
        var spec = Assert.Single(board.GetProperty("specs").EnumerateArray());
        Assert.Equal(0, spec.GetProperty("peers").GetArrayLength());
    }

    [Fact]
    public async Task Board_MalformedSpecStillListed()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await CreateSpec(c, "One");
        var id = (await ReadJson(await CreateSpec(c, "Two"))).GetProperty("id").GetString()!;
        File.WriteAllText(Path.Combine(f.Home.Paths.SpecDir(id), "run.json"), "{ not json");

        var r = await c.GetAsync("/api/board");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var specs = (await ReadJson(r)).GetProperty("specs").EnumerateArray().ToList();
        Assert.Equal(2, specs.Count);
        Assert.Single(specs, e => e.GetProperty("entry").GetProperty("error").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task Board_StrayFolderInSpecsDir_Returns200WithErrorEntry()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await CreateSpec(c, "One");
        Directory.CreateDirectory(Path.Combine(f.Home.Paths.SpecsDir, "archive"));

        var r = await c.GetAsync("/api/board");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var specs = (await ReadJson(r)).GetProperty("specs").EnumerateArray().ToList();
        Assert.Equal(2, specs.Count);
        var bad = Assert.Single(specs, e => e.GetProperty("entry").GetProperty("error").ValueKind == JsonValueKind.String);
        Assert.Equal("archive", bad.GetProperty("entry").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Attack_HttpRequestException_Returns502_AndSpecIsDraft()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = await SeedSpec(c);
        f.Cloud.ThrowOnNextCall(new HttpRequestException("No such host is known"));
        var r = await c.PostAsync($"/api/specs/{id}/attack", null);
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Contains("No such host is known", await r.Content.ReadAsStringAsync());
        var spec = await ReadJson(await c.GetAsync($"/api/specs/{id}"));
        Assert.Equal("Draft", spec.GetProperty("run").GetProperty("status").GetString());
    }

    [Fact]
    public async Task PutMemory_RoundTripsExactly()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        const string text = "a\r\nb \u00e9\u4e2d\U0001F600\r\n";
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/api/memory/owner-proxy", new { text = text + "o" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/api/memory/global", new { text = text + "g" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/api/memory/repo?repo=" + Repo, new { text = text + "r" })).StatusCode);

        var got = await ReadJson(await c.GetAsync("/api/memory"));
        Assert.Equal(text + "o", got.GetProperty("ownerProxy").GetString());
        Assert.Equal(text + "g", got.GetProperty("global").GetString());
        var repo = Assert.Single(got.GetProperty("repos").EnumerateArray());
        Assert.Equal(Repo, repo.GetProperty("repo").GetString());
        Assert.Equal(text + "r", repo.GetProperty("text").GetString());
    }

    [Fact]
    public async Task PutMemory_TooLarge_Returns400()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var r = await c.PutAsJsonAsync("/api/memory/global", new { text = new string('x', 200 * 1024 + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.PutAsJsonAsync("/api/memory/global", new { text = new string('x', 200 * 1024) })).StatusCode);
    }

    [Fact]
    public async Task PutMemory_BadRepo_Returns400()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        foreach (var url in new[] { "/api/memory/repo?repo=../..", "/api/memory/repo?repo=nope", "/api/memory/repo" })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync(url, new { text = "x" })).StatusCode);
    }

    [Fact]
    public async Task Research_LaunchesAndMovesToResearching()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = await SeedSpec(c);
        var r = await c.PostAsync($"/api/specs/{id}/research", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Researching", (await ReadJson(r)).GetProperty("status").GetString());
        Assert.Contains("fyrd-role-research-lead", Assert.Single(f.Cloud.Created).Tags);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/specs/{id}/research", null)).StatusCode);
    }

    [Fact]
    public async Task Research_WithoutEnvironment_Returns409()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var id = await SeedSpec(c);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/specs/{id}/research", null)).StatusCode);
        Assert.Empty(f.Cloud.Created);
    }

    [Fact]
    public async Task Research_WithProposal_Returns409()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = await SeedSpec(c);
        Propose(f.Services.GetRequiredService<SpecStore>(), id, "# n");
        var r = await c.PostAsync($"/api/specs/{id}/research", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Empty(f.Cloud.Created);
    }

    [Fact]
    public async Task Attack_WithProposal_Returns409()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = await SeedSpec(c);
        Propose(f.Services.GetRequiredService<SpecStore>(), id, "# n");
        var r = await c.PostAsync($"/api/specs/{id}/attack", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("Review the research proposal first", await r.Content.ReadAsStringAsync());
        Assert.Empty(f.Cloud.Created);
        Assert.Equal(SpecStatus.Draft, f.Services.GetRequiredService<SpecStore>().Get(id)!.Status);
    }

    [Fact]
    public async Task Proposal_GetAcceptDiscard()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var id = await SeedSpec(c);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/specs/{id}/proposal")).StatusCode);
        var store = f.Services.GetRequiredService<SpecStore>();
        Propose(store, id, "# new");
        var got = await ReadJson(await c.GetAsync($"/api/specs/{id}/proposal"));
        Assert.Equal("# new", got.GetProperty("markdown").GetString());
        var acc = await c.PostAsJsonAsync($"/api/specs/{id}/proposal/accept", new { markdown = "# edited" });
        Assert.Equal(HttpStatusCode.OK, acc.StatusCode);
        Assert.Equal("# edited", store.ReadMarkdown(id));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/specs/{id}/proposal/accept", new { })).StatusCode);

        Propose(store, id, "# again");
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/specs/{id}/proposal/discard", null)).StatusCode);
        Assert.Null(store.GetProposal(id));
    }

    [Fact]
    public async Task AcceptProposal_WhileResearching_Returns409()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = await SeedSpec(c);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/specs/{id}/research", null)).StatusCode);
        var store = f.Services.GetRequiredService<SpecStore>();
        store.SaveProposal(id, "# new", "sum", [], "x");
        var r = await c.PostAsJsonAsync($"/api/specs/{id}/proposal/accept", new { });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.NotEqual("# new", store.ReadMarkdown(id));
    }

    [Fact]
    public async Task PutMarkdown_DraftOnly()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await SetEnv(c);
        var id = await SeedSpec(c);
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/specs/{id}/markdown", new { markdown = "# v2" })).StatusCode);
        Assert.Equal("# v2", f.Services.GetRequiredService<SpecStore>().ReadMarkdown(id));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/specs/{id}/markdown", new { markdown = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/specs/{id}/attack", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync($"/api/specs/{id}/markdown", new { markdown = "# v3" })).StatusCode);
    }

    private static void Propose(SpecStore store, string id, string markdown)
    {
        Assert.True(store.TryTransition(id, SpecStatus.Draft, SpecStatus.Researching));
        store.SaveProposal(id, markdown, "sum", [], "x");
        store.Finish(id, SpecStatus.Draft, null, null);
    }
}
