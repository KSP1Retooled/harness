using System.Globalization;

namespace KspHarness.ConfigNodes;

/// <summary>A colour in KSP's <c>r,g,b</c> or <c>r,g,b,a</c> text form; alpha defaults to 1 as in Unity's <c>Color</c>.</summary>
public readonly struct CfgColor
{
    public CfgColor(float r, float g, float b, float a = 1f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public float R { get; }

    public float G { get; }

    public float B { get; }

    public float A { get; }

    public override string ToString()
    {
        return string.Join(",", Format(R), Format(G), Format(B), Format(A));
    }

    private static string Format(float component)
    {
        return component.ToString("R", CultureInfo.InvariantCulture);
    }
}
