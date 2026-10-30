# fyrd

Build software with an army of specialized agents, managed from a one-click console.

## Usage

Requires Node, the .NET 10 SDK, and npm.

- `/fyrd:up` builds (first run only) and starts the console on `127.0.0.1` (port 4870, or the next free one), then opens it in your browser. Running it again reuses the running server.
- `/fyrd:status` shows the URL, spec count, and decisions waiting.
- `/fyrd:down` stops the server.

Runtime state (PID/port, log) lives in `~/.fyrd/run/`; data lives in `~/.fyrd/data/`.

Environment variables:

- `FYRD_HOME` moves the data directory (default `~/.fyrd/data`). It moves data only; `~/.fyrd/run` (PID/port, log) is always shared.
- `FYRD_NO_BROWSER=1` stops `/fyrd:up` from opening a browser.

## Tests

```
dotnet test runtime/Fyrd.sln
cd console && npm test
node --test "scripts/*.test.mjs"
```

## Local install

```
/plugin marketplace add C:/Users/nhuizenga/repos/personal/fyrd
/plugin install fyrd@fyrd
```

After edits, run `/reload-plugins`.
