using System;
using System.IO;
using KspHarness.Mod.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KspHarness.Script;

/// <summary>Captures what the game shows, UI included.</summary>
public static class Screenshot
{
    /// <summary>
    /// Resumes at the end of the next frame, once it has rendered, with that frame as PNG bytes. A
    /// <paramref name="superSize"/> above 1 renders the scene's cameras again that many times larger, without KSP's
    /// UI, and throws <see cref="InvalidOperationException"/> when that comes out black while the screen does not.
    /// </summary>
    public static ScriptAwaitable<byte[]> Capture(int superSize = 1) => new CaptureAwaitable(CheckSuperSize(superSize));

    /// <summary>
    /// Like <see cref="Capture(int)"/>, but writes the PNG to <paramref name="path"/> (relative paths are under the
    /// game's folder) and resumes with the full path.
    /// </summary>
    public static ScriptAwaitable<string> Capture(string path, int superSize = 1)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("A path is required", nameof(path));

        return new SaveAwaitable(Path.GetFullPath(path), CheckSuperSize(superSize));
    }

    // Only valid at the end of the frame: earlier, the back buffer holds a frame still being drawn
    private static byte[] CapturePng(int superSize)
    {
        // A supersized capture is not a copy of the frame: Unity renders the scene's cameras again at the larger
        // size, without KSP's UI. Where the UI is all there is (loading screens, scene switches) that gives a black
        // image, which is refused rather than returned as if it were the screen. The frame itself is read first: after
        // the supersized render the back buffer no longer holds it.
        bool screenBlank = superSize > 1 && IsBlankCapture(1);

        Texture2D texture = ScreenCapture.CaptureScreenshotAsTexture(superSize);
        try
        {
            if (superSize > 1 && !screenBlank && IsBlank(texture))
            {
                throw new InvalidOperationException(
                    $"Screenshot.Capture with superSize {superSize} rendered a black image while the screen shows "
                        + "something: supersized captures render the scene's cameras again without the UI, so a screen "
                        + "made only of UI (a loading screen, a scene switch) comes out black. Use superSize 1 here."
                );
            }

            return texture.EncodeToPNG();
        }
        finally
        {
            Object.Destroy(texture);
        }
    }

    private static bool IsBlankCapture(int superSize)
    {
        Texture2D texture = ScreenCapture.CaptureScreenshotAsTexture(superSize);
        try
        {
            return IsBlank(texture);
        }
        finally
        {
            Object.Destroy(texture);
        }
    }

    private static bool IsBlank(Texture2D texture)
    {
        Color32[] pixels = texture.GetPixels32();

        // A sparse sample is enough to tell a picture from a blank frame
        for (int i = 0; i < pixels.Length; i += 61)
        {
            Color32 pixel = pixels[i];
            if (pixel.r > 8 || pixel.g > 8 || pixel.b > 8)
                return false;
        }

        return true;
    }

    private static int CheckSuperSize(int superSize)
    {
        if (superSize < 1)
            throw new ArgumentOutOfRangeException(nameof(superSize), superSize, "superSize must be at least 1");

        return superSize;
    }

    private sealed class CaptureAwaitable : ScriptAwaitable<byte[]>
    {
        private readonly int superSize;

        public CaptureAwaitable(int superSize)
            : base(TickPoint.EndOfFrame)
        {
            this.superSize = superSize;
        }

        internal override bool Poll()
        {
            Result = CapturePng(superSize);
            return true;
        }
    }

    private sealed class SaveAwaitable : ScriptAwaitable<string>
    {
        private readonly string path;
        private readonly int superSize;

        public SaveAwaitable(string path, int superSize)
            : base(TickPoint.EndOfFrame)
        {
            this.path = path;
            this.superSize = superSize;
        }

        internal override bool Poll()
        {
            byte[] png = CapturePng(superSize);
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            // Instances hard-link files to their starting install, so never write through an existing file
            if (File.Exists(path))
                File.Delete(path);

            File.WriteAllBytes(path, png);
            Result = path;
            return true;
        }
    }
}
