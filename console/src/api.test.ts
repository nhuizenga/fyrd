import { vi } from "vitest";
import { adoptController, openController, retireController, updateConfig } from "./api";
import { config } from "./test/fixtures";

afterEach(() => vi.unstubAllGlobals());

it("updateConfig merges a partial over the loaded config and PUTs the full object", async () => {
  const loaded = config({ squadCap: 5 });
  const fetchMock = vi.fn(async (_path: string, init?: RequestInit) => {
    const body = init?.method === "PUT" ? JSON.parse(init.body as string) : loaded;
    return new Response(JSON.stringify(body), { status: 200 });
  });
  vi.stubGlobal("fetch", fetchMock);

  const saved = await updateConfig({ sessionCap: 7 });

  const put = fetchMock.mock.calls.find(([, init]) => init?.method === "PUT")!;
  expect(put[0]).toBe("/api/config");
  expect(JSON.parse(put[1]!.body as string)).toEqual({ ...loaded, sessionCap: 7 });
  expect(saved.sessionCap).toBe(7);
});

it("adoptController posts the session id; retireController posts to retire", async () => {
  const fetchMock = vi.fn(async (path: string, _init?: RequestInit) =>
    path.endsWith("/retire")
      ? new Response(null, { status: 204 })
      : new Response(JSON.stringify({ sessionId: "session_a", url: "https://claude.ai/code/session_a" }), { status: 200 }));
  vi.stubGlobal("fetch", fetchMock);

  expect((await adoptController("session_a")).sessionId).toBe("session_a");
  expect(await retireController()).toBeUndefined();

  expect(fetchMock.mock.calls[0][0]).toBe("/api/hub/controller/adopt");
  expect(fetchMock.mock.calls[0][1]?.method).toBe("POST");
  expect(JSON.parse(fetchMock.mock.calls[0][1]?.body as string)).toEqual({ sessionId: "session_a" });
  expect(fetchMock.mock.calls[1][0]).toBe("/api/hub/controller/retire");
  expect(fetchMock.mock.calls[1][1]?.method).toBe("POST");
});

it("errors carry the server detail and HTTP status", async () => {
  vi.stubGlobal("fetch", vi.fn(async () =>
    new Response(JSON.stringify({ detail: "No Controller yet." }), { status: 404 })));
  await expect(openController()).rejects.toMatchObject({ message: "No Controller yet.", status: 404 });
});
