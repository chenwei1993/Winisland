namespace OppoPods.Core;

/// <summary>命令封包器,维护自动递增序号(1..0xFE,到 0xFE 后回绕到 1)。</summary>
public sealed class OppoPacketBuilder
{
    private readonly object _gate = new();
    private byte _sequence = 1;

    public byte[] Make(int command, byte[]? payload = null)
    {
        byte sequence;
        lock (_gate)
        {
            sequence = _sequence;
            _sequence = _sequence >= 0xFE ? (byte)1 : (byte)(_sequence + 1);
        }
        return new OppoFrame(command, sequence, payload ?? Array.Empty<byte>()).ToBytes();
    }

    /// <summary>固定序号封包(部分查询/设置命令要求固定 0xF0 或 0,设备据此区分查询与设置,不可改为自增)。</summary>
    public byte[] Make(int command, byte fixedSequence, byte[]? payload = null)
    {
        return new OppoFrame(command, fixedSequence, payload ?? Array.Empty<byte>()).ToBytes();
    }
}
