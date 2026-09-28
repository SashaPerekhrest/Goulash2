using System.Threading.Channels;

namespace Goulash.Api.Providers;

/// <summary>Wakes the background worker; queued jobs themselves are persisted in PostgreSQL.</summary>
public sealed class DiscoveryJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(Guid id, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(id, cancellationToken);

    public ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}
