// Throwaway spike: verifies the cloud-session assumptions Plan 2 depends on.
// Run ONLY by the owner:  dotnet run --project spikes/cloud-sessions -- <q1..q7> [args]
//
// Rules: the access token is never printed or logged. Only responses are logged, one JSON line each,
// to spikes/cloud-sessions/out/<qN>.jsonl (gitignored).
//
// Endpoints the typed client lacks, as found (read-only grep) in the Claude Code CLI binary claude.exe:
//   * LIST sessions : GET {base}/v1/sessions   (CLI sends it with header anthropic-beta: ccr-byoc-2025-07-29, no query
//                     params at all; the v1alpha2 variant is GET /v1/code/sessions?limit=N). The CLI never sends a
//                     `tags` filter, so a server-side tags filter is UNVERIFIED: q3 tries several spellings and also
//                     filters client-side on each session's `tags`.
//   * INTERRUPT     : POST {base}/v1/sessions/{id}/events with body
//                     {"events":[{"type":"control_request","request_id":"<id>",
//                                 "request":{"subtype":"interrupt","cancel_queued":true},"uuid":"<guid>"}]}
//                     Headers: Bearer token, anthropic-beta: ccr-byoc-2025-07-29, x-organization-uuid
//                     (CLI function interruptRemoteSession; payload constant {subtype:"interrupt",cancel_queued:true}).
//   * ARCHIVE       : POST {base}/v1/code/sessions/{id}/archive with body {}  (CLI archiveRemoteSession; 200 or 409 =
//                     success). Note the different path prefix (/v1/code/sessions, not /v1/sessions). The CLI sends only
//                     bearer + user-agent there, so q6 sends the same four headers and, on 404, also tries
//                     POST /v1/sessions/{id}/archive (a managed-agents SDK route; may need a different beta header).
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fyrd.Core.CloudSessions;

const string DefaultEnv = "env_011v4b3WBhRMaGGti6Qw747c";
const string Model = "claude-sonnet-5-5";
const string Repo = "https://github.com/servicetitan/titan-lens-ossie";
const string BranchA = "fyrd-spike/2026-10-29-a";
const string BranchB = "fyrd-spike/2026-10-29-b";
const string BranchIntegration = "fyrd-spike/2026-10-29-integration";
const string SpikeTag = "fyrd-spec-spike";
const string Codeword = "PINEAPPLE-7421";

var outDir = FindOutDir();
Directory.CreateDirectory(outDir);

var argv = args.ToList();
if (argv.Count == 0) return Usage();
var cmd = argv[0].ToLowerInvariant();
var envId = TakeOpt(argv, "--env") ?? DefaultEnv;
var timeoutMin = int.TryParse(TakeOpt(argv, "--timeout"), out var tm) ? tm : 30;
var leadOpt = TakeOpt(argv, "--lead");
var positional = argv.Skip(1).Where(a => !a.StartsWith("--")).ToList();

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
var creds = new ClaudeCredentials();
var client = new CloudSessionClient(http, creds);
var raw = new RawApi(http, creds);

