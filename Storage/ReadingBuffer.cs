using System.Threading.Channels;

namespace PlcDataLogger.Storage;

/// <summary>
/// In-memory queue (§5 "Data Buffer") that decouples OPC UA acquisition from the
/// database writer, so a slow disk write never causes the client to miss notifications.
/// </summary>
public sealed class ReadingBuffer
{
    private readonly Channel<Reading> _channel =
        Channel.CreateUnbounded<Reading>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private long _enqueued;

    public ChannelReader<Reading> Reader => _channel.Reader;

    /// <summary>Total readings ever accepted into the buffer (for queue-depth health).</summary>
    public long Enqueued => Interlocked.Read(ref _enqueued);

    /// <summary>Enqueue a reading, counting it for queue-depth reporting.</summary>
    public bool TryWrite(Reading reading)
    {
        if (!_channel.Writer.TryWrite(reading))
            return false;
        Interlocked.Increment(ref _enqueued);
        return true;
    }
}
