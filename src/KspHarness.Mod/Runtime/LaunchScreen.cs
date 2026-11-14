using System;
using System.Globalization;
using UnityEngine;

namespace KspHarness.Mod.Runtime;

/// <summary>
/// Makes the window size given on the command line stick. Unity opens the window at <c>-screen-width</c>,
/// <c>-screen-height</c> and <c>-screen-fullscreen</c>, but KSP resizes it to the values in settings.cfg ten frames
/// after it starts, and settings.cfg is not under the harness's control: GPU tools such as the NVIDIA App rewrite it
/// with their "optimal" (native, full-screen) settings the first time they see a new install.
/// </summary>
internal static class LaunchScreen
{
    public const string WidthArgument = "-screen-width";
    public const string HeightArgument = "-screen-height";
    public const string FullscreenArgument = "-screen-fullscreen";

    /// <summary>Copies the command-line size into KSP's settings and resizes the window when it differs.</summary>
    public static void Apply(string[] arguments)
    {
        int? width = IntArgument(arguments, WidthArgument);
        int? height = IntArgument(arguments, HeightArgument);
        int? fullscreen = IntArgument(arguments, FullscreenArgument);
        if (width == null && height == null && fullscreen == null)
            return;

        // KSP's own resize (GameSettings.WaitAndSetResolution) and every later save read these fields, so the size
        // holds for the whole session and goes into settings.cfg with the game's next save.
        GameSettings.SCREEN_RESOLUTION_WIDTH = width ?? GameSettings.SCREEN_RESOLUTION_WIDTH;
        GameSettings.SCREEN_RESOLUTION_HEIGHT = height ?? GameSettings.SCREEN_RESOLUTION_HEIGHT;
        GameSettings.FULLSCREEN = fullscreen.HasValue ? fullscreen.Value != 0 : GameSettings.FULLSCREEN;

        int w = GameSettings.SCREEN_RESOLUTION_WIDTH;
        int h = GameSettings.SCREEN_RESOLUTION_HEIGHT;
        bool full = GameSettings.FULLSCREEN;
        if (Screen.width != w || Screen.height != h || Screen.fullScreen != full)
            Screen.SetResolution(w, h, full);

        Debug.Log($"[KspHarness] Screen set to {w}x{h} {(full ? "full screen" : "windowed")} from the command line");
    }

    private static int? IntArgument(string[] arguments, string name)
    {
        for (int i = 0; i < arguments.Length - 1; i++)
        {
            if (!string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (int.TryParse(arguments[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return value;
        }

        return null;
    }
}
