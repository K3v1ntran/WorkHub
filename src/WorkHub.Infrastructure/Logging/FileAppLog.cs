using System.Globalization;
using WorkHub.Core.Diagnostics;

namespace WorkHub.Infrastructure.Logging;

/// <summary>
/// Append-only daily text log in <c>Logs\</c>. Writes are synchronous and rare (startup, errors);
/// there is no background flushing thread. Logging failures are swallowed so they never crash the app.
/// Callers must not pass secrets.
/// </summary>
public sealed class FileAppLog(string logsDirectory, TimeProvider? time = null) : IAppLog
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        var now = _time.GetUtcNow();
        var line = $"{now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)} [{level}] {message}{Environment.NewLine}";
        var file = Path.Combine(logsDirectory, $"workhub-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(logsDirectory);
                File.AppendAllText(file, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never take the application down.
            }
        }
    }
}