try
{
    switch (cmd)
    {
        case "q1": await Q1(); break;
        case "q2": await Q2(); break;
        case "q3": await Q3(); break;
        case "q4":
            if (positional.Count > 0 && positional[0] == "ask") await Q4Ask();
            else if (positional.Count > 1 && positional[0] == "answer") await Q4Answer(positional[1]);
            else return Usage();
            break;
        case "q5": await Q5(); break;
        case "q6": await Q6(); break;
        case "q7": await Q7(); break;
        default: return Usage();
    }
    return 0;
}
catch (CloudSessionApiException e)
{
    Console.Error.WriteLine($"API error HTTP {e.Status}: {Trunc(e.Body, 800)}");
    Log("error", "api-exception", new JsonObject { ["status"] = e.Status, ["body"] = e.Body });
    return 1;
}
catch (Exception e) when (e is CloudSessionAuthException or ClaudeAuthMissingException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

// ------------------------------------------------------------------------------------------------ questions

// Q1: does an API-created session have the peer (claude-code-remote) tools?
async Task Q1()
{
    var q = "q1";
    var sw = Stopwatch.StartNew();
    string id;
    if (leadOpt is not null) id = leadOpt;
    else
    {
        var s = await client.CreateAsync(NewReq("fyrd spike q1 lead", ["fyrd", SpikeTag, "fyrd-role-lead"],
            "List every tool available to you whose name starts with `mcp__claude-code-remote__`. " +
            "Reply with one fenced ```json block containing an array of those tool names (exact names), " +
            "and nothing else. If there are none, reply with an empty array and say so in one sentence. " +
            "Do not call any of the tools and do not modify any files."));
        id = s.Id;
        LogSession(q, "created", s);
    }
    SaveLead(id);
    Console.WriteLine($"[q1] lead session: {id}");
    var seen = new StatusSeen();
    Console.WriteLine($"[q1] {await WaitSettled(q, id, seen, TimeSpan.FromMinutes(timeoutMin))}");
    var events = await FetchAllEvents(q, id);
    var last = LastAssistant(events);
    Console.WriteLine($"[q1] took {sw.Elapsed:mm\\:ss}. Last assistant text:\n{Trunc(last?.Text, 2500)}");
    var text = last?.Text ?? "";
    foreach (var want in new[] { "create_session", "send_message", "get_session", "list_events" })
        Console.WriteLine($"[q1] mentions mcp__claude-code-remote__{want}: {text.Contains("mcp__claude-code-remote__" + want)}");
    seen.Print();
}

// Q2: lead spawns two tagged implementer peers, both push, lead merges into the integration branch.
// Also tracks Q5 status values while it works (every 10 s) and verifies pushes with `git ls-remote`.
async Task Q2()
{
    var q = "q2";
    var sw = Stopwatch.StartNew();
    var prompt = LeadPrompt();
    string id;
    HashSet<string>? baseline = null;
    if (leadOpt is not null)
    {
        id = leadOpt;
        baseline = await Baseline(id); // only events after this point count as the lead's response
        await client.SendMessageAsync(id, prompt);
        Log(q, "sent-prompt-to-existing-lead", new JsonObject { ["session"] = id });
    }
    else
    {
        var s = await client.CreateAsync(NewReq("fyrd spike q2 lead", ["fyrd", SpikeTag, "fyrd-role-lead"], prompt));
        id = s.Id;
        LogSession(q, "created", s);
    }
    SaveLead(id);
    Console.WriteLine($"[q2] lead session: {id}  (polling every 10 s, timeout {timeoutMin} min)");
    var seen = new StatusSeen();
    var leadResult = await WaitSettled(q, id, seen, TimeSpan.FromMinutes(timeoutMin), baseline);
    Console.WriteLine($"[q2] lead settled after {sw.Elapsed:mm\\:ss} ({leadResult})");

    var events = await FetchAllEvents(q, id);
    var last = LastAssistant(events);
    Console.WriteLine($"[q2] lead's final message:\n{Trunc(last?.Text, 3000)}");

    // Children discovered by tag without relying on the lead's report (also exercises Q3 client-side).
    var children = (await ListAllSessions(q)).Where(s => s.Tags.Contains("fyrd-role-implementer") && s.Tags.Contains(SpikeTag)).ToList();
    Console.WriteLine($"[q2] implementer sessions found by tag: {children.Count}");
    var childIds = new List<string>();
    foreach (var c in children)
    {
        childIds.Add(c.Id);
        var s = await client.GetAsync(c.Id);
        seen.Record(s);
        LogSession(q, "child", s);
        Console.WriteLine($"  child {s.Id} tags=[{string.Join(",", s.Tags)}] session_status={s.SessionStatus} connection={s.ConnectionStatus} category={s.Summary?.StatusCategory}");
    }
    File.WriteAllLines(Path.Combine(outDir, "q2-children.txt"), childIds);

    var ls = RunProcess("git", $"ls-remote {Repo} \"refs/heads/fyrd-spike/*\"");
    Log(q, "git-ls-remote", new JsonObject { ["output"] = ls });
    Console.WriteLine($"[q2] remote branches:\n{ls}");
    foreach (var b in new[] { BranchA, BranchB, BranchIntegration })
        Console.WriteLine($"[q2] pushed {b}: {ls.Contains("refs/heads/" + b)}");
    Console.WriteLine($"[q2] total elapsed {sw.Elapsed:mm\\:ss}");
    seen.Print();
    File.WriteAllText(Path.Combine(outDir, "q2-timing.txt"), sw.Elapsed.ToString());
}

// Q3: discovery by tag. Tries server-side tag filters and client-side filtering.
async Task Q3()
{
    var q = "q3";
    var variants = new[]
    {
        "/v1/sessions?limit=100",
        $"/v1/sessions?tags={SpikeTag}&limit=100",
        $"/v1/sessions?tags[]={SpikeTag}&limit=100",
        $"/v1/sessions?tag={SpikeTag}&limit=100",
    };
    foreach (var v in variants)
    {
        var r = await raw.SendAsync(HttpMethod.Get, v, null);
        Log(q, v, r.Describe());
        var arr = r.Json?["data"]?.AsArray();
        var total = arr?.Count ?? -1;
        var matching = arr?.Count(n => TagsOf(n).Contains(SpikeTag)) ?? 0;
        Console.WriteLine($"[q3] GET {v} -> HTTP {r.Status}; returned={total}; with tag {SpikeTag}={matching}; has_more={r.Json?["has_more"]}");
        if (arr is not null && total > 0 && matching == total && v.Contains("tag"))
            Console.WriteLine($"[q3]   -> every returned session carries the tag; compare with the unfiltered count to see whether the server filtered.");
    }
    var all = await ListAllSessions(q);
    var tagged = all.Where(s => s.Tags.Contains(SpikeTag)).ToList();
    Console.WriteLine($"[q3] client-side filter over {all.Count} listed sessions: {tagged.Count} tagged {SpikeTag}");
    foreach (var s in tagged)
        Console.WriteLine($"  {s.Id}  tags=[{string.Join(",", s.Tags)}]  status={s.SessionStatus}  title={s.Title}");
    Console.WriteLine("[q3] Compare these ids against the lead/child ids from q1/q2 to confirm Fyrd needs no stored ids.");
}

// Q4 part 1: lead asks the owner a question via a fenced fyrd-question block, then the owner stops the process.
async Task Q4Ask()
{
    var q = "q4";
    var prompt =
        $"Remember this codeword for later: {Codeword}. Do not repeat it in this turn.\n" +
        "Do not touch any files or run any tools. Your ONLY job this turn is to ask the owner a question. " +
        "End your turn with exactly one fenced code block whose info string is `fyrd-question` and whose body is this JSON:\n" +
        "```fyrd-question\n{\"id\":\"q-color\",\"question\":\"Which colour should the spike use, red or blue?\",\"options\":[\"red\",\"blue\"]}\n```\n" +
        "Then stop and wait for the answer.";
    var s = await client.CreateAsync(NewReq("fyrd spike q4 lead", ["fyrd", SpikeTag, "fyrd-role-lead"], prompt));
    LogSession(q, "created", s);
    Console.WriteLine($"[q4] lead session: {s.Id}");
    var seen = new StatusSeen();
    Console.WriteLine($"[q4] {await WaitSettled(q, s.Id, seen, TimeSpan.FromMinutes(timeoutMin))}");
    var after = await client.GetAsync(s.Id);
    LogSession(q, "after-settle", after);
    var events = await FetchAllEvents(q, s.Id);
    var last = LastAssistant(events);
    var question = ExtractQuestion(last?.Text);
    Console.WriteLine($"[q4] last assistant text:\n{Trunc(last?.Text, 1500)}");
    Console.WriteLine($"[q4] fyrd-question block found in last assistant event: {question ?? "NO"}");
    Console.WriteLine($"[q4] post_turn_summary: category={after.Summary?.StatusCategory} needs_action={after.Summary?.NeedsAction} " +
                      $"recent_action={Trunc(after.Summary?.RecentAction, 120)} detail={Trunc(after.Summary?.StatusDetail, 120)}");
    var state = new JsonObject
    {
        ["session_id"] = s.Id,
        ["asked_at_utc"] = DateTimeOffset.UtcNow.ToString("o"),
        ["event_count"] = events.Count,
        ["last_event_uuid"] = events.LastOrDefault()?.Uuid,
        ["env_manager_log_events"] = events.Count(IsEnvManagerLog),
    };
    File.WriteAllText(Path.Combine(outDir, "q4-state.json"), state.ToJsonString());
    seen.Print();
    Console.WriteLine($"\n[q4] NOW stop here. Wait at least 2 hours, then run:\n  dotnet run --project spikes/cloud-sessions -- q4 answer {s.Id}");
}

// Q4 part 2: after the 2h+ wait, read the question back, answer it, check context and reprovisioning.
async Task Q4Answer(string id)
{
    var q = "q4";
    var sw = Stopwatch.StartNew();
    JsonNode? state = null;
    var statePath = Path.Combine(outDir, "q4-state.json");
    if (File.Exists(statePath)) state = JsonNode.Parse(File.ReadAllText(statePath));
    if (state?["asked_at_utc"] is { } at && DateTimeOffset.TryParse(at.ToString(), out var asked))
        Console.WriteLine($"[q4] time since question was asked: {DateTimeOffset.UtcNow - asked:hh\\:mm\\:ss}");

    var s = await client.GetAsync(id);
    LogSession(q, "before-answer", s);
    Console.WriteLine($"[q4] before answer: session_status={s.SessionStatus} connection={s.ConnectionStatus} category={s.Summary?.StatusCategory} needs_action={s.Summary?.NeedsAction}");
    var before = await FetchAllEvents(q, id);
    var last = LastAssistant(before);
    Console.WriteLine($"[q4] fyrd-question in last assistant event: {ExtractQuestion(last?.Text) ?? "NO"}");
    var envLogsBefore = before.Count(IsEnvManagerLog);
    Console.WriteLine($"[q4] events before: {before.Count}; env_manager_log events: {envLogsBefore}");

    await client.SendMessageAsync(id, "The owner answers: blue. Now reply with ONE line only: the codeword I told you at the very start, then a space, then the owner's answer.");
    Log(q, "sent-answer", new JsonObject { ["session"] = id });
    var seen = new StatusSeen();
    var knownIds = before.Select(e => e.Uuid).ToHashSet();
    Console.WriteLine($"[q4] {await WaitSettled(q, id, seen, TimeSpan.FromMinutes(timeoutMin), knownIds)}");
    var after = await FetchAllEvents(q, id);
    var fresh = after.Where(e => !knownIds.Contains(e.Uuid)).ToList();
    var reply = LastAssistant(fresh);
    Console.WriteLine($"[q4] reply after answer: {Trunc(reply?.Text, 500)}");
    Console.WriteLine($"[q4] context intact (reply contains {Codeword}): {reply?.Text?.Contains(Codeword) == true}");
    var newEnv = fresh.Where(IsEnvManagerLog).ToList();
    Console.WriteLine($"[q4] new env_manager_log events since answer: {newEnv.Count} (environment reprovisioned? likely if > 0)");
    foreach (var e in newEnv.Take(6)) Console.WriteLine($"  {Trunc(e.Raw.GetRawText(), 300)}");
    Console.WriteLine($"[q4] new event types: {string.Join(", ", fresh.GroupBy(e => e.Type).Select(g => $"{g.Key}x{g.Count()}"))}");
    seen.Print();
    Console.WriteLine($"[q4] answer round trip {sw.Elapsed:mm\\:ss}");
}

// Q5: poll given sessions (default: last lead + q2 children) every 10 s and report distinct status values.
// (Q2 already records these while it runs; use this to watch any sessions on their own.)
async Task Q5()
{
    var q = "q5";
    var ids = positional.ToList();
    if (leadOpt is not null) ids.Add(leadOpt);
    if (ids.Count == 0)
    {
        var lead = LoadLead();
        if (lead is not null) ids.Add(lead);
        var cp = Path.Combine(outDir, "q2-children.txt");
        if (File.Exists(cp)) ids.AddRange(File.ReadAllLines(cp).Select(l => l.Trim()).Where(l => l.Length > 0));
    }
    if (ids.Count == 0) { Console.WriteLine("[q5] pass session ids, or run q1/q2 first."); return; }
    Console.WriteLine($"[q5] polling {ids.Count} sessions every 10 s for up to {timeoutMin} min (Ctrl-C to stop)");
    var seen = new StatusSeen();
    var deadline = DateTimeOffset.UtcNow.AddMinutes(timeoutMin);
    var stable = 0;
    string prev = "";
    while (DateTimeOffset.UtcNow < deadline)
    {
        var sig = new StringBuilder();
        foreach (var id in ids)
        {
            var s = await client.GetAsync(id);
            seen.Record(s);
            Log(q, "poll", SessionNode(s));
            sig.Append(s.Id).Append(s.SessionStatus).Append(s.ConnectionStatus).Append(s.Summary?.StatusCategory).Append(s.Summary?.NeedsAction);
        }
        stable = sig.ToString() == prev ? stable + 1 : 0;
        prev = sig.ToString();
        if (stable >= 6) break; // one minute without any change
        await Task.Delay(TimeSpan.FromSeconds(10));
    }
    seen.Print();
}

// Q6: interrupt then archive. With --fresh, creates a long-running throwaway session so interrupt is observable mid-run.
async Task Q6()
{
    var q = "q6";
    string id;
    if (argv.Contains("--fresh"))
    {
        var s0 = await client.CreateAsync(NewReq("fyrd spike q6 throwaway", ["fyrd", SpikeTag, "fyrd-role-implementer"],
            "Count slowly from 1 to 300, running `sleep 5` in the shell between each number and printing the number after each sleep. Do not stop until you reach 300."));
        id = s0.Id;
        LogSession(q, "created-fresh", s0);
        Console.WriteLine($"[q6] created throwaway {id}; waiting 60 s for it to start working");
        await Task.Delay(TimeSpan.FromSeconds(60));
    }
    else if (positional.Count > 0) id = positional[0];
    else
    {
        var cp = Path.Combine(outDir, "q2-children.txt");
        var first = File.Exists(cp) ? File.ReadAllLines(cp).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) : null;
        if (first is null) { Console.WriteLine("[q6] pass a child session id, or use --fresh, or run q2 first."); return; }
        id = first;
    }

    var before = await client.GetAsync(id);
    Console.WriteLine($"[q6] before: session_status={before.SessionStatus} connection={before.ConnectionStatus} category={before.Summary?.StatusCategory}");

    var interruptBody = new JsonObject
    {
        ["events"] = new JsonArray(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = Guid.NewGuid().ToString(),
            ["request"] = new JsonObject { ["subtype"] = "interrupt", ["cancel_queued"] = true },
            ["uuid"] = Guid.NewGuid().ToString(),
        }),
    };
    var ir = await raw.SendAsync(HttpMethod.Post, $"/v1/sessions/{id}/events", interruptBody);
    Log(q, "interrupt", ir.Describe());
    Console.WriteLine($"[q6] interrupt: POST /v1/sessions/{id}/events -> HTTP {ir.Status}: {Trunc(ir.Text, 400)}");
    await Task.Delay(TimeSpan.FromSeconds(15));
    var mid = await client.GetAsync(id);
    LogSession(q, "after-interrupt", mid);
    Console.WriteLine($"[q6] after interrupt: session_status={mid.SessionStatus} connection={mid.ConnectionStatus} category={mid.Summary?.StatusCategory}");

    var ar = await raw.SendAsync(HttpMethod.Post, $"/v1/code/sessions/{id}/archive", new JsonObject());
    Log(q, "archive", ar.Describe());
    Console.WriteLine($"[q6] archive: POST /v1/code/sessions/{id}/archive -> HTTP {ar.Status}: {Trunc(ar.Text, 400)}");
    if (ar.Status == 404)
    {
        var ar2 = await raw.SendAsync(HttpMethod.Post, $"/v1/sessions/{id}/archive", new JsonObject());
        Log(q, "archive-fallback", ar2.Describe());
        Console.WriteLine($"[q6] archive fallback: POST /v1/sessions/{id}/archive -> HTTP {ar2.Status}: {Trunc(ar2.Text, 400)}");
    }
    try
    {
        var end = await client.GetAsync(id);
        LogSession(q, "after-archive", end);
        Console.WriteLine($"[q6] after archive: session_status={end.SessionStatus} connection={end.ConnectionStatus} category={end.Summary?.StatusCategory}");
    }
    catch (CloudSessionApiException e)
    {
        Console.WriteLine($"[q6] get after archive -> HTTP {e.Status}: {Trunc(e.Body, 200)}");
    }
}

