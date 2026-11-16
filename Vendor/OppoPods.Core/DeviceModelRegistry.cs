using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OppoPods.Core;

/// <summary>
/// 加载 device_models.json 型号白名单与 EQ 名称表,按 productID 生成能力表。
/// 资源以 EmbeddedResource 随程序集分发。
/// </summary>
public sealed class DeviceModelRegistry
{
    private sealed record Root([property: JsonPropertyName("whiteList")] Model[]? WhiteList);

    private sealed record Model(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("supportSpp")] bool? SupportSpp,
        [property: JsonPropertyName("function")] Functions? Function);

    private sealed record Functions(
        [property: JsonPropertyName("noiseReductionMode")] NoiseMode[]? NoiseReductionMode,
        [property: JsonPropertyName("spatialTypes")] int[]? SpatialTypes,
        [property: JsonPropertyName("multiDevicesConnect")] int? MultiDevicesConnect,
        [property: JsonPropertyName("wearDetection")] int? WearDetection,
        [property: JsonPropertyName("gameMode")] int? GameMode,
        [property: JsonPropertyName("gameModeList")] GameModeItem[]? GameModeList,
        [property: JsonPropertyName("gameSoundList")] GameSoundItem[]? GameSoundList,
        [property: JsonPropertyName("customEqualizer")] int? CustomEqualizer,
        [property: JsonPropertyName("customEqFrequency")] int[]? CustomEqFrequency,
        [property: JsonPropertyName("customEqMax")] int? CustomEqMax,
        [property: JsonPropertyName("equalizerMode")] EQMode[]? EqualizerMode,
        [property: JsonPropertyName("equalizerModeCompat")] EQMode[]? EqualizerModeCompat,
        [property: JsonPropertyName("equalizerModeByVersion")] EQMode[]? EqualizerModeByVersion);

    private sealed record NoiseMode(
        [property: JsonPropertyName("modeType")] int ModeType,
        [property: JsonPropertyName("protocolIndex")] int ProtocolIndex,
        [property: JsonPropertyName("childrenMode")] NoiseMode[]? ChildrenMode);

    private sealed record GameModeItem([property: JsonPropertyName("gameMode")] int? GameMode);

    private sealed record GameSoundItem([property: JsonPropertyName("type")] int? Type);

    private sealed record EQMode(
        [property: JsonPropertyName("modeType")] int? ModeType,
        [property: JsonPropertyName("protocolIndex")] int? ProtocolIndex);

    private sealed record EQNames([property: JsonPropertyName("mapping")] Dictionary<string, string>? Mapping);

    private readonly Dictionary<string, Model> _modelsByID = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _eqNames = new();

    public DeviceModelRegistry()
    {
        if (TryReadResource("device_models.json", out var modelsData))
        {
            var root = JsonSerializer.Deserialize<Root>(modelsData);
            if (root?.WhiteList != null)
            {
                foreach (var model in root.WhiteList)
                {
                    var id = model.Id?.ToUpperInvariant();
                    if (!string.IsNullOrEmpty(id)) _modelsByID[id] = model;
                }
            }
        }

        // 与 macOS 版一致:zh 使用中文名,其余语言使用英文名。
        var resource = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
            ? "eq_mode_names.json"
            : "eq_mode_names.en.json";
        if (TryReadResource(resource, out var namesData))
        {
            var names = JsonSerializer.Deserialize<EQNames>(namesData);
            if (names?.Mapping != null)
            {
                foreach (var (key, value) in names.Mapping)
                {
                    if (int.TryParse(key, out var id)) _eqNames[id] = value;
                }
            }
        }
    }

    public int ModelCount => _modelsByID.Count;

    public DeviceCapabilities? CapabilitiesForName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var model = _modelsByID.Values.FirstOrDefault(m =>
            string.Equals(m.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        return model?.Id is { Length: > 0 } id ? CapabilitiesFor(id) : null;
    }

    public DeviceCapabilities? CapabilitiesFor(string productID)
    {
        var id = productID.ToUpperInvariant();
        if (!_modelsByID.TryGetValue(id, out var model) || model.SupportSpp == false) return null;
        var functions = model.Function;

        var byIndex = new Dictionary<int, NoiseControlMode>();
        var byMode = new Dictionary<NoiseControlMode, int>();
        var hasNoiseLevels = false;

        foreach (var entry in functions?.NoiseReductionMode ?? Array.Empty<NoiseMode>())
        {
            if (NoiseModeFor(entry.ModeType) is not { } mode) continue;
            byIndex[entry.ProtocolIndex] = mode;
            byMode[mode] = entry.ProtocolIndex;
            foreach (var child in entry.ChildrenMode ?? Array.Empty<NoiseMode>())
            {
                if (child.ModeType is 3 or 4 or 7 or 8) hasNoiseLevels = true;
                var childMode = NoiseModeFor(child.ModeType) ?? mode;
                byIndex[child.ProtocolIndex] = childMode;
                // 子档位同样可被 SetNoiseControl 按位图选中(如 Enco X3 自适应 = 通透子项 index 9)
                if (!byMode.ContainsKey(childMode)) byMode[childMode] = child.ProtocolIndex;
            }
        }

        var spatialTypes = functions?.SpatialTypes ?? Array.Empty<int>();
        var allEQ = (functions?.EqualizerMode ?? Array.Empty<EQMode>())
            .Concat(functions?.EqualizerModeCompat ?? Array.Empty<EQMode>())
            .Concat(functions?.EqualizerModeByVersion ?? Array.Empty<EQMode>());
        var presetsByID = new Dictionary<int, EqualizerPreset>();
        foreach (var entry in allEQ)
        {
            if (entry.ProtocolIndex == null || presetsByID.ContainsKey(entry.ProtocolIndex.Value)) continue;
            var presetId = entry.ProtocolIndex.Value;
            var name = entry.ModeType != null && _eqNames.TryGetValue(entry.ModeType.Value, out var eqName)
                ? eqName
                : $"M{presetId}";
            presetsByID[presetId] = new EqualizerPreset(presetId, name);
        }

        // Swift 源码为 explicitGame ?? (listGame || gameSound || true),兜底分支恒为 true:
        // 无显式 gameMode 字段 → true;有显式字段 → 按字段值 > 0。
        var hasGameMode = functions?.GameMode is not { } explicitGame || explicitGame > 0;

        return new DeviceCapabilities(
            ModelID: id,
            ModelName: model.Name ?? id,
            NoiseModeByIndex: byIndex,
            IndexByNoiseMode: byMode,
            HasAdaptiveANC: byMode.ContainsKey(NoiseControlMode.Adaptive),
            HasNoiseLevels: hasNoiseLevels,
            HasGameMode: hasGameMode,
            HasSpatialSound: spatialTypes.Length > 0 && !spatialTypes.Contains(2),
            HasSpatialAudioModes: spatialTypes.Contains(2),
            HasWearDetection: (functions?.WearDetection ?? 0) > 0,
            HasDualDevice: (functions?.MultiDevicesConnect ?? 0) > 0,
            EqualizerPresets: presetsByID.Values.OrderBy(p => p.Id).ToArray(),
            HasCustomEQ: (functions?.CustomEqualizer ?? 0) > 0,
            CustomEQFrequencies: (functions?.CustomEqFrequency ?? Array.Empty<int>()).Where(f => f > 0).ToArray(),
            CustomEQMaximumPresets: Math.Max(0, functions?.CustomEqMax ?? 0));
    }

    private static NoiseControlMode? NoiseModeFor(int type)
    {
        return type switch
        {
            1 => NoiseControlMode.Off,
            2 => NoiseControlMode.Transparency,
            5 or 3 or 4 or 7 or 8 => NoiseControlMode.NoiseCancellation,
            6 or 10 => NoiseControlMode.Adaptive,
            _ => null,
        };
    }

    private static bool TryReadResource(string fileName, out byte[] data)
    {
        var assembly = typeof(DeviceModelRegistry).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal));
        if (name == null)
        {
            data = Array.Empty<byte>();
            return false;
        }
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        data = memory.ToArray();
        return true;
    }
}
