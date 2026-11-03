import { expect, test, type Locator } from "@playwright/test";
import { LONG_TITLE, QUESTION, SESSIONS, docsTranscript, item, longTranscript, mockApi, prStatus, session } from "./mockApi";

const fontOf = (l: Locator) => l.evaluate((el) => getComputedStyle(el).fontFamily);
const box = async (l: Locator) => (await l.boundingBox())!;

test.describe("Sessions", () => {
  test.beforeEach(async ({ page }) => {
    await mockApi(page, { transcripts: { session_b1: longTranscript(), session_b2: docsTranscript() }, questions: [QUESTION] });
  });

  test("deep link opens the session; panes scroll on their own and the composer stays visible", async ({ page }) => {
    await page.goto("/#/sessions/session_b1");
    await expect(page.getByRole("heading", { name: "builder-prime-sieve" })).toBeVisible();
    const log = page.getByRole("log", { name: "Messages" });
    await expect(log.getByText("Step 30:")).toBeVisible();

    // The page itself never scrolls; the thread does.
    const pageScrolls = await page.evaluate(() => document.scrollingElement!.scrollHeight > window.innerHeight);
    expect(pageScrolls).toBe(false);
    expect(await log.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);

    // Newest message in view (the thread is scrolled to its end); the composer is at the bottom of the viewport.
    const gap = () => log.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight);
    await expect.poll(gap).toBeLessThan(50);
    await page.waitForTimeout(500);
    expect(await gap()).toBeLessThan(50);
    await expect(log.getByText("Done")).toBeInViewport();
    const composer = page.getByLabel("Message this session");
    await expect(composer).toBeInViewport();
    expect((await box(composer)).y).toBeGreaterThan(900 - 150);

    // Scrolling the thread leaves the list and details where they are.
    const list = page.getByRole("complementary", { name: "Session list" });
    const before = await box(list);
    await log.hover();
    await page.mouse.wheel(0, -2000);
    await expect.poll(gap).toBeGreaterThan(500);
    expect(await box(list)).toEqual(before);
    await expect(composer).toBeInViewport();
  });

  test("everything uses Nunito Sans", async ({ page }) => {
    await page.goto("/#/sessions/session_b1");
    await expect(page.getByText("Step 1:")).toBeVisible();
    for (const l of [
      page.getByText("Step 1:"),
      page.getByRole("button", { name: /^(?!Export|Archive|Rename).*builder-prime-sieve/ }).locator(".session-row-title"),
      page.getByLabel("Message this session"),
      page.getByRole("button", { name: "Send" }),
    ]) {
      expect(await fontOf(l)).toContain("Nunito Sans");
    }
  });

  test("bubbles: agent left on gray, owner right on blue; markdown renders", async ({ page }) => {
    await page.goto("/#/sessions/session_b1");
    const log = page.getByRole("log", { name: "Messages" });
    const agent = log.locator(".msg-agent").first();
    const user = log.locator(".msg-user").first();
    await expect(user).toHaveText("Build a prime sieve.");
    const logBox = await box(log);
    const [a, u] = [await box(agent), await box(user)];
    expect(a.x - logBox.x).toBeLessThan(30);
    expect(logBox.x + logBox.width - (u.x + u.width)).toBeLessThan(30);
    // Full width apart from a small offset on the opposite side, whatever the text length.
    const short = log.locator(".msg-user").first();
    expect(a.width).toBeGreaterThan(logBox.width * 0.85);
    expect((await box(short)).width).toBeGreaterThan(logBox.width * 0.85);
    expect(u.x - logBox.x).toBeGreaterThan(30);
    const bg = (l: Locator) => l.evaluate((el) => getComputedStyle(el).backgroundColor);
    expect(await bg(agent)).not.toEqual(await bg(user));
    await expect(agent.locator("strong")).toHaveText("progress");
    await expect(agent.locator("code")).toHaveText("code_0");
    await expect(log.locator("table")).toBeVisible();
  });

  test("Enter sends, Shift+Enter adds a line", async ({ page }) => {
    const { sent } = await mockApi(page, { transcripts: { session_b1: longTranscript() } });
    await page.goto("/#/sessions/session_b1");
    const composer = page.getByLabel("Message this session");
    await composer.fill("first");
    await composer.press("Shift+Enter");
    await composer.pressSequentially("second");
    await expect(composer).toHaveValue("first\nsecond");
    await composer.press("Enter");
    await expect(composer).toHaveValue("");
    expect(sent).toContainEqual({ url: "/api/hub/sessions/session_b1/message", body: { text: "first\nsecond" } });
  });

  test("Export downloads the session's events as .jsonl", async ({ page }) => {
    await page.goto("/#/sessions");
    const list = page.getByRole("complementary", { name: "Session list" });
    const download = page.waitForEvent("download");
    await list.getByRole("listitem").filter({ hasText: "builder-docs" }).hover();
    await list.getByRole("button", { name: "Export builder-docs" }).click();
    expect((await download).suggestedFilename()).toMatch(/\.jsonl$/);
  });

  test("list: Controller, its controls, search, then others with status icons; rows highlight on hover", async ({ page }) => {
    await page.goto("/#/sessions");
    const list = page.getByRole("complementary", { name: "Session list" });
    const y = async (l: Locator) => (await box(l)).y;
    const ctl = list.getByRole("button", { name: /^(?!Export|Archive|Rename).*Controller role/ });
    const open = list.getByRole("button", { name: "Open Controller" });
    const search = list.getByLabel("Search sessions");
    const first = list.getByRole("button", { name: /^(?!Export|Archive|Rename).*builder-prime-sieve/ });
    // Open/Replace are icons on the Controller's row, to the right of its name; they appear after a short hover.
    await expect(open).toBeHidden();
    await ctl.hover();
    await expect(open).toBeVisible();
    const replace = list.getByRole("button", { name: "Replace Controller" });
    for (const b of [open, replace]) {
      expect(Math.abs((await y(b)) + (await box(b)).height / 2 - ((await y(ctl)) + (await box(ctl)).height / 2))).toBeLessThan(6);
      expect((await box(b)).x).toBeGreaterThan((await box(ctl)).x + 100);
      await expect(b).toHaveText("");
      await expect(b.locator("svg")).toBeVisible();
    }
    expect(await y(open)).toBeLessThan(await y(search));
    // Rows carry the status icon, title and repo chip.
    await expect(first).toHaveText(/^builder-prime-sievesieve$/);
    expect(await y(search)).toBeLessThan(await y(first));
    await expect(first.getByRole("img", { name: "Running" })).toBeVisible();
    await expect(list.getByRole("button", { name: /^(?!Export|Archive|Rename).*builder-docs/ }).getByRole("img", { name: "Waiting for you" })).toBeVisible();
    const bg = (l: Locator) => l.evaluate((el) => getComputedStyle(el).backgroundColor);
    const rows = list.locator(".session-list.pane-scroll .row-btn");
    const bgs = await rows.evaluateAll((els) => els.map((el) => getComputedStyle(el).backgroundColor));
    expect(new Set(bgs).size).toBe(1); // no zebra stripes
    const docs = list.getByRole("button", { name: /^(?!Export|Archive|Rename).*builder-docs/ });
    const resting = await bg(docs);
    await docs.hover();
    await expect.poll(() => bg(docs)).not.toBe(resting);

    await search.fill("docs");
    await expect(ctl).toBeVisible();
    await expect(first).toBeHidden();
  });

  test("details pane order and relative times", async ({ page }) => {
    await page.goto("/#/sessions/session_b1");
    const pane = page.getByRole("complementary", { name: "Session details" });
    await expect(pane.locator("h4")).toHaveText(["Estimated cost", "Details", "Pull requests", "Tags"]);
    await expect(pane.locator("dt")).toHaveText(["Role", "Repository", "Branch", "Started", "Last activity"]);
    await expect(pane.locator("dd").nth(3)).toHaveText("1 day ago");
    await expect(pane.locator("dd").nth(4)).toHaveText("5 minutes ago");
  });

  test("cost block shows spend against the limit and +$25 posts a raise", async ({ page }) => {
    const { sent } = await mockApi(page, { transcripts: {} });
    await page.goto("/#/sessions/session_b1");
    await expect(page.getByText("Fyrd today: $120.00 of $500.00")).toBeVisible();
    const pane = page.getByRole("complementary", { name: "Session details" });
    await expect(pane.getByText("$47.50 (estimated)")).toBeVisible();
    await expect(pane.getByRole("progressbar", { name: "95 percent of today's $50.00 limit used" })).toBeVisible();
    await pane.getByRole("button", { name: "+$25 to today's limit" }).click();
    await expect.poll(() => sent).toContainEqual({ url: "/api/hub/sessions/session_b1/budget", body: { amountUsd: 25 } });
    await expect(pane.getByText("63% of $75.00 limit today")).toBeVisible();
  });

  test("a pending question is answerable in the thread; Other reveals the text box", async ({ page }) => {
    const { sent } = await mockApi(page, { transcripts: { session_b2: docsTranscript() }, questions: [QUESTION] });
    await page.goto("/#/sessions/session_b2");
    const card = page.getByRole("region", { name: "Question waiting for you" });
    await expect(card.getByRole("button", { name: "Markdown" })).toBeVisible();
    await expect(card.getByLabel("Your answer")).toHaveCount(0);
    await card.getByRole("button", { name: "Other" }).click();
    await card.getByLabel("Your answer").fill("reStructuredText");
    await card.getByRole("button", { name: "Send answer" }).click();
    await expect.poll(() => sent).toContainEqual({ url: "/api/hub/questions/session_b2%3Areq1%3A0/answer", body: { text: "reStructuredText" } });
  });

  test("Add repository sends the attach request", async ({ page }) => {
    const { sent } = await mockApi(page, { transcripts: {} });
    await page.goto("/#/sessions/session_b3");
    await page.getByLabel("Add GitHub repository").fill("https://github.com/acme/widgets");
    await page.getByRole("button", { name: "Add repository" }).click();
    await expect(page.getByRole("status")).toHaveText("Asked the session to attach acme/widgets.");
    expect(JSON.stringify(sent)).toContain("owner `acme`, repo `widgets`");
  });
});