// Q7: does running a claude command refresh an expired token? Only expiry timestamps are read; the token is never shown.
async Task Q7()
{
    var q = "q7";
    var a = creds.Read();
    Console.WriteLine($"[q7] expiresAt before: {a.ExpiresAt:o}  expired={a.ExpiresAt <= DateTimeOffset.UtcNow}  (now {DateTimeOffset.UtcNow:o})");
    var probe1 = await raw.SendAsync(HttpMethod.Get, "/v1/sessions?limit=1", null);
    Console.WriteLine($"[q7] API probe before `claude auth status`: HTTP {probe1.Status}");
    var status = RunProcess("claude", "auth status");
    Console.WriteLine($"[q7] `claude auth status` output:\n{Trunc(status, 1200)}");
    var b = creds.Read();
    Console.WriteLine($"[q7] expiresAt after:  {b.ExpiresAt:o}  expired={b.ExpiresAt <= DateTimeOffset.UtcNow}");
    var probe2 = await raw.SendAsync(HttpMethod.Get, "/v1/sessions?limit=1", null);
    Console.WriteLine($"[q7] API probe after:  HTTP {probe2.Status}");
    Console.WriteLine($"[q7] expiresAt changed: {a.ExpiresAt != b.ExpiresAt}. " +
                      (a.ExpiresAt > DateTimeOffset.UtcNow
                          ? "Token was NOT expired at start, so this run cannot show refresh; rerun when expired."
                          : "Token was expired at start."));
    Log(q, "result", new JsonObject
    {
        ["expires_before"] = a.ExpiresAt.ToString("o"),
        ["expires_after"] = b.ExpiresAt.ToString("o"),
        ["probe_before_status"] = probe1.Status,
        ["probe_after_status"] = probe2.Status,
        ["auth_status_output"] = status,
    });
    await Task.CompletedTask;
}

