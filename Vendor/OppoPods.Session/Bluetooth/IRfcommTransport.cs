namespace OppoPods.Session.Bluetooth;

/// <summary>耳机 RFCOMM 控制通道传输抽象(行为对齐 macOS 版 RFCOMMTransport)。</summary>
public interface IRfcommTransport : IDisposable
{
    /// <summary>收到原始字节流(可能半包/粘包,由上层 Framer 处理)。</summary>
    event Action<byte[]>? OnData;

    /// <summary>通道关闭(远端断开或本端错误)。</summary>
    event Action? OnClosed;

    /// <summary>建立 RFCOMM 控制通道。</summary>
    /// <exception cref="TransportException">连接失败,消息面向最终用户。</exception>
    Task ConnectAsync(ulong bluetoothAddress, CancellationToken cancellationToken);

    Task SendAsync(ReadOnlyMemory<byte> data);

    /// <summary>主动关闭,不触发 <see cref="OnClosed"/>。</summary>
    void Close();
}

/// <summary>传输层错误。</summary>
public sealed class TransportException : Exception
{
    public TransportException(string message) : base(message) { }
}
