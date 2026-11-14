#nullable disable

namespace KspHarness.Script;

/// <summary>Captures what the game shows, UI included, at the end of the next frame.</summary>
public static class Screenshot
{
    public static ScriptAwaitable<byte[]> Capture(int superSize = 1) => throw OutsideGame.Error();

    public static ScriptAwaitable<string> Capture(string path, int superSize = 1) => throw OutsideGame.Error();
}