// ------------------------------------------------------------------------------------------------ helpers

string LeadPrompt() =>
    "You are the lead of a throwaway spike. Use your `mcp__claude-code-remote__*` tools (create_session, send_message, get_session, list_events) to orchestrate two peer sessions. Steps:\n" +
    $"1. Call create_session twice, once per unit, with these properties:\n" +
    $"   - unit a: tags [\"fyrd\",\"{SpikeTag}\",\"fyrd-role-implementer\",\"fyrd-unit-a\"], source_url \"{Repo}\", outcome_branch \"{BranchA}\", permission_mode \"acceptEdits\"\n" +
    $"   - unit b: tags [\"fyrd\",\"{SpikeTag}\",\"fyrd-role-implementer\",\"fyrd-unit-b\"], source_url \"{Repo}\", outcome_branch \"{BranchB}\", permission_mode \"acceptEdits\"\n" +
    "   Also pass the same environment this session uses if the tool needs one. Each child's first message: " +
    "\"Create a file spikes/fyrd/<unit>.txt (unit is a or b) containing one line 'hello from <unit>', commit it with message 'spike: unit <unit>', and push to your outcome branch. Reply 'pushed' when done.\"\n" +
    "2. Poll get_session / list_events for each child until both report they pushed (or 20 minutes pass).\n" +
    $"3. In YOUR checkout, create branch {BranchIntegration} from the default branch, merge origin/{BranchA} and origin/{BranchB} into it, and push it to origin.\n" +
    $"SAFETY RULES: never force-push (no --force, no --force-with-lease, no deleting branches). Push ONLY these three branches: {BranchA}, {BranchB}, {BranchIntegration}. " +
    "Only create, message, interrupt, or archive the two implementer sessions you create in this run; do not touch any other session (do not list, message, interrupt, or archive existing ones).\n" +
    "4. Finish with a short report: the exact create_session tool parameters you used, both child session ids, and for each of the three branches whether the push succeeded (include any error text). Never modify any other branch.";

