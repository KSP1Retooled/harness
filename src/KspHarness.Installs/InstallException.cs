namespace KspHarness.Installs;

public class InstallException : Exception
{
    public InstallException(string message)
        : base(message)
    {
    }

    public InstallException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>A CKAN run failed. The message ends with the tail of the CKAN log.</summary>
public sealed class CkanException : InstallException
{
    public CkanException(string message, string logPath, int exitCode)
        : base(message)
    {
        LogPath = logPath;
        ExitCode = exitCode;
    }

    public string LogPath { get; }

    public int ExitCode { get; }
}
