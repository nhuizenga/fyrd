import { vi } from "vitest";
import { addBudget, createController, openController, retireController, updateConfig } from "./api";
import { config } from "./test/fixtures";

afterEach(() => vi.unstubAllGlobals());

it("updateConfig merges a partial over the loaded config and PUTs the full object", async () => {
  const loaded = config({ sessionCap: 5 });
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

it("createController posts to create; retireController posts to retire", async () => {
  const fetchMock = vi.fn(async (path: string, _init?: RequestInit) =>
    path.endsWith("/retire")
      ? new Response(null, { status: 204 })
      : new Response(JSON.stringify({ sessionId: "session_a", url: "https://claude.ai/code/session_a" }), { status: 200 }));
  vi.stubGlobal("fetch", fetchMock);

  expect((await createController()).sessionId).toBe("session_a");
  expect(await retireController()).toBeUndefined();

  expect(fetchMock.mock.calls[0][0]).toBe("/api/hub/controller/create");
  expect(fetchMock.mock.calls[0][1]?.method).toBe("POST");
  expect(fetchMock.mock.calls[1][0]).toBe("/api/hub/controller/retire");
  expect(fetchMock.mock.calls[1][1]?.method).toBe("POST");
});

it("errors carry the server detail and HTTP status", async () => {
  vi.stubGlobal("fetch", vi.fn(async () =>
    new Response(JSON.stringify({ detail: "No Controller yet." }), { status: 404 })));
  await expect(openController()).rejects.toMatchObject({ message: "No Controller yet.", status: 404 });
});

it("addBudget posts the amount to the session's budget endpoint", async () => {
  const fetchMock = vi.fn(async (_p: string, _i?: RequestInit) => new Response(JSON.stringify({ limitUsd: 75 }), { status: 200 }));
  vi.stubGlobal("fetch", fetchMock);
  await addBudget("s/1", 25);
  expect(fetchMock.mock.calls[0][0]).toBe("/api/hub/sessions/s%2F1/budget");
  expect(JSON.parse(fetchMock.mock.calls[0][1]!.body as string)).toEqual({ amountUsd: 25 });
});

it("uploadFile posts multipart to the uploads endpoint; deleteUpload forgets an id; sendMessage carries fileIds only when given", async () => {
  const fetchMock = vi.fn(async (path: string, init?: RequestInit) =>
    init?.method === "DELETE" ? new Response(null, { status: 204 })
      : path.endsWith("/uploads") ? new Response(JSON.stringify({ id: "file_1", name: "a.txt", mimeType: "text/plain", size: 1 }), { status: 200 })
        : new Response(null, { status: 204 }));
  vi.stubGlobal("fetch", fetchMock);
  const { uploadFile, deleteUpload, sendMessage } = await import("./api");

  expect((await uploadFile(new File(["x"], "a.txt", { type: "text/plain" }))).id).toBe("file_1");
  const up = fetchMock.mock.calls[0];
  expect(up[0]).toBe("/api/hub/uploads");
  expect(up[1]?.method).toBe("POST");
  expect((up[1]?.body as FormData).get("file")).toBeInstanceOf(File);
  expect((up[1]?.headers as unknown) ?? undefined).toBeUndefined(); // the browser sets the multipart boundary

  await deleteUpload("file_1");
  expect(fetchMock.mock.calls[1][0]).toBe("/api/hub/uploads/file_1");
  expect(fetchMock.mock.calls[1][1]?.method).toBe("DELETE");

  await sendMessage("s", "hi");
  await sendMessage("s", "hi", ["file_1"]);
  expect(JSON.parse(fetchMock.mock.calls[2][1]?.body as string)).toEqual({ text: "hi" });
  expect(JSON.parse(fetchMock.mock.calls[3][1]?.body as string)).toEqual({ text: "hi", fileIds: ["file_1"] });
});

it("uploadFile rejects with the server's detail and status", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ detail: "The file is too large" }), { status: 413 })));
  const { uploadFile } = await import("./api");
  await expect(uploadFile(new File(["x"], "a.txt"))).rejects.toMatchObject({ message: "The file is too large", status: 413 });
});
