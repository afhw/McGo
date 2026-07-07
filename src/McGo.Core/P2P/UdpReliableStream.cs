using System.Buffers.Binary;
using System.Threading.Channels;

namespace McGo.Core.P2P;

public enum UdpFrameKind : byte
{
    Syn = 1,
    SynAck = 2,
    Data = 3,
    Ack = 4,
    Fin = 5,
    KeepAlive = 6,
}

public sealed record UdpFrame(UdpFrameKind Kind, ushort StreamId, byte[] Token, uint Seq = 0, uint Ack = 0, byte[]? Payload = null)
{
    public const int HeaderSize = 26;
    public const int PayloadSize = 1000;
    private static readonly byte[] Magic = "MGU1"u8.ToArray();

    public byte[] Pack()
    {
        var payload = Payload ?? [];
        if (payload.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("UDP frame payload is too large.");
        }

        var packet = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(packet.AsSpan(0, 4));
        packet[4] = (byte)Kind;
        packet[5] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), StreamId);
        NormalizeToken(Token).CopyTo(packet.AsSpan(8, 8));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16, 4), Seq);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(20, 4), Ack);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24, 2), (ushort)payload.Length);
        payload.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    public static UdpFrame? Unpack(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..4].SequenceEqual(Magic))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(24, 2));
        if (length != data.Length - HeaderSize)
        {
            return null;
        }

        var kind = (UdpFrameKind)data[4];
        var streamId = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6, 2));
        var token = data.Slice(8, 8).ToArray();
        var seq = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var ack = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        var payload = data.Slice(HeaderSize, length).ToArray();
        return new UdpFrame(kind, streamId, token, seq, ack, payload);
    }

    public static byte[] NormalizeToken(byte[]? token)
    {
        var result = new byte[8];
        if (token is null)
        {
            return result;
        }

        token.AsSpan(0, Math.Min(8, token.Length)).CopyTo(result);
        return result;
    }
}

public interface IUdpFrameTransport
{
    void Send(UdpFrame frame);
}

public sealed class UdpReliableStream
{
    private const int MaxRetries = 24;
    private static readonly TimeSpan ResendTimeout = TimeSpan.FromMilliseconds(350);
    private readonly IUdpFrameTransport _transport;
    private readonly Dictionary<uint, TaskCompletionSource> _pendingAcks = [];
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private uint _sendSeq = 1;
    private uint _expectedSeq = 1;

    public UdpReliableStream(IUdpFrameTransport transport, ushort streamId, byte[] token, bool initiator = false)
    {
        _transport = transport;
        StreamId = streamId;
        Token = UdpFrame.NormalizeToken(token);
        Initiator = initiator;
    }

    public ushort StreamId { get; }
    public byte[] Token { get; }
    public bool Initiator { get; }
    public bool Closed { get; private set; }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var waiter = AddPendingAck(0);
        try
        {
            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                _transport.Send(new UdpFrame(UdpFrameKind.Syn, StreamId, Token));
                if (await WaitAckAsync(waiter, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            RemovePendingAck(0);
        }
    }

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        while (offset < data.Length && !Closed)
        {
            var length = Math.Min(UdpFrame.PayloadSize, data.Length - offset);
            var chunk = data.AsSpan(offset, length).ToArray();
            await SendReliableAsync(UdpFrameKind.Data, chunk, cancellationToken);
            offset += length;
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Closed)
        {
            return;
        }

        Closed = true;
        await SendReliableAsync(UdpFrameKind.Fin, [], cancellationToken);
    }

    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken = default)
        => await _incoming.Reader.ReadAsync(cancellationToken);

    public void HandleFrame(UdpFrame frame)
    {
        if (frame.StreamId != StreamId || !frame.Token.SequenceEqual(Token))
        {
            return;
        }

        switch (frame.Kind)
        {
            case UdpFrameKind.Syn:
                _transport.Send(new UdpFrame(UdpFrameKind.SynAck, StreamId, Token, Ack: frame.Seq));
                break;
            case UdpFrameKind.SynAck:
                CompletePendingAck(0);
                break;
            case UdpFrameKind.Ack:
                CompletePendingAck(frame.Ack);
                break;
            case UdpFrameKind.Data:
                if (frame.Seq == _expectedSeq)
                {
                    _expectedSeq++;
                    _incoming.Writer.TryWrite(frame.Payload ?? []);
                }

                _transport.Send(new UdpFrame(UdpFrameKind.Ack, StreamId, Token, Ack: frame.Seq));
                break;
            case UdpFrameKind.Fin:
                _transport.Send(new UdpFrame(UdpFrameKind.Ack, StreamId, Token, Ack: frame.Seq));
                Closed = true;
                _incoming.Writer.TryWrite([]);
                break;
        }
    }

    private async Task SendReliableAsync(UdpFrameKind kind, byte[] payload, CancellationToken cancellationToken)
    {
        var seq = _sendSeq++;
        var waiter = AddPendingAck(seq);
        try
        {
            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                _transport.Send(new UdpFrame(kind, StreamId, Token, Seq: seq, Payload: payload));
                if (await WaitAckAsync(waiter, cancellationToken))
                {
                    return;
                }
            }
        }
        finally
        {
            RemovePendingAck(seq);
        }

        throw new TimeoutException("UDP stream ack timeout.");
    }

    private TaskCompletionSource AddPendingAck(uint seq)
    {
        lock (_pendingAcks)
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[seq] = waiter;
            return waiter;
        }
    }

    private void RemovePendingAck(uint seq)
    {
        lock (_pendingAcks)
        {
            _pendingAcks.Remove(seq);
        }
    }

    private void CompletePendingAck(uint seq)
    {
        lock (_pendingAcks)
        {
            if (_pendingAcks.TryGetValue(seq, out var waiter))
            {
                waiter.TrySetResult();
            }
        }
    }

    private static async Task<bool> WaitAckAsync(TaskCompletionSource waiter, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(waiter.Task, Task.Delay(ResendTimeout, cancellationToken));
        return completed == waiter.Task;
    }
}
