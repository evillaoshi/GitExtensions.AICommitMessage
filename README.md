# GitExtensions.AICommitMessage

## What's new

The AI entry point is a single drop-down in the Commit dialog, and the settings were rebuilt around
it:

- **`🤖 AI commit` drop-down**, next to “Commit templates”, with two actions:
  - **`生成 stage`** — the model picks **one** related group out of your *unstaged* changes, you
    confirm the file list, the plugin stages exactly those files, refreshes the dialog and then
    writes the commit message. Nothing is ever committed for you.
  - **`生成 commit`** — writes the message from the **already staged** diff (the previous
    `✨ AI message` behaviour).
- **Model is a non-editable drop-down** fed by `GET {baseUrl}/models`: fill in the URL and API key and
  the list loads by itself (hard 6 s timeout, status shown right below the field). There is no default
  model, so generation waits until you choose one.
- **New setting `接口类型`** — `chat` (Chat Completions, `/chat/completions`, default) or `response`
  (Responses API, `/responses`).
- **`Max diff size (bytes)`** — drop-down `10000` / `50000` / `不限制` (default `10000`). The diff is
  cut on real UTF-8 byte boundaries, never inside an emoji, and the truncation note counts towards the
  budget.
- **New setting `AI 分组摘要上限（字节）`** — drop-down `8000` / `20000` / `不限制` (default `8000`),
  caps the change summary that `生成 stage` sends when it asks for a group.
- **Every default is visible and editable**, including at the *Global for all repositories* level,
  where unset values used to render as empty boxes.
- **The default System prompt is Chinese**: one `类型: 修改描述` subject line (≤ 40 characters, no
  trailing period, `feat`/`fix`/`refactor`/`docs`/`test` …).
- **Staging safety**: staging goes through Git Extensions' own staging code and only for the files you
  confirmed; the unstaged list is re-checked right before staging; a dialog can never stage into
  another repository; deleted files are staged as deletions and untracked directories are expanded
  into files.

## Build environment (quick reference)

What the container that builds this project does, in order. A .NET 10 SDK plus a couple of system
packages is all it takes — the plugin has no NuGet dependencies of its own.

### Linux (headless Debian 13 / Ubuntu container)

```sh
git clone https://github.com/evillaoshi/GitExtensions.AICommitMessage
cd GitExtensions.AICommitMessage

# 1) .NET 10 SDK. The distro package (apt install dotnet-sdk-10.0) did not exist in this container,
#    so the official installer is used instead:
curl -L https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"          # put this in ~/.bashrc to make it stick

# 2) The .NET runtime needs ICU on Debian/Ubuntu:
apt update && apt install -y libicu76

# 3) Build. net10.0-windows on Linux needs Windows targeting, and the host assemblies must be
#    pointed at explicitly (the default is C:\Program Files\GitExtensions):
dotnet build src/GitExtensions.AICommitMessage/GitExtensions.AICommitMessage.csproj \
  -c Release \
  -p:EnableWindowsTargeting=true \
  -p:GitExtensionsPath="$(pwd)/extPath"
```

### Windows

```sh
dotnet build src/GitExtensions.AICommitMessage/GitExtensions.AICommitMessage.csproj -c Release
```

`GitExtensionsPath` defaults to `C:\Program Files\GitExtensions`; add
`-p:GitExtensionsPath="D:\path\to\GitExtensions"` when yours lives elsewhere.

### Where the build output lands

```
src/GitExtensions.AICommitMessage/bin/Release/net10.0-windows/GitExtensions.AICommitMessage.dll
```

### Notes

- **`extPath/` is git-ignored** (see `.gitignore`), so a fresh clone does not contain it. It holds the
  Git Extensions assemblies this plugin compiles against: `GitExtensions.dll`,
  `GitExtensions.Extensibility.dll`, `GitCommands.dll`, `GitUIPluginInterfaces.dll`,
  `ResourceManager.dll`, `System.ComponentModel.Composition.dll`. Copy them from your Git Extensions
  installation into `extPath/`, or skip the folder and point `-p:GitExtensionsPath=` at that
  installation.
