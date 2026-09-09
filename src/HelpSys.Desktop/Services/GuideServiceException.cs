namespace HelpSys.Services;

public enum GuideFailureKind
{
    Network,
    ServiceUnavailable,
    Rejected,
    InvalidResponse,
    ContextChanged
}

public sealed class GuideServiceException : Exception
{
    public GuideServiceException(GuideFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public GuideFailureKind Kind { get; }
}
