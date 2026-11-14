using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KspHarness;

/// <summary>A notification from the game, in the order the game sent it.</summary>
public abstract record GameEvent(string Kind, long Seq, double RealTime, long Frame, double? Ut, JsonObject Data);

public enum LogLevel
{
    Log,
    Warning,
    Error,
    Assert,
    Exception,
}

public sealed record LogEvent(
    long Seq,
    double RealTime,
    long Frame,
    double? Ut,
    JsonObject Data,
    LogLevel Level,
    string Message,
    string Stack,
    IReadOnlyList<string> Mods
) : GameEvent("log", Seq, RealTime, Frame, Ut, Data)
{
    /// <summary>Error, Exception and Assert entries.</summary>
    public bool IsError => Level is LogLevel.Error or LogLevel.Exception or LogLevel.Assert;

    public override string ToString()
    {
        var mods = Mods.Count > 0 ? $" [{string.Join(", ", Mods)}]" : "";
        return $"[{Level}] {Message}{mods}";
    }
}

public sealed record SceneEvent(
    long Seq,
    double RealTime,
    long Frame,
    double? Ut,
    JsonObject Data,
    string From,
    string To,
    string Phase
) : GameEvent("scene", Seq, RealTime, Frame, Ut, Data);

public sealed record DialogEvent(
    long Seq,
    double RealTime,
    long Frame,
    double? Ut,
    JsonObject Data,
    string Name,
    string Title,
    string Message,
    IReadOnlyList<string> Buttons
) : GameEvent("dialog", Seq, RealTime, Frame, Ut, Data)
{
    /// <summary>The dialog's instance id in the game; the events of one dialog share it.</summary>
    public long Id => Data["id"]?.GetValue<long>() ?? 0;

    /// <summary>
    /// <c>opened</c> when the game showed it, <c>clicked</c> or <c>dismissed</c> when a snippet answered it (a popup
    /// policy, <c>Dialogs.Click</c> or <c>Dialogs.Dismiss</c>).
    /// </summary>
    public string Action => Data["action"]?.GetValue<string>() ?? "opened";

    /// <summary>The caption of the button a snippet clicked; null otherwise.</summary>
    public string? Button => Data["button"]?.GetValue<string>();

    public bool IsAnswer => Action != "opened";

    public override string ToString()
    {
        var answer = Action switch
        {
            "clicked" => $" -> clicked \"{Button}\"",
            "dismissed" => " -> dismissed",
            _ => "",
        };
        return $"{Name}: {Plain(Title)} | {Plain(Message)} [{string.Join(", ", Buttons)}]{answer}";
    }

    // Messages carry rich-text tags and line breaks; a summary line shows the text as the dialog reads.
    private static string Plain(string text)
    {
        var untagged = Regex.Replace(text, "<[^<>]*>", "");
        return Regex.Replace(untagged, @"\s+", " ").Trim();
    }
}

public sealed record CrashingEvent(long Seq, double RealTime, long Frame, double? Ut, JsonObject Data, string Message)
    : GameEvent("crashing", Seq, RealTime, Frame, Ut, Data);

/// <summary>An event kind this driver does not know yet.</summary>
public sealed record OtherEvent(string Kind, long Seq, double RealTime, long Frame, double? Ut, JsonObject Data)
    : GameEvent(Kind, Seq, RealTime, Frame, Ut, Data);

internal static class GameEventParser
{
    public static GameEvent Parse(JsonObject data)
    {
        var kind = data["kind"]?.GetValue<string>() ?? "";
        var seq = data["seq"]?.Deserialize<long>() ?? -1;
        var realTime = data["realTime"]?.Deserialize<double>() ?? 0;
        var frame = data["frame"]?.Deserialize<long>() ?? 0;
        var ut = data["ut"]?.Deserialize<double?>();

        switch (kind)
        {
            case "log":
                return new LogEvent(
                    seq,
                    realTime,
                    frame,
                    ut,
                    data,
                    Enum.TryParse<LogLevel>(Text(data, "level"), out var level) ? level : LogLevel.Log,
                    Text(data, "message"),
                    Text(data, "stack"),
                    Strings(data["mods"])
                );
            case "scene":
                return new SceneEvent(
                    seq,
                    realTime,
                    frame,
                    ut,
                    data,
                    Text(data, "from"),
                    Text(data, "to"),
                    Text(data, "phase")
                );
            case "dialog":
                return new DialogEvent(
                    seq,
                    realTime,
                    frame,
                    ut,
                    data,
                    Text(data, "name"),
                    Text(data, "title"),
                    Text(data, "message"),
                    Strings(data["buttons"])
                );
            case "crashing":
                return new CrashingEvent(seq, realTime, frame, ut, data, Text(data, "message"));
            default:
                return new OtherEvent(kind, seq, realTime, frame, ut, data);
        }
    }

    private static string Text(JsonObject data, string name) => data[name]?.GetValue<string>() ?? "";

    private static IReadOnlyList<string> Strings(JsonNode? node)
    {
        return node is JsonArray array ? array.Select(item => item?.GetValue<string>() ?? "").ToArray() : [];
    }
}