- `-p:EnableWindowsTargeting=true` is only needed when building on Linux/macOS; on Windows the plain
  command above is enough.
- A Linux build prints one harmless warning, `MSB3245` for `GitExtUtils`, because that assembly is not
  part of `extPath` — the plugin references the host assemblies with `Private=false`, so the build
  output is just the plugin DLL and Git Extensions supplies the rest at runtime.

**Stop staring at a blank commit message.** This is a plugin for
[Git Extensions](https://github.com/gitextensions/gitextensions) that writes a first-draft commit
message for you, straight from the changes you've staged.

It adds a **`🤖 AI commit`** drop-down to the Commit dialog, right next to “Commit templates”, with
two entries. **`生成 commit`** sends your **staged** diff to an AI model of your choice and drops the
suggested message into the commit box. **`生成 stage`** first picks one related group out of your
unstaged changes, stages it, and then writes the message — which you read, tweak, and commit. No
copy-pasting into a chat window, no leaving Git Extensions.

It talks to any **OpenAI-compatible** API — OpenAI, Azure OpenAI, OpenRouter, Groq — or a model
running **locally** via [Ollama](https://ollama.com/), in which case nothing ever leaves your
machine.

<img width="820" alt="The ✨ AI message button in the Git Extensions Commit dialog" src="https://github.com/user-attachments/assets/16d7a860-c6ec-46af-964a-2bec811dc26f" />

*The **`🤖 AI commit`** drop-down lives in the Commit dialog toolbar, right next to
“Commit templates”.*

**`生成 stage`** is for when the working tree is a mess: it asks the model to pick **one** related
group out of your unstaged changes, shows you the file list, stages exactly the files you confirm,
and then fills in the commit message. The commit itself is still yours to make.

## How it feels to use

1. Stage the changes you want to commit, as usual.
2. Open the **Commit** dialog and pick **`🤖 AI commit` → `生成 commit`**.
3. The plugin reads your staged diff and asks your model for a message. A second or two later the
   suggestion appears in the message box.
4. Edit anything you like, then commit.

Out of the box it asks for a clean [Conventional Commits](https://www.conventionalcommits.org)
subject line plus a short body explaining *why* the change was made — and you can rewrite that
instruction to match your team's style (see the **System prompt** setting below).

> **Background.** This grew out of
> [gitextensions/gitextensions#12203](https://github.com/gitextensions/gitextensions/issues/12203).
> The maintainers preferred not to bake AI into the core app and pointed to the plugin model — so
> this is a standalone plugin you opt into, nothing more.

## AI commit: stage one related change

Working tree full of unrelated edits? **`🤖 AI commit`** carves out one coherent commit for you:

1. Pick **`🤖 AI commit` → `生成 stage`** in the Commit dialog (needs the plugin enabled and a model
   selected).
2. The plugin sends the model a **summary of your changes** — staged and unstaged file names with
   `+/-` counts, plus a short excerpt (about the first 40 lines) of each unstaged file — and asks
   which of them belong to a single feature.
3. A confirmation dialog shows the feature name, the model's reason, and **every file it wants to
   stage**. Nothing has been staged at this point.
4. On **暂存并生成信息**, exactly those files are staged through Git Extensions' own staging code, the
   Commit dialog refreshes, and the commit message is generated from the freshly staged diff.

Cancelling stages nothing. The plugin never commits, never pushes, and never touches files you have
already staged. If the model can't produce a usable group, it **asks** before falling back to
staging everything. Files are staged whole — no hunk-level splitting yet.

## Privacy & safety (please read)

This plugin is built around the concerns raised on that original issue. You stay in control:

- **Off by default.** Nothing happens until you switch it on in settings *and* click the button.
- **Explicit consent, every single time.** Your diff is sent **only when you click the button** —
  never automatically, never when the dialog opens, never in the background.
- **Only *staged* content is sent** — with one exception. `生成 commit` sends `git diff --cached`.
  `生成 stage` must look at your unstaged changes first, so it sends their **file names, `+/-`
  counts and a short excerpt of each** (see [AI commit](#ai-commit-stage-one-related-change)); the
  excerpt budget is a setting. Either way, files ignored by `.gitignore` are never included.
- **Your key, your endpoint.** You bring your own API key — or point it at a local Ollama model so
  *nothing* leaves your machine. The key is stored in Git Extensions' plugin settings.
- **Size cap.** Large diffs are truncated to a configurable character limit, so a huge commit can't
  run up an unexpected token bill.

In short: you are sending your staged diff to whatever endpoint you configure. Don't enable it on
repositories whose contents you can't share with that provider.

## Requirements

- **Git Extensions 7.x** (built and tested against 7.0.1.86, which runs on .NET 10). The 5.2.x / .NET 8
  line is a separate, incompatible generation — see [Compatibility](#compatibility).
- **Git** available on your `PATH`.
- An API key for an OpenAI-compatible provider, **or** a local server such as Ollama.

## Compatibility

Git Extensions plugins are tied to the host's runtime **and** to the major version of its
`GitExtensions.Extensibility` contract — Git Extensions bumps that major version on every
*plugin-breaking* release, and the Plugin Manager only shows a plugin whose declared dependency range
covers the host's version. Because of that, a single build can't span generations:

| Git Extensions | Runtime | Extensibility | This plugin |
| --- | --- | --- | --- |
| **7.x** (current) | .NET 10 | `7.0.x` | **v0.5.3+** — depends on `[7.0.0, 8.0.0)` |
| 5.2.x | .NET 8 | `< 1.0` | v0.1.x (legacy, still on nuget.org) |

The `[7.0.0, 8.0.0)` range means this release works across the **entire current 7.x line** — every
patch and minor update, no re-pinning needed — but it intentionally will **not** appear in the Plugin
Manager on the older .NET 8 builds. If you're on a 5.2.x install, use the older `0.1.x` package or
update Git Extensions.

## Install

1. Build the plugin (see [Build from source](#build-from-source)) or download the release
   `.nupkg`/DLL.
2. Copy **`GitExtensions.AICommitMessage.dll`** into the Git Extensions `Plugins` folder:
   `C:\Program Files\GitExtensions\Plugins\` (writing here needs administrator rights).
3. Restart Git Extensions.

Prefer the Plugin Manager? See [Install from NuGet](#install-from-nuget-other-machines).

## Configure

Open **Settings → Plugins → AI Commit Message** and fill in the fields:

<img width="820" alt="AI commit message settings in Git Extensions" src="https://github.com/user-attachments/assets/f78f6df1-a96c-4c27-90d3-ffac81d702fc" />

| Setting | Notes |
| --- | --- |
| **Enabled** | Master switch. Off by default — turn this on first. |
| **API base URL** | `https://api.openai.com/v1` (OpenAI), `http://localhost:11434/v1` (Ollama), or any OpenAI-compatible base. |
| **接口类型** | Dropdown: `chat` (Chat Completions, `/chat/completions`, default) or `response` (Responses API, `/responses`). |
| **Model** | A non-editable dropdown populated automatically from the configured API `/models` endpoint. |
| **API key** | Masked. Models are loaded automatically after the URL and key are entered; local servers may leave it blank. |
| **Max diff size (bytes)** | Dropdown: `10000`, `50000`, or `不限制` (send everything). Default `10000` bytes. |
| **AI 分组摘要上限（字节）** | Only used by `生成 stage`: caps how much of the unstaged-change summary is sent when picking a group. Dropdown: `8000`, `20000`, or `不限制`. Default `8000` bytes. |
| **System prompt** | Steer the style. The default is Chinese: a single `类型: 修改描述` subject line (≤ 40 chars, no trailing period, `feat`/`fix`/`refactor`/`docs`/`test` …) and nothing else. |

## Build from source

```sh
dotnet build src/GitExtensions.AICommitMessage/GitExtensions.AICommitMessage.csproj -c Release
```

The project compiles against the Git Extensions assemblies in your install
(`C:\Program Files\GitExtensions` by default). If yours is elsewhere:

```sh
dotnet build ... -c Release -p:GitExtensionsPath="D:\path\to\GitExtensions"
```

Those host assemblies are referenced with `Private=false`, so the build output is just the plugin
DLL — Git Extensions provides the rest at runtime.

## Install from NuGet (other machines)

Once published, the package depends on `GitExtensions.Extensibility` — the marker the Git Extensions
**Plugin Manager** uses to discover plugins. On another machine you can either:

- open Git Extensions → **Plugins → Plugin Manager**, find **AI Commit Message**, and install it; or
- download the `.nupkg` from [nuget.org](https://www.nuget.org/packages/GitExtensions.AICommitMessage),
  rename it to `.zip`, and copy the `lib/GitExtensions.AICommitMessage.dll` into your
  `%LOCALAPPDATA%\GitExtensions\UserPlugins\` folder.

## Releasing to NuGet

Publishing is automated by [`.github/workflows/release.yml`](.github/workflows/release.yml) using
nuget.org **Trusted Publishing** (OIDC — no stored API key to manage). One-time setup:

1. On nuget.org → **Trusted Publishing**, add a policy:
   - **Repository Owner:** `badrshs`
   - **Repository:** `GitExtensions.AICommitMessage`
   - **Workflow File:** `release.yml`
   - **Environment:** *(leave blank)*
2. In this GitHub repo, add an Actions **variable** `NUGET_USER` set to your nuget.org username
   (Settings → Secrets and variables → Actions → **Variables** → New repository variable).
3. Tag a version and push it:

   ```sh
   git tag v0.5.3
   git push origin v0.5.3
   ```

The workflow fetches the matching Git Extensions binaries, packs the plugin, obtains a short-lived
key via OIDC, and pushes to nuget.org.

To build the package locally instead:

```sh
dotnet pack src/GitExtensions.AICommitMessage/GitExtensions.AICommitMessage.csproj -c Release
# then, with your own key:
dotnet nuget push src/GitExtensions.AICommitMessage/bin/Release/GitExtensions.AICommitMessage.0.5.3.nupkg \
  -k <YOUR_NUGET_API_KEY> -s https://api.nuget.org/v3/index.json
```

## How it works

A quick tour for the curious — three small files:

- **`Plugin.cs`** exports `IGitPlugin` / `IGitPluginForCommit` via MEF. When the Commit dialog opens
  (and only if the plugin is enabled) it waits for the form to appear, then injects the
  a **`🤖 AI commit`** drop-down (`生成 stage` / `生成 commit`) into the commit toolbar next to
  “Commit templates”, mirroring Git Extensions' own template menu. Everything is read and sent
  **only** inside those click handlers — that's the consent boundary.
- **`GitHelper.cs`** reads the staged diff with `git --no-pager diff --cached --no-color`, and builds
  the size-bounded change summary (`status --porcelain=v1 -z`, `diff --numstat`, short excerpts) that
  `🤖 AI commit` sends when asking for a group.
- **`OpenAiClient.cs`** POSTs the system prompt + diff to `{baseUrl}/chat/completions` (接口类型
  `chat`) or `{baseUrl}/responses` (接口类型 `response`), and returns the reply text, which is placed
  into the commit message box. `SelectFeatureAsync` uses the same endpoints to ask for the change
  group as JSON, which `FeatureSelection.Parse` reads tolerantly (code fences and extra prose are ok).

## License

MIT — see [LICENSE.md](LICENSE.md).
