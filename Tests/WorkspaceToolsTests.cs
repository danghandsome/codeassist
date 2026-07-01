using System.Text.Json;
using CodeAssistantCli;
using Xunit;

namespace CodeAssistantCli.Tests;

/// <summary>
/// Tests the safety invariants of the workspace tools: files stay inside the
/// root, and run_command / git only accept allowlisted programs.
/// </summary>
public class WorkspaceToolsTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceTools _tools;

    public WorkspaceToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ca_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _tools = new WorkspaceTools(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private static IReadOnlyDictionary<string, JsonElement> Input(params (string key, string value)[] pairs)
        => pairs.ToDictionary(p => p.key, p => JsonSerializer.SerializeToElement(p.value));

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        _tools.Execute("write_file", Input(("path", "notes/a.txt"), ("content", "hello world")));
        string result = _tools.Execute("read_file", Input(("path", "notes/a.txt")));
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void ListFiles_ShowsCreatedFile()
    {
        _tools.Execute("write_file", Input(("path", "b.txt"), ("content", "x")));
        string result = _tools.Execute("list_files", Input(("path", ".")));
        Assert.Contains("b.txt", result);
    }

    [Fact]
    public void ReadFile_OutsideRoot_IsRejected()
    {
        string result = _tools.Execute("read_file", Input(("path", "../../secret.txt")));
        Assert.StartsWith("Error", result);
    }

    [Fact]
    public void RunCommand_DisallowedProgram_IsRejected()
    {
        string result = _tools.Execute("run_command", Input(("command", "rm -rf /")));
        Assert.Contains("not allowed", result);
    }

    [Fact]
    public void Git_DisallowedSubcommand_IsRejected()
    {
        string result = _tools.Execute("git", Input(("args", "push origin main")));
        Assert.Contains("not allowed", result);
    }

    [Fact]
    public void UnknownTool_ReturnsError()
    {
        string result = _tools.Execute("do_something_evil", Input());
        Assert.StartsWith("Error", result);
    }
}
