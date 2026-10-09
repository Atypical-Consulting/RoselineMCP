namespace RoselineMCP.Services;

/// <summary>
/// A token that fires when the MCP client closes stdin (EOF). The SDK offers none: measured in #261
/// against ModelContextProtocol 2.2.0, neither an in-flight handler's cancellation token nor
/// <c>IHostApplicationLifetime.ApplicationStopping</c> moves on EOF, because the SDK drains
/// in-flight handlers before it shuts down. So the server owns the signal by wrapping the input
/// stream it hands the transport (<see cref="Wrap"/>). Cancellation is one-way and permanent.
/// </summary>
public sealed class ClientDisconnect
{
    private readonly CancellationTokenSource _eof = new();

    /// <summary>Cancelled once the wrapped input stream has reported end-of-stream.</summary>
    public CancellationToken Token => _eof.Token;

    /// <summary>Raises the signal. Public so tests can simulate an EOF without real pipes.</summary>
    public void Signal() => _eof.Cancel();

    /// <summary>Wraps <paramref name="input"/> so reading end-of-stream raises <see cref="Signal"/>.</summary>
    public Stream Wrap(Stream input) => new EofSignalingStream(input, this);

    private sealed class EofSignalingStream(Stream inner, ClientDisconnect owner) : Stream
    {
        private int Observe(int read)
        {
            if (read == 0)
            {
                owner.Signal();
            }

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Observe(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Observe(inner.Read(buffer));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Observe(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Observe(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