test.describe("Workers", () => {
  const WORKING = "session_b1";
  test.beforeEach(async ({ page }) => {
    const sessions = SESSIONS.map((s) => s.id === WORKING
      ? session({ ...s, status: "idle", tags: [...s.tags, "fyrd-workers-running"], workersRunning: true }) : s);
    await mockApi(page, {
      sessions,
      transcripts: {
        [WORKING]: [
          item({ kind: "worker", text: "build: initial task", detail: "fork", toolUseId: "toolu_w1" }),
          item({ kind: "worker-result", text: "Status: done\nHand-off: pushed abc123", status: "completed", toolUseId: "toolu_w1" }),
        ],
      },
      progress: {
        [WORKING]: item({
          key: "ephemeral", seq: 0, kind: "progress", status: "Waiting for workers",
          workers: [{ id: "toolu_w2", description: "fix: validator round 1", line: "Running Bash · 42s" }],
        }),
      },
    });
  });

  test("the list shows a workers chip with a count and the header says Working (background)", async ({ page }) => {
    await page.goto(`/#/sessions/${WORKING}`);
    const list = page.getByRole("complementary", { name: "Session list" });
    await expect(list.getByLabel(/^\d+ workers? running$/)).toBeVisible();
    await expect(page.getByText("Working (background)")).toBeVisible();
  });

  test("worker card, collapsed result and the running workers list render in the thread", async ({ page }) => {
    await page.goto(`/#/sessions/${WORKING}`);
    await expect(page.getByText("Worker started")).toBeVisible();
    const result = page.locator("details.msg-worker-result");
    await expect(result.locator("summary")).toHaveText("Worker result (completed)");
    await expect(result).not.toHaveAttribute("open", "");
    await expect(result.getByText("pushed abc123")).toBeHidden();
    await result.locator("summary").click();
    await expect(result.getByText(/pushed abc123/)).toBeVisible();
    const running = page.getByRole("list", { name: "Running workers" });
    await expect(running.getByText("fix: validator round 1")).toBeVisible();
    await expect(running.getByText(/Running Bash · 42s/)).toBeVisible();
  });
});

