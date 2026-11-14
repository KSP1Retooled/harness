namespace KspHarness.ConfigNodes;

/// <summary>ModuleManager's wildcard match: the whole text, case-sensitive, <c>*</c> for any run and <c>?</c> for one character.</summary>
public static class Wildcard
{
    public static bool IsMatch(string pattern, string text)
    {
        var p = 0;
        var t = 0;
        var starPattern = -1;
        var starText = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || (pattern[p] != '*' && pattern[p] == text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starText = t;
            }
            else if (starPattern >= 0)
            {
                // Let the last star absorb one more character and retry the rest of the pattern from there.
                p = starPattern + 1;
                t = ++starText;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }
}