CreateSessionRequest NewReq(string title, string[] tags, string first) =>
    new(title, tags, envId, first, Model, null, new SessionSource(Repo, null));

void LogSession(string q, string label, CloudSession s) => Log(q, label, SessionNode(s));

JsonNode SessionNode(CloudSession s) => JsonNode.Parse(s.Raw.GetRawText())!;

void Log(string q, string label, JsonNode? payload)
{
    var line = new JsonObject
    {
        ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
        ["label"] = label,
        ["response"] = payload?.DeepClone(),
    };
    File.AppendAllText(Path.Combine(outDir, q + ".jsonl"), line.ToJsonString() + Environment.NewLine);
}

void SaveLead(string id) => File.WriteAllText(Path.Combine(outDir, "last-lead.txt"), id);

string? LoadLead()
{
    var p = Path.Combine(outDir, "last-lead.txt");
    return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
}

static bool IsEnvManagerLog(SessionEvent e) =>
    e.Type.Contains("env_manager", StringComparison.OrdinalIgnoreCase)
    || e.Raw.GetRawText().Contains("env_manager_log", StringComparison.Ordinal);

static SessionEvent? LastAssistant(IReadOnlyList<SessionEvent> events) =>
    events.Where(e => e.Type == "assistant" && !string.IsNullOrWhiteSpace(e.Text)).LastOrDefault();

