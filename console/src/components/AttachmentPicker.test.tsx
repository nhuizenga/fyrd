import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { question } from "../test/fixtures";
import Composer from "./Composer";
import QuestionGroup from "./QuestionGroup";
import { MAX_FILE_BYTES, MAX_FILES } from "./AttachmentPicker";

vi.mock("../api");

const file = (name = "notes.txt", body = "hello", type = "text/plain") => new File([body], name, { type });
const bigFile = (name: string) => {
  const f = file(name);
  Object.defineProperty(f, "size", { value: MAX_FILE_BYTES + 1 });
  return f;
};
let seq = 0;
beforeEach(() => {
  vi.resetAllMocks();
  seq = 0;
  localStorage.clear();
  vi.mocked(api.uploadFile).mockImplementation(async (f) => ({ id: `file_${(seq += 1)}`, name: f.name, mimeType: f.type, size: f.size }));
  vi.mocked(api.deleteUpload).mockResolvedValue(undefined);
});

const renderComposer = (onSend = vi.fn().mockResolvedValue(true)) => {
  render(<Composer sessionId="a" sending={false} blocked={null} height={80} onSend={onSend} />);
  return onSend;
};
const attach = (...files: File[]) => userEvent.upload(screen.getByTestId("attach-input"), files);

describe("Composer attachments", () => {
  it("uploads on attach, shows a chip, and sends the file ids with the text", async () => {
    const onSend = renderComposer();
    await attach(file("notes.txt"));
    expect(api.uploadFile).toHaveBeenCalledTimes(1);
    expect(await screen.findByText("notes.txt")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Message this session"), "see attached");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(onSend).toHaveBeenCalledWith("see attached", ["file_1"], ["notes.txt"]);
    await waitFor(() => expect(screen.queryByText("notes.txt")).not.toBeInTheDocument());
  });

  it("can send files with no text", async () => {
    const onSend = renderComposer();
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
    await attach(file());
    await waitFor(() => expect(screen.getByRole("button", { name: "Send" })).toBeEnabled());
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(onSend).toHaveBeenCalledWith("", ["file_1"], ["notes.txt"]);
  });

  it("holds Send while a file is still uploading", async () => {
    let finish!: (v: Awaited<ReturnType<typeof api.uploadFile>>) => void;
    vi.mocked(api.uploadFile).mockReturnValue(new Promise((r) => { finish = r; }));
    renderComposer();
    await userEvent.type(screen.getByLabelText("Message this session"), "hi");
    await attach(file());
    expect(screen.getByText("Uploading…")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
    finish({ id: "file_9", name: "notes.txt", mimeType: "text/plain", size: 5 });
    await waitFor(() => expect(screen.getByRole("button", { name: "Send" })).toBeEnabled());
  });

  it("removing a chip forgets the upload and leaves it out of the send", async () => {
    const onSend = renderComposer();
    await attach(file("a.txt"), file("b.txt"));
    await screen.findByText("b.txt");
    await waitFor(() => expect(screen.queryByText("Uploading…")).not.toBeInTheDocument());
    await userEvent.click(screen.getByRole("button", { name: "Remove a.txt" }));
    expect(api.deleteUpload).toHaveBeenCalledWith("file_1");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(onSend).toHaveBeenCalledWith("", ["file_2"], ["b.txt"]);
  });

  it("shows an upload error on the chip and does not send that file", async () => {
    vi.mocked(api.uploadFile).mockRejectedValue(new Error("Signed out of Claude"));
    const onSend = renderComposer();
    await userEvent.type(screen.getByLabelText("Message this session"), "hi");
    await attach(file());
    expect(await screen.findByText(/notes\.txt: Signed out of Claude/)).toBeInTheDocument();
    expect(screen.getByText("Upload failed")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(onSend).toHaveBeenCalledWith("hi", [], []);
  });

  it("refuses files over 25 MB and empty files without uploading them", async () => {
    renderComposer();
    await attach(bigFile("huge.bin"), file("empty.txt", ""));
    expect(await screen.findByRole("alert")).toHaveTextContent("huge.bin is larger than 25 MB. empty.txt is empty.");
    expect(api.uploadFile).not.toHaveBeenCalled();
  });

  it("allows at most 10 files", async () => {
    renderComposer();
    await attach(...Array.from({ length: MAX_FILES + 1 }, (_, i) => file(`f${i}.txt`)));
    expect(api.uploadFile).toHaveBeenCalledTimes(MAX_FILES);
    expect(await screen.findByRole("alert")).toHaveTextContent(`at most ${MAX_FILES} files`);
  });

  it("accepts dropped and pasted files", async () => {
    renderComposer();
    const box = screen.getByLabelText("Message this session");
    fireEvent.drop(box, { dataTransfer: { files: [file("dropped.txt")], types: ["Files"] } });
    expect(await screen.findByText("dropped.txt")).toBeInTheDocument();
    fireEvent.paste(box, { clipboardData: { files: [file("pasted.png", "x", "image/png")] } });
    expect(await screen.findByText("pasted.png")).toBeInTheDocument();
    expect(api.uploadFile).toHaveBeenCalledTimes(2);
  });

  it("keeps the files when the send fails", async () => {
    renderComposer(vi.fn().mockResolvedValue(false));
    await attach(file());
    await screen.findByText("Uploading…").catch(() => {});
    await waitFor(() => expect(screen.getByRole("button", { name: "Send" })).toBeEnabled());
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(screen.getByText("notes.txt")).toBeInTheDocument();
  });
});

describe("Other answer attachments", () => {
  const renderQ = (q = question({ options: ["Apple", "Banana"] })) => {
    vi.mocked(api.answerQuestion).mockResolvedValue(undefined);
    render(<QuestionGroup group={[q]} onChanged={vi.fn()} />);
  };

  it("offers attach only under Other, and sends the ids with the typed answer", async () => {
    renderQ();
    expect(screen.queryByLabelText("Add files")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Other" }));
    await userEvent.type(screen.getByLabelText("Your answer"), "See the file");
    await attach(file());
    await screen.findByText("notes.txt");
    await userEvent.click(screen.getByRole("button", { name: "Send answer" }));
    expect(api.answerQuestion).toHaveBeenCalledWith("s1:r1:0", { text: "See the file", fileIds: ["file_1"] });
  });

  it("allows an answer that is only files", async () => {
    renderQ();
    await userEvent.click(screen.getByRole("button", { name: "Other" }));
    expect(screen.getByRole("button", { name: "Send answer" })).toBeDisabled();
    await attach(file());
    await waitFor(() => expect(screen.getByRole("button", { name: "Send answer" })).toBeEnabled());
    await userEvent.click(screen.getByRole("button", { name: "Send answer" }));
    expect(api.answerQuestion).toHaveBeenCalledWith("s1:r1:0", { text: "", fileIds: ["file_1"] });
  });

  it("an option answer never carries files", async () => {
    renderQ();
    await userEvent.click(screen.getByRole("button", { name: "Other" }));
    await attach(file());
    await screen.findByText("notes.txt");
    await userEvent.click(screen.getByRole("button", { name: "Banana" }));
    expect(api.answerQuestion).toHaveBeenCalledWith("s1:r1:0", { choice: "Banana" });
  });

  it("multi-select Other sends the picks, the text and the files together", async () => {
    renderQ(question({ multiSelect: true, options: ["A", "B"] }));
    await userEvent.click(screen.getByLabelText("A"));
    await userEvent.click(screen.getByLabelText("Other"));
    await userEvent.type(screen.getByLabelText("Your answer"), "more");
    await attach(file());
    await screen.findByText("notes.txt");
    await waitFor(() => expect(screen.getByRole("button", { name: "Send" })).toBeEnabled());
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(api.answerQuestion).toHaveBeenCalledWith("s1:r1:0", { text: "A, more", fileIds: ["file_1"] });
  });
});
