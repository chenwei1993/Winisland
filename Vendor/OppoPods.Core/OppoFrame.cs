namespace OppoPods.Core;

/// <summary>
/// 外层协议帧:
/// AA [TotalLen] 00 00 [Cmd LE2] [Seq] [PayLen LE2] [Payload...]
/// TotalLen = payloadLen + 7(仅覆盖 Cmd..Payload,不含 AA 与自身),总帧长 = TotalLen + 2,无校验和。
/// </summary>
public readonly record struct OppoFrame(int Command, byte Sequence, byte[] Payload)
{
    public static OppoFrame? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9 || data[0] != 0xAA) return null;
        var totalLength = data[1];
        if (totalLength < 7 || data.Length != totalLength + 2) return null;
        var payloadLength = data[7] | (data[8] << 8);
        if (payloadLength != totalLength - 7 || data.Length < 9 + payloadLength) return null;
        var command = data[4] | (data[5] << 8);
        return new OppoFrame(command, data[6], data.Slice(9, payloadLength).ToArray());
    }

    public byte[] ToBytes()
    {
        var totalLength = Payload.Length + 7;
        var result = new byte[totalLength + 2];
        result[0] = 0xAA;
        result[1] = (byte)totalLength;
        // result[2..3] 保留,固定 0
        result[4] = (byte)Command;
        result[5] = (byte)(Command >> 8);
        result[6] = Sequence;
        result[7] = (byte)Payload.Length;
        result[8] = (byte)(Payload.Length >> 8);
        Payload.CopyTo(result, 9);
        return result;
    }
}
