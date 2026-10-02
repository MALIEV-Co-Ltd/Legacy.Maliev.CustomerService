using System.Diagnostics;
using System.Text;

// Test-only runtime convention: this type must remain in the global namespace.
internal static class StartupHook
{
    private static readonly object Gate = new();
    private static string? phaseFile;
    private static int emitted;
    private static IDisposable? allListeners;
    private static IDisposable? hostingListener;

    public static void Initialize()
    {
        try
        {
            var candidate = Environment.GetEnvironmentVariable("MALIEV_TEST_CUSTOMER_PHASE_FILE");
            if (string.IsNullOrEmpty(candidate) || !Path.IsPathFullyQualified(candidate)) return;
            var full = Path.GetFullPath(candidate);
            var owner = Path.GetDirectoryName(full);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (owner is null || !full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(owner).StartsWith("private-customer-startup-sentinel-", StringComparison.Ordinal)
                || Path.GetFileName(full) != "startup-phase-codes.txt" || !File.Exists(full)) return;
            phaseFile = full;
            Emit(1);
            allListeners = DiagnosticListener.AllListeners.Subscribe(new ListenerObserver());
            GC.KeepAlive(allListeners);
        }
        catch
        {
            // Probe setup must not replace application behavior or expose an error payload.
        }
    }

    private static void Emit(int code)
    {
        lock (Gate)
        {
            var bit = 1 << code;
            if (phaseFile is null || (emitted & bit) != 0) return;
            try
            {
                using var stream = new FileStream(phaseFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = Encoding.ASCII.GetBytes(code switch { 1 => "1\n", 2 => "2\n", 3 => "3\n", _ => throw new InvalidOperationException() });
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                emitted |= bit;
            }
            catch
            {
                // No logging, exception text or change to the observed host is permitted.
            }
        }
    }

    private sealed class ListenerObserver : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != "Microsoft.Extensions.Hosting") return;
            lock (Gate)
            {
                hostingListener ??= listener.Subscribe(new EventObserver(), name => name is "HostBuilding" or "HostBuilt");
            }
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class EventObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> entry)
        {
            // Deliberately never read entry.Value, resolve IHost or inspect configuration/DI.
            if (entry.Key == "HostBuilding") Emit(2);
            else if (entry.Key == "HostBuilt") Emit(3);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
