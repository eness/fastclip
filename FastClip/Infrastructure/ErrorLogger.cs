namespace FastClip.Infrastructure;

internal sealed class ErrorLogger : IErrorLogger
{
    private readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipboardToSelectedFile",
        "Logs");

    public void Log(string operationId, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var logPath = Path.Combine(_logDirectory, $"{DateTime.UtcNow:yyyyMMdd}.log");
            var lines = new[]
            {
                $"[{DateTime.UtcNow:O}] [{operationId}] {ex.GetType().FullName}",
                ex.Message,
                ex.StackTrace ?? string.Empty,
                string.Empty
            };

            File.AppendAllLines(logPath, lines);
        }
        catch
        {
        }
    }
}
