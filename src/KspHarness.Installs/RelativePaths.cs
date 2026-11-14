namespace KspHarness.Installs;

internal static class RelativePaths
{
    /// <summary>Rejects paths that are absolute or climb out of the folder they are relative to.</summary>
    public static void Check(string relative, string parameter)
    {
        string normalized = Normalize(relative);
        bool escapes = normalized.Split('/').Any(segment => segment == "..");
        if (normalized.Length == 0 || Path.IsPathRooted(relative) || escapes)
        {
            throw new ArgumentException($"expected a relative path inside the instance: '{relative}'", parameter);
        }
    }

    public static string Normalize(string relative) => relative.Replace('\\', '/').Trim('/');

    /// <summary>Combines and checks that the result stays inside <paramref name="root"/>.</summary>
    public static string Resolve(string root, string relative)
    {
        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (
            !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidDataException($"path '{relative}' leaves {root}");
        }

        return full;
    }
}
