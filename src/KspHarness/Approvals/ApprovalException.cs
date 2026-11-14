namespace KspHarness.Approvals;

/// <summary>A value or screenshot does not match its approved file, or has none yet.</summary>
public sealed class ApprovalException : Exception
{
    internal ApprovalException(string message, string approvedPath, string? receivedPath, string? diffPath = null)
        : base(message)
    {
        ApprovedPath = approvedPath;
        ReceivedPath = receivedPath;
        DiffPath = diffPath;
    }

    public string ApprovedPath { get; }

    /// <summary>The received file written next to the approved one, or null when none was written.</summary>
    public string? ReceivedPath { get; }

    /// <summary>The highlighted difference image of a screenshot comparison, when there is one.</summary>
    public string? DiffPath { get; }
}
