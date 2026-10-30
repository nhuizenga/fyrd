import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";

export function runDir(home = os.homedir()) {
  return path.join(home, ".fyrd", "run");
}

const serverFile = (dir) => path.join(dir, "server.json");

export function readServerFile(dir) {
  try {
    const info = JSON.parse(fs.readFileSync(serverFile(dir), "utf8"));
    if (!info || !Number.isInteger(info.pid) || !Number.isInteger(info.port)) return null;
    return info;
  } catch {
    return null;
  }
}

export function writeServerFile(dir, info) {
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(serverFile(dir), JSON.stringify(info));
}

export function removeServerFile(dir) {
  fs.rmSync(serverFile(dir), { force: true });
}

export async function isServerAlive(info, fetchImpl = fetch) {
  try {
    process.kill(info.pid, 0);
  } catch {
    return false;
  }
  try {
    const res = await fetchImpl(`http://127.0.0.1:${info.port}/api/health`, {
      signal: AbortSignal.timeout(1000),
    });
    const body = await res.json();
    return body?.status === "ok";
  } catch {
    return false;
  }
}

function canListen(port) {
  return new Promise((resolve) => {
    const s = net.createServer();
    s.once("error", () => resolve(false));
    s.listen(port, "127.0.0.1", () => s.close(() => resolve(true)));
  });
}

export async function findFreePort(start = 4870, tries = 10) {
  for (let p = start; p < start + tries; p++) {
    if (await canListen(p)) return p;
  }
  throw new Error(`No free port in ${start}-${start + tries - 1}`);
}

function newestMtime(dir) {
  let newest = 0;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    const t = e.isDirectory() ? newestMtime(p) : fs.statSync(p).mtimeMs;
    if (t > newest) newest = t;
  }
  return newest;
}

export function needsConsoleBuild(repoRoot) {
  const index = path.join(repoRoot, "runtime/src/Fyrd.Server/wwwroot/index.html");
  if (!fs.existsSync(index)) return true;
  const src = path.join(repoRoot, "console/src");
  if (!fs.existsSync(src)) return false;
  return newestMtime(src) > fs.statSync(index).mtimeMs;
}
