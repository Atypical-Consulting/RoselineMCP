using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class ClientDisconnectTests
{
    [Fact]
    public async Task Reading_To_End_Of_Stream_Signals_The_Disconnect()
    {
        var disconnect = new ClientDisconnect();
        using var stream = disconnect.Wrap(new MemoryStream([1, 2, 3]));
        var buffer = new byte[8];

        (await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken)).ShouldBe(3);
        disconnect.Token.IsCancellationRequested.ShouldBeFalse();

        (await stream.ReadAsync(buffer.AsMemory(), TestContext.Current.CancellationToken)).ShouldBe(0);
        disconnect.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void A_Zero_Length_Read_Is_Not_End_Of_Stream()
    {
        var disconnect = new ClientDisconnect();
        using var stream = disconnect.Wrap(new MemoryStream([1]));

        stream.Read(new byte[1], 0, 0).ShouldBe(0);
        stream.Read(Span<byte>.Empty).ShouldBe(0);

        disconnect.Token.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public void A_Throwing_Callback_Does_Not_Escape_Signal()
    {
        var disconnect = new ClientDisconnect();
        disconnect.Token.Register(() => throw new InvalidOperationException("boom"));

        Should.NotThrow(disconnect.Signal);
        disconnect.Token.IsCancellationRequested.ShouldBeTrue();
    }
}
