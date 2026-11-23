# Releases

Each plugin version is published as a GitHub Release, tagged and titled `v<version>` after the `version` in
`.claude-plugin/plugin.json` (version 0.2.57 is release `v0.2.57`). A release has two assets:

| Asset | What it is |
| --- | --- |
| `fyrd.<version>.nupkg` | The Fyrd console as a dotnet tool: the Fyrd.Server backend with the built React console, the skills and the session-skills guide. Its command is `fyrd`. |
| `fyrd-plugin-<version>.zip` | The Claude Code plugin's files, at the zip's root (see [What the plugin zip holds](#what-the-plugin-zip-holds)). |

## When a release is made

`.github/workflows/release.yml` runs on every push to `main`. It reads the plugin version and asks GitHub whether a
release with that tag exists. If one does, it stops there. If not, it runs every test suite
(`scripts/release/invoke-all-tests.ps1`: the .NET tests, the node script tests, the console's `tsc -b` and vitest
tests, and the Pester tests), builds both assets and creates the release on the pushed commit, with notes GitHub
generates from the merged pull requests. A failing test stops the run before anything is built or published. So a release is made when a pull request
that bumps the version merges (the version is bumped once per pull request into `main`, see `CLAUDE.md`), and a push
that does not bump it releases nothing. If the check cannot tell (any answer but found or 404), the run fails instead
of guessing.

To re-run a release whose run failed, start the workflow by hand on `main` (Actions › Release › Run workflow). It does
nothing when the release exists. If a failed run left a draft release behind, delete the draft first.

The workflow has no secrets: it uses the run's own `GITHUB_TOKEN`. It has three jobs:

| Job | Token | Runs |
| --- | --- | --- |
| `check` | `contents: read` | `plugin-version.ps1` and `release-exists.ps1` |
| `build` | `contents: read` | the tests, `build-tool-package.ps1` and `build-plugin-zip.ps1`, then uploads both assets as the `release-assets` artifact |
| `publish` | `contents: write` | downloads `release-assets` and runs `publish-release.ps1` |

Everything that runs code from npm, NuGet or PSGallery (`npm ci`, the restore in `dotnet pack` and `dotnet test`,
installing Pester) is in `build`, which cannot write to the repository. `publish` runs only `gh` and the release
scripts, and refuses an artifact that holds anything but the two assets named after the version. Actions are pinned by
commit SHA, and the tests pin Pester 6.2.0.

`publish-release.ps1` reads the tag before it creates the release. If `v<version>` already exists on another commit
(an annotated tag is followed to its commit), it stops without publishing, because `gh release create` would otherwise
attach the release to that commit and ignore the pushed one. Move or delete the tag, or bump the version, and re-run.
A tag already on the pushed commit is used as it is.

## Install the console tool from a release

The tool needs the .NET 10 runtime. Download `fyrd.<version>.nupkg` from the release into a folder of its own, then:

```
dotnet tool install --global fyrd --add-source <that folder> --version <version>
```

To move to a newer release, download its package into the folder and run
`dotnet tool update --global fyrd --add-source <that folder> --version <version>`.

`fyrd` starts the console on `http://127.0.0.1:4870` (pass `--urls http://127.0.0.1:<port>` for another port). It
serves the console, skills and guide that came in its package; the environment variables in the README's
Configuration section apply as they do to `/fyrd:up`.

## Install the plugin from a release

Unzip `fyrd-plugin-<version>.zip` into a folder, then in Claude Code:

```
/plugin marketplace add <that folder>
/plugin install fyrd@fyrd
```

The zip has no runtime source, so `/fyrd:up` runs the `fyrd` tool from your `PATH` instead of building the server (on
Windows, macOS and Linux, `dotnet tool install --global` puts it in `~/.dotnet/tools`, which must be on `PATH`), and
points it at the plugin's own skills and docs. Install the tool from the same release as the zip. Without it, `/fyrd:up`
says how to install it. `/fyrd:up` still needs Node. A clone of the repository keeps building and running the server
from source, as before.

## What the plugin zip holds

Only files git tracks, and of those only what the plugin uses at runtime:

- `.claude-plugin/` (`plugin.json` and `marketplace.json`, so the unzipped folder can be added as a marketplace)
- `commands/` (`/fyrd:up`, `/fyrd:down`, `/fyrd:status`)
- `skills/` (every skill, with `checkouts/checkouts.mjs`, `tuner/ledger.html` and `persona-seed-history.json`, which
  the console reads to seed personas)
- `scripts/fyrd.mjs`, which the commands run, and `scripts/lib.mjs`, which it imports
- `docs/*.md`, the user guides (`session-skills.md` is also shown in the console)
- `README.md` and `LICENSE`

It leaves out the runtime and console source (the tool package carries the built console), `spikes/`, tests, the
release scripts, the developer scripts (`probe-cloud-create.ps1`, `test-conpty.ps1`, `seed-history.mjs`) and
`docs/reference/` and `docs/superpowers/`, which are API captures, plans and design notes for developing Fyrd.
`scripts/release/build-plugin-zip.ps1` holds the rule (`Test-PluginFile`), and its tests check that every file
`fyrd.mjs` and `lib.mjs` import and every skill file is in the zip, and that no source, spike or test is.

## The release scripts

The workflow runs PowerShell scripts in `scripts/release/`, so each step can be run and tested on its own (PowerShell 7:
`dotnet tool install --global powershell` if `pwsh` is missing):

| Script | Does |
| --- | --- |
| `plugin-version.ps1` | Reads and checks the version; writes `version` and `tag` |
| `release-exists.ps1` | Asks GitHub (read-only) whether the tag's release exists; writes `exists` |
| `build-tool-package.ps1` | Builds the console, packs `runtime/src/Fyrd.Server` and checks the package holds the `fyrd` command at the plugin version with the console, skills and guide; writes `path` |
| `build-plugin-zip.ps1` | Zips the plugin files; writes `path` |
| `invoke-all-tests.ps1` | Runs every test suite, stopping at the first failure |
| `invoke-tests.ps1` | Runs the Pester tests under `scripts/` with Pester 6.2.0 |
| `publish-release.ps1` | Checks the tag is missing or on the pushed commit, then runs `gh release create` with both assets |

To build both assets locally into `out/` (ignored by git) without publishing anything:

```
pwsh ./scripts/release/build-tool-package.ps1 -OutDir out
pwsh ./scripts/release/build-plugin-zip.ps1 -OutDir out
```

The Pester tests are `pwsh ./scripts/release/invoke-tests.ps1` (it installs Pester 6.2.0 for the current user if that
version is missing). `pwsh ./scripts/release/invoke-all-tests.ps1` runs every suite, as the workflow does before it
builds. The ConPTY runner tests in `Fyrd.Core.Tests` return at once off Windows, so on the Linux runner they pass
without exercising the pseudo console; run them on Windows with `scripts/test-conpty.ps1`.

`runtime/src/Fyrd.Server/Fyrd.Server.csproj` reads the package version from `plugin.json`, so `dotnet pack` there always
packs the plugin version. The package carries `wwwroot` only once the console is built (`npm run build` in `console/`),
which is why the pack script builds it first. Run from the package, the server finds the console, skills and docs beside
its own assembly (`ServerLayout`); run from a clone, it finds them in the repository as before.