test.describe("Sessions, more", () => {
  test("copy appears on hover and copies the message", async ({ page, context }) => {
    await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    await mockApi(page, { transcripts: { session_b1: longTranscript() } });
    await page.goto("/#/sessions/session_b1");
    const bubble = page.locator(".msg-user").first();
    const copy = bubble.getByRole("button", { name: "Copy message" });
    await expect(copy).toHaveCSS("opacity", "0");
    await bubble.hover();
    await expect(copy).toHaveCSS("opacity", "1");
    await copy.click();
    await expect(bubble.getByRole("button", { name: "Copied" })).toBeVisible();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe("Build a prime sieve.");
  });

  test("the Sessions tab reopens the session viewed last", async ({ page }) => {
    await mockApi(page, { transcripts: {} });
    await page.goto("/#/sessions/session_b3");
    await expect(page.getByRole("heading", { name: "reviewer-api" })).toBeVisible();
    await page.getByRole("button", { name: "Inbox" }).click();
    await page.getByRole("button", { name: "Sessions" }).click();
    await expect(page.getByRole("heading", { name: "reviewer-api" })).toBeVisible();
    await expect(page).toHaveURL(/#\/sessions\/session_b3$/);
  });

  test("the Inbox count badge is fully inside the viewport", async ({ page }) => {
    await mockApi(page, { questions: [QUESTION], transcripts: {} });
    await page.goto("/#/sessions");
    const badge = page.getByRole("navigation", { name: "Primary" }).getByText("1", { exact: true });
    await expect(badge).toBeVisible();
    expect((await box(badge)).y).toBeGreaterThanOrEqual(0);
  });
});

test.describe("Inbox", () => {
  test("conversation above the answer, collapsed to its tail, expandable", async ({ page }) => {
    await mockApi(page, { transcripts: { session_b2: docsTranscript() }, questions: [QUESTION] });
    await page.goto("/#/inbox");
    const log = page.getByRole("log", { name: "Conversation" });
    await expect(log.getByText("Line five, the latest.")).toBeInViewport();
    const answer = page.getByRole("button", { name: "Markdown" });
    expect((await box(log)).y).toBeLessThan((await box(answer)).y);
    const collapsed = (await box(log)).height;
    expect(collapsed).toBeLessThan(140);
    await expect(page.getByLabel("Your answer")).toHaveCount(0);

    await page.getByRole("button", { name: "Show whole conversation" }).click();
    await expect(log.getByText("Write the docs.")).toBeVisible();
    expect((await box(log)).height).toBeGreaterThan(collapsed);
    await expect(answer).toBeInViewport();
  });
});

test.describe("Settings and shell", () => {
  test.beforeEach(async ({ page }) => {
    await mockApi(page);
  });

  test("left nav, one section at a time, each with a deep link", async ({ page }) => {
    await page.goto("/#/settings");
    const nav = page.getByRole("navigation", { name: "Settings sections" });
    await expect(page.getByRole("heading", { name: "Environment" })).toBeVisible();
    await nav.getByRole("button", { name: "Personas" }).click();
    await expect(page).toHaveURL(/#\/settings\/personas$/);
    await expect(page.getByRole("heading", { name: "Environment" })).toHaveCount(0);
    const navBox = await box(nav);
    const heading = await box(page.getByRole("heading", { name: "Personas" }));
    expect(navBox.x).toBeLessThan(heading.x);
    expect(await fontOf(page.getByRole("button", { name: /^Builder/ }))).toContain("Nunito Sans");
  });

  test("personas rename except the Controller", async ({ page }) => {
    await page.goto("/#/settings/personas");
    await page.getByRole("button", { name: /^Controller/ }).click();
    await expect(page.getByLabel("Name")).toBeDisabled();
    await page.getByRole("button", { name: /^Builder/ }).click();
    await expect(page.getByLabel("Name")).toBeEnabled();
  });

  test("memory: pencil edits, trash deletes at once", async ({ page }) => {
    const { sent } = await mockApi(page, {
      memory: [{ id: "m1", text: "Always use TDD", source: "owner", at: new Date().toISOString() }],
    });
    await page.goto("/#/settings/memory");
    const row = page.locator(".memory-row", { hasText: "Always use TDD" });
    await expect(row.getByRole("button", { name: "Edit" }).locator("svg")).toBeVisible();
    await expect(row.getByRole("button", { name: "Edit" })).toHaveText("");
    await row.getByRole("button", { name: "Delete" }).click();
    await expect.poll(() => sent.map((x) => x.url)).toContain("/api/hub/memory/shared/m1");
  });

  test("app icon is served and shown in the header", async ({ page }) => {
    await page.goto("/");
    const icons = await page.locator('link[rel="icon"]').evaluateAll((ls) => ls.map((l) => (l as HTMLLinkElement).href));
    expect(icons.length).toBeGreaterThan(0);
    for (const href of icons) expect((await page.request.get(href)).ok()).toBe(true);
    const logo = page.locator(".brand img");
    await expect(logo).toBeVisible();
    expect(await logo.evaluate((img) => (img as HTMLImageElement).naturalWidth)).toBeGreaterThan(0);
  });
});

test.describe("Stacked PRs", () => {
  test("the details pane shows the chain line, and the PR menu offers Merge stack", async ({ page }) => {
    await mockApi(page, { stack: true });
    await page.goto("/#/sessions/session_stack");
    const details = page.getByRole("complementary", { name: "Session details" });
    await expect(details.getByText("main ← #11 ← #12 ← #13 (top)")).toBeVisible();
    await details.getByRole("button", { name: "Ask for help with #12" }).click();
    await expect(page.getByRole("menuitem", { name: "Merge stack" }).first()).toBeVisible();
  });

  test("the Merge stack confirm scrolls into view in a short viewport", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 560 });
    await mockApi(page, { stack: true });
    await page.goto("/#/sessions/session_stack");
    const details = page.getByRole("complementary", { name: "Session details" });
    const trigger = details.getByRole("button", { name: "Ask for help with #12" });
    await trigger.click();
    const menuId = await trigger.getAttribute("aria-controls");
    await page.locator(`[id="${menuId}"]`).getByRole("menuitem", { name: "Merge stack" }).click();
    await expect(details.getByText("#13 → #12 → #11 into main, squash, waiting for checks between steps")).toBeInViewport();
    await expect(details.getByRole("button", { name: "Merge stack" })).toBeInViewport();
  });

  test("a running stack merge shows its step line and a Cancel button", async ({ page }) => {
    await mockApi(page, {
      stack: true,
      stackMerges: [{
        id: "j1", repo: "acme/stack", prs: ["https://github.com/acme/stack/pull/13", "https://github.com/acme/stack/pull/12", "https://github.com/acme/stack/pull/11"],
        index: 1, status: "waiting", message: null, startedAt: new Date().toISOString(), updatedAt: new Date().toISOString(),
      }],
    });
    await page.goto("/#/sessions/session_stack");
    const details = page.getByRole("complementary", { name: "Session details" });
    await expect(details.getByText("Waiting for checks on #12 (step 2 of 3)")).toBeVisible();
    await expect(details.getByRole("button", { name: "Cancel stack merge" })).toBeVisible();
  });
});

