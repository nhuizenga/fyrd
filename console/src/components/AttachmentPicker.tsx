import { useRef, useState, type ClipboardEvent, type DragEvent } from "react";
import { Button, Icon } from "@servicetitan/anvil2";
import AttachIcon from "@servicetitan/anvil2/assets/icons/material/round/attach_file.svg?react";
import CloseIcon from "@servicetitan/anvil2/assets/icons/material/round/close.svg?react";
import * as api from "../api";

export const MAX_FILES = 10;
export const MAX_FILE_BYTES = 25 * 1024 * 1024;

export interface Attachment {
  key: string;
  name: string;
  size: number;
  status: "uploading" | "ready" | "error";
  /** The Files API id once uploaded. */
  id?: string;
  error?: string;
}

export const formatSize = (bytes: number) =>
  bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1).replace(/\.0$/, "")} MB`
    : bytes >= 1024 ? `${Math.round(bytes / 1024)} KB`
      : `${bytes} B`;

/**
 * The files attached to one message or answer. `add` checks the limits (10 files, 25 MB each) and uploads each file at
 * once; `ids` are the uploaded ones, ready to send. A chip's remove button forgets the upload. `clear` drops the chips
 * after a send without touching the uploads (the server consumed them).
 */
export function useAttachments() {
  const [items, setItems] = useState<Attachment[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const seq = useRef(0);
  const live = useRef(new Set<string>());
  // Mirror of items for limit checks inside one event (several files dropped at once).
  const count = useRef(0);

  const patch = (key: string, change: Partial<Attachment>) =>
    setItems((cur) => cur.map((a) => (a.key === key ? { ...a, ...change } : a)));

  const add = (files: File[]) => {
    const problems: string[] = [];
    for (const file of files) {
      if (count.current >= MAX_FILES) {
        problems.push(`You can attach at most ${MAX_FILES} files.`);
        break;
      }
      if (file.size === 0) { problems.push(`${file.name} is empty.`); continue; }
      if (file.size > MAX_FILE_BYTES) {
        problems.push(`${file.name} is larger than ${MAX_FILE_BYTES / 1024 / 1024} MB.`);
        continue;
      }
      const key = `att-${(seq.current += 1)}`;
      count.current += 1;
      live.current.add(key);
      setItems((cur) => [...cur, { key, name: file.name, size: file.size, status: "uploading" }]);
      api.uploadFile(file).then(
        (up) => {
          if (live.current.has(key)) patch(key, { status: "ready", id: up.id });
          else void api.deleteUpload(up.id).catch(() => {}); // removed while uploading
        },
        (e) => {
          if (live.current.has(key)) patch(key, { status: "error", error: e instanceof Error ? e.message : String(e) });
        },
      );
    }
    setNotice(problems.length ? problems.join(" ") : null);
  };

  const remove = (key: string) => {
    const item = items.find((a) => a.key === key);
    if (!item) return;
    live.current.delete(key);
    count.current -= 1;
    setItems((cur) => cur.filter((a) => a.key !== key));
    setNotice(null);
    if (item.id) void api.deleteUpload(item.id).catch(() => {});
  };

  const clear = () => {
    live.current.clear();
    count.current = 0;
    setItems([]);
    setNotice(null);
  };

  return {
    items,
    notice,
    add,
    remove,
    clear,
    ids: items.filter((a) => a.status === "ready").map((a) => a.id!),
    names: items.filter((a) => a.status === "ready").map((a) => a.name),
    uploading: items.some((a) => a.status === "uploading"),
  };
}

export type Attachments = ReturnType<typeof useAttachments>;

/** Drop and paste handlers for the element that wraps a message box. */
export function attachmentHandlers(att: Attachments, disabled = false) {
  return {
    onDragOver: (e: DragEvent) => {
      if (!disabled && e.dataTransfer?.types.includes("Files")) e.preventDefault();
    },
    onDrop: (e: DragEvent) => {
      if (disabled || !e.dataTransfer?.files.length) return;
      e.preventDefault();
      att.add([...e.dataTransfer.files]);
    },
    onPaste: (e: ClipboardEvent) => {
      if (disabled || !e.clipboardData?.files.length) return;
      e.preventDefault(); // a pasted file; pasted text still goes into the box
      att.add([...e.clipboardData.files]);
    },
  };
}

/** The attach button with its hidden file input. */
export function AttachButton({ att, disabled }: { att: Attachments; disabled?: boolean }) {
  const input = useRef<HTMLInputElement>(null);
  return (
    <>
      <input
        ref={input}
        type="file"
        multiple
        hidden
        data-testid="attach-input"
        onChange={(e) => {
          att.add([...(e.target.files ?? [])]);
          e.target.value = "";
        }}
      />
      <Button appearance="ghost" disabled={disabled} aria-label="Attach files" title={`Attach files (up to ${MAX_FILES}, ${MAX_FILE_BYTES / 1024 / 1024} MB each)`}
        onClick={() => input.current?.click()}>
        <Icon svg={AttachIcon} size="small" />
      </Button>
    </>
  );
}

/** One chip per attached file (name, size, upload state, remove) and any limit message. */
export function AttachmentChips({ att, disabled }: { att: Attachments; disabled?: boolean }) {
  if (att.items.length === 0 && !att.notice) return null;
  return (
    <div className="attachments">
      {att.items.length > 0 && (
        <ul className="attachment-list" aria-label="Attached files">
          {att.items.map((a) => (
            <li key={a.key} className={`attachment attachment-${a.status}`}>
              <span className="attachment-name" title={a.name}>{a.name}</span>
              <span className="attachment-meta">
                {a.status === "uploading" ? "Uploading…" : a.status === "error" ? "Upload failed" : formatSize(a.size)}
              </span>
              <button type="button" className="attachment-remove" disabled={disabled} aria-label={`Remove ${a.name}`} onClick={() => att.remove(a.key)}>
                <Icon svg={CloseIcon} size="small" />
              </button>
              {a.status === "error" && <span role="alert" className="attachment-error">{a.name}: {a.error}</span>}
            </li>
          ))}
        </ul>
      )}
      {att.notice && <span role="alert" className="attachment-error">{att.notice}</span>}
    </div>
  );
}