static string? ExtractQuestion(string? text)
{
    if (text is null) return null;
    var m = Regex.Match(text, "```fyrd-question\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline);
    return m.Success ? m.Groups[1].Value : null;
}

static IEnumerable<string> TagsOf(JsonNode? n) =>
    n?["tags"] is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "") : [];

static string Trunc(string? s, int n) => s is null ? "(none)" : s.Length <= n ? s : s[..n] + "...";

// Fetches every event, ordered by created_at (stable on ties). Pagination direction is part of what the spike learns,
// so every page is logged.
async Task<List<SessionEvent>> FetchAllEvents(string q, string id, bool log = true)
{
    var all = new List<SessionEvent>();
    string? after = null;
    for (var page = 0; page < 30; page++)
    {
        var p = await client.ListEventsAsync(id, 100, after);
        if (log) Log(q, "events-page", new JsonObject
        {
            ["session"] = id, ["has_more"] = p.HasMore, ["first_id"] = p.FirstId, ["last_id"] = p.LastId,
            ["events"] = new JsonArray(p.Events.Select(e => JsonNode.Parse(e.Raw.GetRawText())).ToArray()),
        });
        all.AddRange(p.Events.Where(e => all.All(x => x.Uuid != e.Uuid || x.Uuid == "")));
        if (!p.HasMore || p.LastId is null || p.LastId == after) break;
        after = p.LastId;
    }
    return all.Select((e, i) => (e, i)).OrderBy(t => t.e.CreatedAt).ThenBy(t => t.i).Select(t => t.e).ToList();
}