test.describe("Pull request details", () => {
  const prs = [
    prStatus({ number: 20, title: LONG_TITLE, draft: true, canMerge: false, base: "fyrd/github-api", checks: "failing", checksPassed: 8, checksFailed: 1, checksPending: 2,
      failingCheck: { name: "lint", url: "https://ci.example/lint" }, review: "commented", mergeable: "conflicting", mergeState: "behind" }),
    prStatus({ number: 21, title: "Short title" }),
  ];

  test("a long title truncates inside the pane and the action buttons stay visible", async ({ page }) => {
    await mockApi(page, { prs });
    await page.goto("/#/sessions/session_b1");
    const details = page.getByRole("complementary", { name: "Session details" });
    const title = details.getByText(LONG_TITLE);
    await expect(title).toBeVisible();
    await expect(title).toHaveAttribute("title", LONG_TITLE);
    const pane = await box(details);
    const open = await box(details.getByRole("button", { name: "Open #20" }));
    expect(open.x + open.width).toBeLessThanOrEqual(pane.x + pane.width + 1);
    expect(await details.evaluate((el) => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
    const t = await box(title);
    expect(t.x + t.width).toBeLessThanOrEqual(pane.x + pane.width + 1);
    await expect(details.getByRole("button", { name: "Open #21" })).toBeVisible();
  });

  test("shows counts on a draft, the base, review, conflicts and behind badges, and no Merge on a conflict", async ({ page }) => {
    await mockApi(page, { prs });
    await page.goto("/#/sessions/session_b1");
    const details = page.getByRole("complementary", { name: "Session details" });
    await expect(details.getByTitle("8 checks passed")).toBeVisible();
    await expect(details.getByTitle(/^1 check failed/)).toBeVisible();
    await expect(details.getByTitle("2 checks pending")).toBeVisible();
    const fill = (title: string | RegExp) => details.getByTitle(title).locator("svg").first().evaluate((el) => getComputedStyle(el).fill);
    expect(await fill("8 checks passed")).toBe("rgb(46, 158, 79)");
    expect(await fill(/^1 check failed/)).toBe("rgb(211, 47, 47)");
    expect(await fill("2 checks pending")).toBe("rgb(138, 143, 152)");
    for (const t of ["Draft", "Conflicts", "Behind base", "Reviewed", "→ fyrd/github-api"]) await expect(details.getByText(t, { exact: true })).toBeVisible();
    await expect(details.getByRole("button", { name: "Merge #20" })).toHaveCount(0);
    await expect(details.getByRole("button", { name: "Merge #21" })).toBeVisible();
  });
});
