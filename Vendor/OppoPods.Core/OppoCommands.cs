namespace OppoPods.Core;

/// <summary>协议命令 payload 构造。</summary>
public sealed class OppoCommands
{
    private const int GameModeFeatureNew = 0x28;
    private const int GameModeFeatureLegacy = 0x06;

    private readonly OppoPacketBuilder _builder;

    public OppoCommands(OppoPacketBuilder? builder = null)
    {
        _builder = builder ?? new OppoPacketBuilder();
    }

    public byte[] Handshake() => _builder.Make(OppoCommand.Handshake);

    public byte[] QueryProductID() => _builder.Make(OppoCommand.QueryProductID);

    public byte[] QueryBattery() => _builder.Make(OppoCommand.QueryBattery, 0xF0);

    public byte[] QueryANC() => _builder.Make(OppoCommand.QueryANC, payload: new byte[] { 0x01, 0x01 });

    public byte[] QueryEQ() => _builder.Make(OppoCommand.QueryEQ);

    public byte[] QueryAllEQ() => _builder.Make(OppoCommand.QueryAllEQ, payload: new byte[] { 0x01, 0x05 });

    public byte[] QueryBroadcastCodes() => _builder.Make(OppoCommand.QueryBroadcastCodes);

    /// <summary>批量查询 11 个 feature 开关,固定序号 0。</summary>
    public byte[] QueryStatus()
    {
        return _builder.Make(
            OppoCommand.QueryStatus,
            fixedSequence: 0x00,
            payload: new byte[] { 0x0B, 0x05, 0x04, 0x0B, 0x11, 0x13, 0x18, 0x06, 0x1B, 0x1C, 0x27, GameModeFeatureNew });
    }

    public byte[] Subscribe(IReadOnlyList<byte> codes)
    {
        var payload = new byte[1 + codes.Count];
        payload[0] = (byte)Math.Clamp(codes.Count, 0, byte.MaxValue);
        for (var i = 0; i < codes.Count; i++) payload[1 + i] = codes[i];
        return _builder.Make(OppoCommand.SubscribeBroadcast, payload: payload);
    }

    /// <summary>按固定模式设置降噪(无型号索引表时使用)。</summary>
    public byte[] SetNoiseControl(NoiseControlMode mode)
    {
        var payload = mode switch
        {
            NoiseControlMode.Off => new byte[] { 0x01, 0x01, 0x08 },
            NoiseControlMode.NoiseCancellation => new byte[] { 0x01, 0x01, 0x10 },
            NoiseControlMode.Transparency => new byte[] { 0x01, 0x01, 0x00, 0x01 },
            NoiseControlMode.Adaptive => new byte[] { 0x01, 0x01, 0x00, 0x08 },
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return _builder.Make(OppoCommand.SetANC, fixedSequence: 0xF0, payload: payload);
    }

    /// <summary>按型号 protocolIndex 位图设置降噪。</summary>
    public byte[] SetNoiseControl(int protocolIndex)
    {
        var byteCount = protocolIndex / 8 + 1;
        var payload = new byte[2 + byteCount];
        payload[0] = 0x01;
        payload[1] = 0x01;
        payload[2 + protocolIndex / 8] = (byte)(1 << (protocolIndex % 8));
        return _builder.Make(OppoCommand.SetANC, fixedSequence: 0xF0, payload: payload);
    }

    public byte[] SetFeature(byte feature, bool enabled)
    {
        return _builder.Make(
            OppoCommand.SetFeature,
            fixedSequence: 0xF0,
            payload: new byte[] { feature, enabled ? (byte)1 : (byte)0 });
    }

    public byte[] SetSpatialAudio(SpatialAudioMode mode)
    {
        return _builder.Make(
            OppoCommand.SetSpatialAudio,
            fixedSequence: 0xF0,
            payload: new byte[] { (byte)mode });
    }

    public byte[] SetEQ(int presetId)
    {
        return _builder.Make(OppoCommand.SetEQ, payload: new byte[] { (byte)Math.Clamp(presetId, 0, byte.MaxValue) });
    }

    public byte[] SaveCustomEQ(
        int id,
        string name,
        IReadOnlyList<int> frequencies,
        IReadOnlyList<int> gains,
        int minimumGain = -6,
        int maximumGain = 6)
    {
        var payload = EqualizerDetailPayload(
            action: id > 0 ? 2 : 1,
            id: id,
            name: name,
            frequencies: frequencies,
            gains: gains,
            minimumGain: minimumGain,
            maximumGain: maximumGain);
        return _builder.Make(OppoCommand.SetEQDetail, payload: payload);
    }

    public byte[] DeleteCustomEQ(DeviceEqualizerPreset preset)
    {
        var payload = EqualizerDetailPayload(
            action: 3,
            id: preset.Id,
            name: preset.Name,
            frequencies: preset.Frequencies,
            gains: preset.Gains,
            minimumGain: preset.MinimumGain,
            maximumGain: preset.MaximumGain);
        return _builder.Make(OppoCommand.SetEQDetail, payload: payload);
    }

    private static byte[] EqualizerDetailPayload(
        int action,
        int id,
        string name,
        IReadOnlyList<int> frequencies,
        IReadOnlyList<int> gains,
        int minimumGain,
        int maximumGain)
    {
        int[] defaultFrequencies = { 62, 250, 1_000, 4_000, 8_000, 16_000 };
        var source = frequencies.Count == 0 ? defaultFrequencies : frequencies.ToArray();
        var safeFrequencies = source.Take(32).ToArray();
        var lower = Math.Clamp(Math.Min(minimumGain, maximumGain), -128, 127);
        var upper = Math.Clamp(Math.Max(minimumGain, maximumGain), lower, 127);
        var maximumNameLength = Math.Max(0, 248 - 6 - safeFrequencies.Length * 3);
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name).Take(Math.Min(255, maximumNameLength)).ToArray();

        var payload = new List<byte>
        {
            (byte)Math.Clamp(action, 1, 3),
            unchecked((byte)(sbyte)lower),
            unchecked((byte)(sbyte)upper),
            (byte)Math.Clamp(id, 0, byte.MaxValue),
            (byte)nameBytes.Length,
        };
        payload.AddRange(nameBytes);
        payload.Add((byte)safeFrequencies.Length);
        for (var i = 0; i < safeFrequencies.Length; i++)
        {
            var frequency = Math.Clamp(safeFrequencies[i], 0, 0xFFFF);
            payload.Add((byte)frequency);
            payload.Add((byte)(frequency >> 8));
            var gain = Math.Clamp(i < gains.Count ? gains[i] : 0, lower, upper);
            payload.Add(unchecked((byte)(sbyte)gain));
        }
        return payload.ToArray();
    }
}
