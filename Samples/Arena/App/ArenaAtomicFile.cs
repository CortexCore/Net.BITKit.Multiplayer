namespace BITKit.Multiplayer.Samples.Arena;

// App and Relay reports are local diagnostic files, not a queue. Keep one fully
// written temporary file per update and retry Windows' transient share/delete
// denial briefly; a persistent permission error still fails the process.
internal static class ArenaAtomicFile
{
    private const int Attempts = 6;
    private const int DelayMilliseconds = 25;

    public static void Write(string path, string content)
    {
        var (destination, temporary) = Prepare(path);
        try
        {
            Retry(() => File.WriteAllText(temporary, content));
            Retry(() => File.Move(temporary, destination, true));
        }
        finally { Cleanup(temporary); }
    }

    public static async Task WriteAsync(string path, string content, CancellationToken token)
    {
        var (destination, temporary) = Prepare(path);
        try
        {
            await RetryAsync(() => File.WriteAllTextAsync(temporary, content, token), token);
            await RetryAsync(() => { File.Move(temporary, destination, true); return Task.CompletedTask; }, token);
        }
        finally { Cleanup(temporary); }
    }

    private static (string Destination, string Temporary) Prepare(string path)
    {
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        return (destination, destination + "." + Guid.NewGuid().ToString("N") + ".tmp");
    }

    private static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (IOException) when (attempt < Attempts) { Thread.Sleep(DelayMilliseconds); }
            catch (UnauthorizedAccessException) when (attempt < Attempts) { Thread.Sleep(DelayMilliseconds); }
        }
    }

    private static async Task RetryAsync(Func<Task> action, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { await action(); return; }
            catch (IOException) when (attempt < Attempts) { await Task.Delay(DelayMilliseconds, token); }
            catch (UnauthorizedAccessException) when (attempt < Attempts) { await Task.Delay(DelayMilliseconds, token); }
        }
    }

    private static void Cleanup(string temporary)
    {
        // Never replace a primary write failure with a cleanup error. A temporary
        // file might briefly be scanned/held after the final failed replacement.
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); return; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (attempt < Attempts) Thread.Sleep(DelayMilliseconds);
        }
    }
}
