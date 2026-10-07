namespace WorkHub.Core.Diagnostics;

/// <summary>
/// Minimal diagnostic log. Never pass passwords, tokens, keys or other secrets to it.
/// </summary>
public interface IAppLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}

public sealed class NullAppLog : IAppLog
{
    public static readonly NullAppLog Instance = new();

    public void Info(string message) { }

    public void Warn(string message) { }

    public void Error(string message, Exception? exception = null) { }
}