async Task<List<CloudSession>> ListAllSessions(string q)
{
    var result = new List<CloudSession>();
    string? after = null;
    for (var page = 0; page < 10; page++)
    {
        var path = "/v1/sessions?limit=100" + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after));
        var r = await raw.SendAsync(HttpMethod.Get, path, null);
        Log(q, "list-sessions-page", r.Describe());
        if (r.Json?["data"] is not JsonArray arr) break;
        foreach (var n in arr)
        {
            if (n?["id"]?.GetValue<string>() is not { } sid) continue;
            result.Add(new CloudSession(sid, n["title"]?.GetValue<string>(), n["session_status"]?.GetValue<string>(),
                n["connection_status"]?.GetValue<string>(), null, TagsOf(n).ToList(),
                JsonSerializer.Deserialize<JsonElement>(n.ToJsonString())));
        }
        var hasMore = r.Json["has_more"]?.GetValue<bool>() == true;
        var lastId = r.Json["last_id"]?.GetValue<string>();
        if (!hasMore || lastId is null || lastId == after) break;
        after = lastId;
    }
    return result;
}

// Uuids of every event currently in the session; pass to WaitSettled so only work after this point counts.
async Task<HashSet<string>> Baseline(string id) =>
    (await FetchAllEvents("baseline", id, log: false)).Select(e => e.Uuid).ToHashSet();

// Polls every 10 s. Settled requires a NEW assistant event (not in baseline, scanned across all pages) AND either
// session_status / post_turn_summary.status_category looking idle/completed, or no new events for 60 s (180 s while the
// status still looks like running). Terminal statuses (failed, archived, ...) end the wait immediately.
// A timeout is announced loudly and returned as a string starting with "TIMEOUT".
async Task<string> WaitSettled(string q, string id, StatusSeen seen, TimeSpan timeout, HashSet<string>? baseline = null)
{
    baseline ??= [];
    var deadline = DateTimeOffset.UtcNow + timeout;
    var lastNewCount = -1;
    var stable = 0;
    var lastLine = "";
    var terminal = new[] { "failed", "archived", "terminated", "error", "stopped" };
    var idleish = new[] { "idle", "completed", "complete", "done", "waiting", "needs_input", "awaiting_input", "blocked", "finished" };
    var runningish = new[] { "running", "starting", "provisioning", "queued", "pending", "creating", "working", "in_progress", "active" };
    while (DateTimeOffset.UtcNow < deadline)
    {
        var s = await client.GetAsync(id);
        seen.Record(s);
        Log(q, "poll", SessionNode(s));
        var fresh = (await FetchAllEvents(q, id, log: false)).Where(e => !baseline.Contains(e.Uuid)).ToList();
        var newAssistant = fresh.Any(e => e.Type == "assistant");
        var line = $"session_status={s.SessionStatus} connection={s.ConnectionStatus} category={s.Summary?.StatusCategory} needs_action={s.Summary?.NeedsAction} new_events={fresh.Count}";
        if (line != lastLine) { Console.WriteLine($"  [{DateTimeOffset.Now:HH:mm:ss}] {line}"); lastLine = line; }
        var st = s.SessionStatus?.ToLowerInvariant();
        var cat = s.Summary?.StatusCategory?.ToLowerInvariant();
        if (st is not null && terminal.Contains(st)) return $"terminal status {st}";
        stable = fresh.Count == lastNewCount ? stable + 1 : 0;
        lastNewCount = fresh.Count;
        if (newAssistant)
        {
            if ((st is not null && idleish.Contains(st)) || (cat is not null && idleish.Contains(cat)))
                return $"status idle/completed (session_status={st}, category={cat})";
            var running = st is not null && runningish.Contains(st);
            if (stable >= (running ? 18 : 6)) return $"no new events for {stable * 10} s (session_status={st})";
        }
        await Task.Delay(TimeSpan.FromSeconds(10));
    }
    var msg = $"TIMEOUT after {timeout.TotalMinutes:0} min without settling (no new assistant event or session never looked idle); results below may be incomplete";
    Console.WriteLine($"  !!! {msg}");
    return msg;
}

