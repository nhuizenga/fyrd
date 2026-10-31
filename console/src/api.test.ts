import { vi } from "vitest";
import { updateConfig } from "./api";
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
