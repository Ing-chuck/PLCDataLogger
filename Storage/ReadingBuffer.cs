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

    public ChannelWriter<Reading> Writer => _channel.Writer;
    public ChannelReader<Reading> Reader => _channel.Reader;
}
