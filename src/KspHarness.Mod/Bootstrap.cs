using System;
using System.Globalization;
using KspHarness.Mod.Runtime;
using UnityEngine;

namespace KspHarness.Mod;

/// <summary>
/// Starts the harness when the game was launched with <c>-ksp-harness-port</c> and <c>-ksp-harness-token</c>, and
/// otherwise stays out of the way, so the mod can sit in an install that is also played by hand.
/// </summary>
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public sealed class Bootstrap : MonoBehaviour
{
    public const string PortArgument = "-ksp-harness-port";
    public const string TokenArgument = "-ksp-harness-token";

    private void Awake()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        string portText = ArgumentValue(arguments, PortArgument);
        string token = ArgumentValue(arguments, TokenArgument);

        if (portText == null || token == null)
            Debug.Log($"[KspHarness] Not started: the game was launched without {PortArgument} and {TokenArgument}");
        else if (
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port < 1
            || port > 65535
        )
            Debug.LogError($"[KspHarness] Not started: {PortArgument} {portText} is not a port number");
        else if (!IsHex(token))
            Debug.LogError($"[KspHarness] Not started: {TokenArgument} must be hexadecimal");
        else
        {
            LaunchScreen.Apply(arguments);
            HarnessHost.Create(port, token);
        }

        Destroy(gameObject);
    }

    private static string ArgumentValue(string[] arguments, string name)
    {
        for (int i = 0; i < arguments.Length - 1; i++)
        {
            if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
                return arguments[i + 1];
        }

        return null;
    }

    private static bool IsHex(string text)
    {
        if (text.Length == 0)
            return false;

        foreach (char c in text)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        return true;
    }
}
