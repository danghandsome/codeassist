# codeassist — a Claude-powered coding assistant CLI

A small terminal coding assistant written in **C# / .NET 8**, built on the official
[Anthropic SDK](https://www.nuget.org/packages/Anthropic). It runs an **agentic
tool-use loop**: the model reads, searches, and writes files in a workspace until
it has enough context to answer — the same pattern behind modern AI coding tools,
in ~250 lines of readable C#.

> Built as a portfolio project to demonstrate integrating LLMs into a real .NET
> stack (I work day-to-day on a large WinForms hospital system in C#).

## What it shows

- **Tool calling / function calling** — four client-side tools (`list_files`,
  `read_file`, `write_file`, `search`) defined with JSON Schema and dispatched by name.
- **The agentic loop** — call the model → execute requested tools → feed results
  back → repeat until `stop_reason != "tool_use"`.
- **Safety** — every file operation is confined to the workspace root; paths that
  escape it are rejected (`..`, absolute paths, symlinks).
- **Clean architecture** — API/loop in `Program.cs`, tools isolated in `Tools.cs`.

## Architecture

```
You type a question
  → Messages.Create(model, tools, history)     [Claude decides what to do]
    → model returns text and/or tool_use blocks
      → WorkspaceTools.Execute(name, input)     [run the tool locally, in-sandbox]
        → tool_result fed back into history
          → loop until the model answers with no more tool calls
```

## Run it

> **Getting a key:** create one at [console.anthropic.com](https://console.anthropic.com)
> → *API Keys*. It's pay-as-you-go; a quick test costs a few cents.

```bash
# 1. Set your API key
export ANTHROPIC_API_KEY=sk-ant-...          # PowerShell: $env:ANTHROPIC_API_KEY="sk-ant-..."

# 2. Restore the SDK (once)
dotnet add package Anthropic

# 3. Run against a workspace (defaults to the current directory)
dotnet run -- /path/to/your/project
```

Then chat:

```
you> where is the database connection configured?
  · search("ConnectionString")
  · read_file("appsettings.json")
claude> The connection is configured in appsettings.json:12 under "ConnectionStrings:Default"...

you> add a health-check endpoint to Program.cs
  · read_file("Program.cs")
claude> I'll add a /health endpoint that returns 200 OK. Writing Program.cs...
  · write_file("Program.cs")
claude> Done — added app.MapGet("/health", () => Results.Ok()).
```

Type `exit` to quit.

## Model

Uses `claude-opus-4-8` (Anthropic's most capable Opus-tier model). Change the
`Model` constant in [Program.cs](Program.cs) to use another (e.g. `claude-sonnet-4-6`
for lower cost).

## Files

| File | Role |
|------|------|
| `Program.cs` | Entry point + the agentic tool-use loop |
| `Tools.cs` | The four workspace tools + JSON-schema definitions + path confinement |
| `CodeAssistantCli.csproj` | .NET 8 console project, references the `Anthropic` package |

## Possible extensions

- Stream the final answer token-by-token (`Messages.CreateStreaming`)
- Add a `run_command` tool (with an allowlist) for build/test feedback
- Swap the manual loop for the SDK's `BetaToolRunner`
- Persist conversation history between sessions
