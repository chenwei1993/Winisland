namespace OppoPods.Core;

/// <summary>按 productID 解析出的型号能力表,驱动 UI 功能显隐与协议参数选择。</summary>
public sealed record DeviceCapabilities(
    string ModelID,
    string ModelName,
    IReadOnlyDictionary<int, NoiseControlMode> NoiseModeByIndex,
    IReadOnlyDictionary<NoiseControlMode, int> IndexByNoiseMode,
    bool HasAdaptiveANC,
    bool HasNoiseLevels,
    bool HasGameMode,
    bool HasSpatialSound,
    bool HasSpatialAudioModes,
    bool HasWearDetection,
    bool HasDualDevice,
    IReadOnlyList<EqualizerPreset> EqualizerPresets,
    bool HasCustomEQ,
    IReadOnlyList<int> CustomEQFrequencies,
    int CustomEQMaximumPresets)
{
    public static readonly DeviceCapabilities Generic = new(
        ModelID: "",
        ModelName: "未识别型号",
        NoiseModeByIndex: new Dictionary<int, NoiseControlMode>(),
        IndexByNoiseMode: new Dictionary<NoiseControlMode, int>(),
        HasAdaptiveANC: true,
        HasNoiseLevels: false,
        HasGameMode: true,
        HasSpatialSound: true,
        HasSpatialAudioModes: false,
        HasWearDetection: false,
        HasDualDevice: false,
        EqualizerPresets: Array.Empty<EqualizerPreset>(),
        HasCustomEQ: false,
        CustomEQFrequencies: Array.Empty<int>(),
        CustomEQMaximumPresets: 0);
}
