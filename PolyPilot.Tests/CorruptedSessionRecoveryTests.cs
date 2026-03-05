using System.Text.RegularExpressions;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for TrySanitizeEventsFile and IsCorruptedSessionError —
/// graceful recovery when events.jsonl contains invalid JSON from
/// sessions created by external tools (e.g., Python-style booleans).
/// </summary>
public class CorruptedSessionRecoveryTests : IDisposable
{
    private readonly string _tempDir;

    public CorruptedSessionRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "polypilot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private string CreateSessionDir(string sessionId)
    {
        var dir = Path.Combine(_tempDir, sessionId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string WriteEventsFile(string sessionId, params string[] lines)
    {
        var dir = CreateSessionDir(sessionId);
        var path = Path.Combine(dir, "events.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private string ReadEventsFile(string sessionId)
    {
        return File.ReadAllText(Path.Combine(_tempDir, sessionId, "events.jsonl"));
    }

    // --- IsCorruptedSessionError ---

    [Fact]
    public void IsCorruptedSessionError_MatchesCorruptedMessage()
    {
        var ex = new Exception("Session file is corrupted (line 688: ephemeral: Invalid literal value, expected true)");
        Assert.True(CopilotService.IsCorruptedSessionError(ex));
    }

    [Fact]
    public void IsCorruptedSessionError_MatchesInvalidLiteralMessage()
    {
        var ex = new Exception("Invalid literal value, expected false");
        Assert.True(CopilotService.IsCorruptedSessionError(ex));
    }

    [Fact]
    public void IsCorruptedSessionError_DoesNotMatchUnrelatedError()
    {
        var ex = new Exception("Session not found");
        Assert.False(CopilotService.IsCorruptedSessionError(ex));
    }

    [Fact]
    public void IsCorruptedSessionError_DoesNotMatchGenericIOError()
    {
        var ex = new IOException("File not found");
        Assert.False(CopilotService.IsCorruptedSessionError(ex));
    }

    // --- TrySanitizeEventsFile: capitalized booleans ---

    [Fact]
    public void Sanitize_FixesCapitalizedTrue()
    {
        var sid = Guid.NewGuid().ToString();
        WriteEventsFile(sid,
            """{"type":"assistant.message","data":{"content":"hello","ephemeral": True}}""");

        var result = CopilotService.TrySanitizeEventsFile(sid, _tempDir);

        Assert.True(result);
        var content = ReadEventsFile(sid);
        Assert.Contains("\"ephemeral\": true", content);
        Assert.DoesNotContain("True", content);
    }

    [Fact]
    public void Sanitize_FixesCapitalizedFalse()
    {
        var sid = Guid.NewGuid().ToString();
        WriteEventsFile(sid,
            """{"type":"test","data":{"visible": False}}""");

        var result = CopilotService.TrySanitizeEventsFile(sid, _tempDir);

        Assert.True(result);
        var content = ReadEventsFile(sid);
        Assert.Contains("\"visible\": false", content);
        Assert.DoesNotContain("False", content);
    }

    [Fact]
    public void Sanitize_FixesPythonNone()
    {
        var sid = Guid.NewGuid().ToString();
        WriteEventsFile(sid,
            """{"type":"test","data":{"value": None}}""");

        var result = CopilotService.TrySanitizeEventsFile(sid, _tempDir);

        Assert.True(result);
        var content = ReadEventsFile(sid);
        Assert.Contains("\"value\": null", content);
    }

    [Fact]
    public void Sanitize_FixesMultipleIssuesOnSameLine()
    {
        var sid = Guid.NewGuid().ToString();
        WriteEventsFile(sid,
            """{"type":"test","data":{"a": True, "b": False, "c": None}}""");

        var result = CopilotService.TrySanitizeEventsFile(sid, _tempDir);

        Assert.True(result);
        var content = ReadEventsFile(sid);
        Assert.Contains("\"a\": true", content);
        Assert.Contains("\"b\": false", content);
        Assert.Contains("\"c\": null", content);
    }

    [Fact]
    public void Sanitize_DoesNotModifyValidJson()
    {
        var sid = Guid.NewGuid().ToString();
        var original = """{"type":"test","data":{"ephemeral": true, "visible": false, "value": null}}""";
        WriteEventsFile(sid, original);

        var result = CopilotService.TrySanitizeEventsFile(sid, _tempDir);

        Assert.False(result); // No changes needed
        Assert.Equal(original, ReadEventsFile(sid).TrimEnd());
    }

    [Fact]
    public void Sanitize_DoesNotCorruptTrueInStringValues()
    {
        var sid = Guid.NewGuid().ToString();
        // "True" inside a string value should NOT be changed
        var original = """{"type":"test","data":{"content":"The value True is used"}}""";
        WriteEventsFile(sid, original);

        var result = CopilotService.TrySanitizeEventsFile(sid, _tempDir);

        Assert.False(result);
        Assert.Equal(original, ReadEventsFile(sid).TrimEnd());
    }

    [Fact]
    public void Sanitize_HandlesMultipleLinesWithMixedCorruption()
    {
        var sid = Guid.NewGuid().ToString();
        WriteEventsFile(sid,
            """{"type":"session.start","data":{"context":{"cwd":"/tmp"}}}""",
            """{"type":"user.message","data":{"content":"hello"}}""",
            """{"type":"assistant.message","data":{"content":"world","ephemeral": True}}""");

        var result = CopilotService.TrySanitizeEventsFile(sid, _tempDir);

        Assert.True(result);
        var lines = File.ReadAllLines(Path.Combine(_tempDir, sid, "events.jsonl"));
        Assert.Equal(3, lines.Length);
        // First two lines should be unchanged
        Assert.Contains("session.start", lines[0]);
        Assert.Contains("user.message", lines[1]);
        // Third line should be fixed
        Assert.Contains("\"ephemeral\": true", lines[2]);
    }

    [Fact]
    public void Sanitize_ReturnsFlaseForMissingFile()
    {
        var result = CopilotService.TrySanitizeEventsFile("nonexistent-session", _tempDir);
        Assert.False(result);
    }

    [Fact]
    public void Sanitize_ReturnsFlaseForEmptySessionId()
    {
        var result = CopilotService.TrySanitizeEventsFile("", _tempDir);
        Assert.False(result);
    }

    [Fact]
    public void Sanitize_PreservesEmptyLines()
    {
        var sid = Guid.NewGuid().ToString();
        WriteEventsFile(sid,
            """{"type":"test","data":{"a": True}}""",
            "",
            """{"type":"test","data":{"b": true}}""");

        var result = CopilotService.TrySanitizeEventsFile(sid, _tempDir);

        Assert.True(result);
        var lines = File.ReadAllLines(Path.Combine(_tempDir, sid, "events.jsonl"));
        Assert.Equal(3, lines.Length);
        Assert.Equal("", lines[1]); // Empty line preserved
    }

    [Fact]
    public void Sanitize_FixesBooleanInArray()
    {
        var sid = Guid.NewGuid().ToString();
        WriteEventsFile(sid,
            """{"type":"test","data":{"items":[True, False, None]}}""");

        var result = CopilotService.TrySanitizeEventsFile(sid, _tempDir);

        Assert.True(result);
        var content = ReadEventsFile(sid);
        Assert.Contains("[true, false, null]", content);
    }

    // --- Realistic corruption scenario ---

    [Fact]
    public void Sanitize_RealisticEphemeralCorruption()
    {
        // The exact pattern from the bug report: "ephemeral": True on a deeply-nested event line
        var sid = Guid.NewGuid().ToString();
        WriteEventsFile(sid,
            """{"type":"session.start","data":{"context":{"cwd":"/Users/test/project"},"selectedModel":"claude-opus-4.6"},"id":"abc","timestamp":"2026-03-05T00:00:00Z"}""",
            """{"type":"user.message","data":{"content":"so we have MyGameStateTest"},"id":"def","timestamp":"2026-03-05T00:01:00Z"}""",
            """{"type":"assistant.message","data":{"content":"I'll help","ephemeral": True},"id":"ghi","timestamp":"2026-03-05T00:02:00Z"}""");

        var result = CopilotService.TrySanitizeEventsFile(sid, _tempDir);

        Assert.True(result);
        var content = ReadEventsFile(sid);
        // The ephemeral field should now be valid JSON
        Assert.Contains("\"ephemeral\": true", content);
        // Verify the entire file is valid JSONL (each line parses)
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var doc = System.Text.Json.JsonDocument.Parse(line);
            doc.Dispose();
        }
    }
}
