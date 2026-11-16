namespace OppoPods.Core;

public enum ConnectionStateKind
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

/// <summary>带关联值的连接状态(失败态携带错误信息)。</summary>
public sealed record ConnectionState(ConnectionStateKind Kind, string? Error = null)
{
    public static readonly ConnectionState Disconnected = new(ConnectionStateKind.Disconnected);
    public static readonly ConnectionState Connecting = new(ConnectionStateKind.Connecting);
    public static readonly ConnectionState Connected = new(ConnectionStateKind.Connected);
    public static ConnectionState FailedState(string error) => new(ConnectionStateKind.Failed, error);
}

public enum NoiseControlMode
{
    Off = 0,
    NoiseCancellation = 1,
    Transparency = 2,
    Adaptive = 3,
}

public enum SpatialAudioMode
{
    Off = 0,
    Fixed = 1,
    HeadTracking = 2,
}

public sealed record BatteryLevel(int Level, bool IsCharging);

public sealed record BatteryStatus(
    BatteryLevel? Left = null,
    BatteryLevel? Right = null,
    BatteryLevel? ChargingCase = null)
{
    /// <summary>菜单栏/托盘显示电量:左右耳中较小者。</summary>
    public int? MenuBarLevel
    {
        get
        {
            int? min = null;
            if (Left != null) min = Left.Level;
            if (Right != null && (min == null || Right.Level < min)) min = Right.Level;
            return min;
        }
    }
}

public sealed record FeatureStatus
{
    public bool? GameMode { get; init; }
    public bool? LowLatencyMode { get; init; }
    public bool? WearDetection { get; init; }
    public bool? DualDevice { get; init; }
    public bool? SpatialSound { get; init; }
}

public sealed record EqualizerPreset(int Id, string Name);

public sealed record DeviceEqualizerPreset(
    int Id,
    string Name,
    bool IsSelected = false,
    int MinimumGain = -6,
    int MaximumGain = 6,
    int[]? Frequencies = null,
    int[]? Gains = null)
{
    public int[] Frequencies { get; init; } = Frequencies ?? Array.Empty<int>();
    public int[] Gains { get; init; } = Gains ?? Array.Empty<int>();
}
