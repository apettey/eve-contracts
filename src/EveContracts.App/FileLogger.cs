using System.IO;
using EveContracts.Core;
using Microsoft.Extensions.Logging;

namespace EveContracts.App;

/// <summary>Minimal rolling file logger — the app is WinExe so console output goes nowhere.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider()
    {
        var path = Path.Combine(AppPaths.DataDir, "app.log");
        try { if (File.Exists(path) && new FileInfo(path).Length > 5_000_000) File.Delete(path); } catch { }
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string line) { lock (_lock) _writer.WriteLine(line); }

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var shortCat = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
            provider.Write($"{DateTime.Now:HH:mm:ss.fff} [{logLevel.ToString()[..4].ToUpper()}] {shortCat}: {formatter(state, exception)}{(exception is null ? "" : "\n" + exception)}");
        }
    }
}
