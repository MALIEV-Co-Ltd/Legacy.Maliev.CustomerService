using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;

namespace CustomerOriginalOracle;

// Observes stock formatter ownership in its own runtime; only deterministic fixture values leave the process.
internal static class ResourceProbe
{
    internal static async Task<ResourceObservation> RunAsync(string lane, string scenario, IInputFormatter formatter, Type type)
    {
        var large = lane switch
        {
            "customer" => "{\"FirstName\":\"N\",\"LastName\":\"Resource\",\"Email\":\"resource@example.test\",\"Telephone\":\"" + new string('ก', 40000) + "\"}",
            "address" => "{\"Building\":\"N\",\"AddressLine2\":\"" + new string('ก', 40000) + "\"}",
            "company" => "{\"Name\":\"N\",\"TaxNumber\":\"" + new string('ก', 40000) + "\"}",
            _ => throw new InvalidOperationException("Unknown source model.")
        };
        if (scenario is not ("fragmented" or "canceled" or "invalid-utf8"))
            throw new InvalidOperationException("Unknown resource scenario.");
        using var cancellation = new CancellationTokenSource();
        if (scenario == "canceled") cancellation.Cancel();
        using var body = new FragmentedInput(scenario == "invalid-utf8" ? [0xff] : Encoding.UTF8.GetBytes(large));
        var http = new DefaultHttpContext();
        var completion = new CompletionFeature();
        var observed = new HashSet<FileBufferingReadStream>();
        var paths = new HashSet<string>();
        void Observe()
        {
            if (http.Request.Body is FileBufferingReadStream buffer) observed.Add(buffer);
            foreach (var value in completion.Buffers) observed.Add(value);
            foreach (var value in observed)
                if (value.TempFileName is { } path) paths.Add(path);
        }
        http.Features.Set<IHttpResponseFeature>(completion);
        http.Features.Set<IHttpRequestLifetimeFeature>(new LifetimeFeature(cancellation.Token, Observe));
        http.Request.Body = body;
        http.Request.ContentType = "application/json";
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentLength = body.ByteLength;
        body.Observe = Observe;
        var state = new ModelStateDictionary();
        var context = new InputFormatterContext(http, "", state,
            new EmptyModelMetadataProvider().GetMetadataForType(type),
            static (stream, encoding) => new HttpRequestStreamReader(stream, encoding), false);
        bool canRead = formatter.CanRead(context), isSet = false, hasError = false;
        string? first = null, second = null, error = null;
        int bufferCount = 0, pathCount = 0, completionCount = 0;
        bool bufferOpen = false, pathExists = false, released = false;
        try
        {
            try
            {
                var result = await formatter.ReadAsync(context);
                isSet = result.IsModelSet;
                hasError = result.HasError;
                if (result.Model is { } model)
                {
                    first = model.GetType().GetProperty(lane switch { "customer" => "FirstName", "address" => "Building", _ => "Name" })?.GetValue(model) as string;
                    second = model.GetType().GetProperty(lane switch { "customer" => "Telephone", "address" => "AddressLine2", _ => "TaxNumber" })?.GetValue(model) as string;
                }
            }
            catch (Exception exception) { error = exception.GetType().FullName; }
            Observe();
            bufferCount = observed.Count;
            pathCount = paths.Count;
            completionCount = completion.Buffers.Length;
            bufferOpen = observed.Any(value => value.CanRead);
            pathExists = paths.Any(File.Exists);
            await completion.CompleteAsync();
            Observe();
            released = observed.All(value => !value.CanRead) && paths.All(path => !File.Exists(path));
            return new(canRead, isSet, hasError, first, second, state.Count, state.ErrorCount, error,
                bufferCount, pathCount, completionCount, bufferOpen, pathExists, released,
                body.DisposedByFormatter, body.ByteLength, body.BytesRead, body.CancellableTokenObserved);
        }
        finally
        {
            await completion.CompleteAsync();
            Observe();
            foreach (var buffer in observed) await buffer.DisposeAsync();
            if (observed.Any(value => value.CanRead) || paths.Any(File.Exists))
                throw new InvalidOperationException("Original formatter resource cleanup failed.");
        }
    }

    private sealed class LifetimeFeature(CancellationToken token, Action observe) : IHttpRequestLifetimeFeature
    {
        private CancellationToken value = token;
        public CancellationToken RequestAborted { get { observe(); return value; } set => this.value = value; }
        public void Abort() { }
    }

    private sealed class CompletionFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> callbacks = [];
        private readonly List<FileBufferingReadStream> owned = [];
        internal FileBufferingReadStream[] Buffers => owned.ToArray();
        public override void OnCompleted(Func<object, Task> callback, object state)
        {
            callbacks.Add((callback, state));
            if (state is FileBufferingReadStream buffer) owned.Add(buffer);
        }
        internal async Task CompleteAsync()
        {
            var pending = callbacks.ToArray();
            callbacks.Clear();
            List<Exception> failures = [];
            foreach (var item in pending.Reverse())
            {
                try { await item.Callback(item.State); }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
    }

    private sealed class FragmentedInput(byte[] bytes) : Stream
    {
        private readonly MemoryStream input = new(bytes, writable: false);
        internal Action? Observe { get; set; }
        internal long ByteLength => bytes.LongLength;
        internal long BytesRead { get; private set; }
        internal bool CancellableTokenObserved { get; private set; }
        internal bool DisposedByFormatter { get; private set; }
        public override bool CanRead => !DisposedByFormatter;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous request-body IO is forbidden.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CancellableTokenObserved |= cancellationToken.CanBeCanceled;
            Observe?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer.Span[..Math.Min(buffer.Length, 1024)]);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { DisposedByFormatter = true; input.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

internal sealed record ResourceObservation(bool CanRead, bool IsSet, bool HasError, string? First, string? Second,
    int StateCount, int ErrorCount, string? ExceptionType, int BufferCount, int PathCount, int CompletionCount, bool BufferOpen,
    bool PathExists, bool Released, bool BodyDisposed, long ByteLength, long BytesRead, bool CancellableTokenObserved);
