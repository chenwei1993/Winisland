using System.Text;

namespace OppoPods.Core;

/// <summary>协议响应解析。</summary>
public static class OppoParsers
{
    public static BatteryStatus? Battery(in OppoFrame frame)
    {
        ReadOnlySpan<byte> bytes;
        var payload = frame.Payload;
        if (frame.Command == OppoCommand.BatteryResponse)
        {
            bytes = payload;
        }
        else if (frame.Command == OppoCommand.ActiveReport)
        {
            if (payload.Length < 2 || payload[0] != 0x01) return null;
            var count = Math.Min(payload[1], (payload.Length - 2) / 2);
            bytes = payload.AsSpan(2, count * 2);
        }
        else
        {
            return null;
        }

        var status = new BatteryStatus();
        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            var component = bytes[i];
            var raw = bytes[i + 1];
            var value = new BatteryLevel(raw & 0x7F, (raw & 0x80) != 0);
            status = component switch
            {
                1 => status with { Left = value },
                2 => status with { Right = value },
                3 => status with { ChargingCase = value },
                _ => status,
            };
        }
        return status;
    }

    public static string? ProductID(in OppoFrame frame)
    {
        var bytes = frame.Payload;
        if (frame.Command != OppoCommand.ProductIDResponse || bytes.Length != 4 || bytes[0] != 0) return null;
        var identifier = bytes[1] | (bytes[2] << 8) | (bytes[3] << 16);
        return identifier.ToString("X6");
    }

    public static NoiseControlMode? NoiseControl(in OppoFrame frame)
    {
        if (frame.Command != OppoCommand.ANCResponse && frame.Command != OppoCommand.ActiveReport) return null;
        var bytes = frame.Payload;
        if (frame.Command == OppoCommand.ActiveReport && bytes.Length > 0 && bytes[0] != 0x03 && bytes[0] != 0x04)
        {
            return null;
        }

        var index = FindAnchor(bytes);
        if (index < 0) return null;
        var low = bytes[index + 2];
        var high = index + 3 < bytes.Length ? bytes[index + 3] : 0;
        return (low, high) switch
        {
            (0x10, 0x00) or (0x20, 0x00) or (0x40, 0x00) or (0x80, 0x00) => NoiseControlMode.NoiseCancellation,
            (0x00, 0x01) => NoiseControlMode.Transparency,
            (0x08, 0x00) => NoiseControlMode.Off,
            (0x00, 0x08) => NoiseControlMode.Adaptive,
            _ => null,
        };
    }

    public static NoiseControlMode? NoiseControl(in OppoFrame frame, IReadOnlyDictionary<int, NoiseControlMode> indexToMode)
    {
        if (indexToMode.Count == 0 ||
            (frame.Command != OppoCommand.ANCResponse && frame.Command != OppoCommand.ActiveReport))
        {
            return NoiseControl(frame);
        }
        var bytes = frame.Payload;
        var index = FindAnchor(bytes);
        if (index < 0) return null;
        var low = bytes[index + 2];
        var high = index + 3 < bytes.Length ? bytes[index + 3] : 0;
        var bitmap = low | (high << 8);
        for (var bit = 0; bit < 16; bit++)
        {
            if ((bitmap & (1 << bit)) != 0 && indexToMode.TryGetValue(bit, out var mode)) return mode;
        }
        return NoiseControl(frame);
    }

    public static FeatureStatus? FeatureStatus(in OppoFrame frame)
    {
        if (frame.Command != OppoCommand.StatusResponse) return null;
        var bytes = frame.Payload;
        var pairs = new List<(byte Feature, byte RawValue)>();
        if (bytes.Length >= 2 && bytes[0] == 0 && bytes.Length >= 2 + bytes[1] * 2)
        {
            var count = bytes[1];
            for (var i = 0; i < count; i++)
            {
                var offset = 2 + i * 2;
                pairs.Add((bytes[offset], bytes[offset + 1]));
            }
        }
        else
        {
            for (var i = 0; i + 1 < bytes.Length; i++)
            {
                if (bytes[i + 1] <= 1) pairs.Add((bytes[i], bytes[i + 1]));
            }
        }

        var result = new FeatureStatus();
        foreach (var (feature, rawValue) in pairs)
        {
            var enabled = rawValue == 1;
            result = feature switch
            {
                0x28 => result with { GameMode = enabled },
                0x06 => result with { LowLatencyMode = enabled },
                0x04 => result with { WearDetection = enabled },
                0x11 => result with { DualDevice = enabled },
                0x1B => result with { SpatialSound = enabled },
                _ => result,
            };
        }
        return result.Equals(new FeatureStatus()) ? null : result;
    }

    public static SpatialAudioMode? SpatialAudio(in OppoFrame frame)
    {
        if (frame.Command != OppoCommand.SpatialAudioNotify || frame.Payload.Length == 0) return null;
        return Enum.IsDefined(typeof(SpatialAudioMode), (int)frame.Payload[0])
            ? (SpatialAudioMode)frame.Payload[0]
            : null;
    }

    public static byte[]? BroadcastCodes(in OppoFrame frame)
    {
        var bytes = frame.Payload;
        if (frame.Command != OppoCommand.BroadcastCodesResponse && frame.Command != OppoCommand.BroadcastCodesResponseAlt)
        {
            return null;
        }
        if (bytes.Length < 2 || bytes[0] != 0) return null;
        var count = Math.Min(bytes[1], bytes.Length - 2);
        return bytes.Skip(2).Take(count).ToArray();
    }

    public static int? CurrentEQ(in OppoFrame frame)
    {
        var bytes = frame.Payload;
        if (frame.Command != OppoCommand.EQResponse || bytes.Length < 2 || bytes[0] != 0) return null;
        return bytes[1];
    }

    public static List<DeviceEqualizerPreset>? AllEqualizers(in OppoFrame frame)
    {
        if (frame.Command != OppoCommand.AllEQResponse) return null;
        var bytes = frame.Payload;
        if (bytes.Length < 2 || bytes[0] != 0) return null;

        var expectedCount = bytes[1];
        var position = 2;
        var presets = new List<DeviceEqualizerPreset>();
        while (presets.Count < expectedCount)
        {
            if (position + 5 > bytes.Length) return null;
            var selected = bytes[position] != 0;
            var minimumGain = (sbyte)bytes[position + 1];
            var maximumGain = (sbyte)bytes[position + 2];
            var presetId = bytes[position + 3];
            var nameLength = bytes[position + 4];
            position += 5;
            if (position + nameLength > bytes.Length) return null;
            var name = Encoding.UTF8.GetString(bytes, position, nameLength).Trim();
            position += nameLength;

            if (position >= bytes.Length) return null;
            var frequencyCount = bytes[position];
            position += 1;
            if (position + frequencyCount * 3 > bytes.Length) return null;
            var frequencies = new int[frequencyCount];
            var gains = new int[frequencyCount];
            for (var i = 0; i < frequencyCount; i++)
            {
                frequencies[i] = bytes[position] | (bytes[position + 1] << 8);
                gains[i] = (sbyte)bytes[position + 2];
                position += 3;
            }
            presets.Add(new DeviceEqualizerPreset(
                Id: presetId,
                Name: name,
                IsSelected: selected,
                MinimumGain: minimumGain,
                MaximumGain: maximumGain,
                Frequencies: frequencies,
                Gains: gains));
        }
        return presets;
    }

    /// <summary>扫描连续 01 01 特征,返回其起始索引;-1 表示未找到。范围与 Swift 版一致:0..&lt;(count-2)。</summary>
    private static int FindAnchor(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i + 2 < bytes.Length; i++)
        {
            if (bytes[i] == 0x01 && bytes[i + 1] == 0x01) return i;
        }
        return -1;
    }
}