static string RunProcess(string file, string arguments)
{
    try
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return $"(exit {p.ExitCode})\n{o}".Trim();
    }
    catch (Exception e)
    {
        return $"(failed to run {file}: {e.Message})";
    }
}

static string? TakeOpt(List<string> a, string name)
{
    var i = a.IndexOf(name);
    if (i < 0) return null;
    var v = i + 1 < a.Count ? a[i + 1] : null;
    a.RemoveRange(i, v is null ? 1 : 2);
    return v;
}

static string FindOutDir()
{
    var d = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (d is not null)
    {
        if (Directory.Exists(Path.Combine(d.FullName, "spikes", "cloud-sessions")))
            return Path.Combine(d.FullName, "spikes", "cloud-sessions", "out");
        d = d.Parent;
    }
    return Path.Combine(Directory.GetCurrentDirectory(), "out");
}

static int Usage()
{
    Console.WriteLine("""
        usage: dotnet run --project spikes/cloud-sessions -- <command> [options]
          q1 [--lead <id>]            peer tools present?
          q2 [--lead <id>]            lead spawns 2 tagged implementers, merge into integration (also tracks q5 statuses)
          q3                          discovery by tag
          q4 ask                      lead asks a fyrd-question; then STOP for 2h+
          q4 answer <sessionId>       answer it, check context + env reprovisioning
          q5 [<id> ...]               poll statuses every 10 s (default: last lead + q2 children)
          q6 [<childId>] [--fresh]    interrupt then archive
          q7                          token expiry / refresh check
        options: --env <id> (default env_011v4b3WBhRMaGGti6Qw747c)   --timeout <minutes> (default 30)
        """);
    return 2;
}

// ------------------------------------------------------------------------------------------------ types

// Raw HTTP for endpoints CloudSessionClient lacks. Same four headers; never logs the token or any request headers.
sealed class RawApi(HttpClient http, ClaudeCredentials credentials)
{
    private static readonly Uri Base = new("https://api.anthropic.com");

    public async Task<RawResponse> SendAsync(HttpMethod method, string pathAndQuery, JsonNode? body)
    {
        var auth = credentials.Read();
        using var req = new HttpRequestMessage(method, new Uri(Base, pathAndQuery));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        req.Headers.Add("x-organization-uuid", auth.OrganizationUuid);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Add("anthropic-beta", "ccr-byoc-2025-07-29");
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch (JsonException) { }
        return new RawResponse(method.Method, pathAndQuery, (int)resp.StatusCode, text, json);
    }
}

sealed record RawResponse(string Method, string Path, int Status, string Text, JsonNode? Json)
{
    public JsonNode Describe() => new JsonObject
    {
        ["request"] = $"{Method} {Path}",
        ["status"] = Status,
        ["body"] = Json?.DeepClone() ?? JsonValue.Create(Text),
    };
}

// Distinct values seen for the three status fields (Q5).
sealed class StatusSeen
{
    private readonly SortedSet<string> _session = [], _connection = [], _category = [], _needs = [];

    public void Record(CloudSession s)
    {
        _session.Add(s.SessionStatus ?? "(null)");
        _connection.Add(s.ConnectionStatus ?? "(null)");
        _category.Add(s.Summary?.StatusCategory ?? "(null)");
        _needs.Add(s.Summary?.NeedsAction ?? "(null)");
    }

    public void Print()
    {
        Console.WriteLine("[q5] distinct values seen:");
        Console.WriteLine($"  session_status                  : {string.Join(" | ", _session)}");
        Console.WriteLine($"  connection_status               : {string.Join(" | ", _connection)}");
        Console.WriteLine($"  post_turn_summary.status_category: {string.Join(" | ", _category)}");
        Console.WriteLine($"  post_turn_summary.needs_action   : {string.Join(" | ", _needs)}");
    }
}
