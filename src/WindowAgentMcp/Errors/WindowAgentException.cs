namespace WindowAgentMcp.Errors;

public sealed class WindowAgentException : Exception
{
    public WindowAgentException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
