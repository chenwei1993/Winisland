namespace OppoPods.Core;

/// <summary>
/// 字节流分帧状态机:处理半包、粘包与垃圾字节重同步。
/// 语义与 Swift 版逐行对齐:找 0xAA → 丢弃前导垃圾 → 长度校验(≥7、帧长 ≤512),
/// 非法长度仅丢弃当前 0xAA 后逐字节重扫;半包等待;粘包依次输出。
/// </summary>
public sealed class OppoPacketFramer
{
    private const int MaxFrameLength = 512;
    private readonly List<byte> _pending = new();

    public List<byte[]> Append(ReadOnlySpan<byte> data)
    {
        var frames = new List<byte[]>();
        if (data.IsEmpty) return frames;
        _pending.AddRange(data.ToArray());

        while (_pending.Count > 0)
        {
            var header = _pending.IndexOf((byte)0xAA);
            if (header < 0)
            {
                _pending.Clear();
                break;
            }
            if (header > 0)
            {
                _pending.RemoveRange(0, header);
            }
            if (_pending.Count < 2) break;

            var totalLength = _pending[1];
            var frameLength = totalLength + 2;
            if (totalLength < 7 || frameLength > MaxFrameLength)
            {
                _pending.RemoveAt(0);
                continue;
            }
            if (_pending.Count < frameLength) break;

            frames.Add(_pending.Take(frameLength).ToArray());
            _pending.RemoveRange(0, frameLength);
        }
        return frames;
    }
}
