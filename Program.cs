using Anthropic;
using Anthropic.Models.Messages;
using CodeAssistantCli;

// ---------------------------------------------------------------------------
// codeassist — a minimal terminal coding assistant powered by the Claude API.
// Demonstrates an agentic tool-use loop: the model reads/searches/writes files
// in a workspace until it can answer, then replies.
// ---------------------------------------------------------------------------

const string Model = "claude-opus-4-8";
const int MaxTokens = 16_000;
const int MaxToolTurns = 25;

const string SystemPrompt =
    "You are codeassist, a terminal coding assistant. You help the user understand and edit "
    + "the code in their workspace. Use the file tools to gather context before answering — "
    + "read and search rather than guessing. Keep replies concise and reference files by path. "
    + "Before writing a file, briefly say what you are about to change.";

string apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty;
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set the ANTHROPIC_API_KEY environment variable first.");
    return 1;
}

// Workspace root: first CLI arg, or the current directory.
string workspace = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
if (!Directory.Exists(workspace))
{
    Console.Error.WriteLine($"Workspace directory not found: {workspace}");
    return 1;
}

var client = new AnthropicClient { ApiKey = apiKey };
var tools = new WorkspaceTools(workspace);
var toolUnion = WorkspaceTools.Definitions.Select(t => new ToolUnion(t)).ToArray();
var history = new List<MessageParam>();

Banner(workspace);

while (true)
{
    Prompt("you> ");
    string? line = Console.ReadLine();
    if (line is null || line.Trim() is "exit" or "quit")
    {
        break;
    }

    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    history.Add(new MessageParam { Role = Role.User, Content = line });

    try
    {
        await RunAgentTurn();
    }
    catch (Exception ex)
    {
        WriteLine(ConsoleColor.Red, $"[error] {ex.Message}");
    }
}

return 0;

// ---------------------------------------------------------------------------
// The agentic loop: call the model, run any tools it requests, feed results
// back, and repeat until it stops asking for tools.
// ---------------------------------------------------------------------------
async Task RunAgentTurn()
{
    for (int turn = 0; turn < MaxToolTurns; turn++)
    {
        Message response = await client.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = MaxTokens,
            System = SystemPrompt,
            Tools = toolUnion,
            Messages = history,
        });

        List<ContentBlockParam> assistantContent = [];
        List<ContentBlockParam> toolResults = [];

        foreach (ContentBlock block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
            {
                WriteLine(ConsoleColor.Cyan, $"\nclaude> {text!.Text}\n");
                assistantContent.Add(new TextBlockParam { Text = text.Text });
            }
            else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
            {
                WriteLine(ConsoleColor.DarkGray, $"  · {toolUse!.Name}({Describe(toolUse)})");

                assistantContent.Add(new ToolUseBlockParam
                {
                    ID = toolUse.ID,
                    Name = toolUse.Name,
                    Input = toolUse.Input,
                });

                string result = tools.Execute(toolUse.Name, toolUse.Input);
                toolResults.Add(new ToolResultBlockParam
                {
                    ToolUseID = toolUse.ID,
                    Content = result,
                });
            }
        }

        history.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });

        // Done when the model stops requesting tools.
        if (response.StopReason != "tool_use")
        {
            return;
        }

        history.Add(new MessageParam { Role = Role.User, Content = toolResults });
    }

    WriteLine(ConsoleColor.Yellow, $"[stopped after {MaxToolTurns} tool turns]");
}

// ---------------------------------------------------------------------------
// Console helpers
// ---------------------------------------------------------------------------
static string Describe(ToolUseBlock toolUse)
{
    if (toolUse.Input.TryGetValue("path", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String)
    {
        return p.GetString() ?? string.Empty;
    }

    if (toolUse.Input.TryGetValue("pattern", out var q) && q.ValueKind == System.Text.Json.JsonValueKind.String)
    {
        return $"\"{q.GetString()}\"";
    }

    return string.Empty;
}

static void Banner(string workspace)
{
    WriteLine(ConsoleColor.Green, "codeassist — Claude-powered coding assistant");
    WriteLine(ConsoleColor.DarkGray, $"workspace: {workspace}");
    WriteLine(ConsoleColor.DarkGray, "type your question, or 'exit' to quit.\n");
}

static void Prompt(string label)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write(label);
    Console.ResetColor();
}

static void WriteLine(ConsoleColor color, string message)
{
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ResetColor();
}
