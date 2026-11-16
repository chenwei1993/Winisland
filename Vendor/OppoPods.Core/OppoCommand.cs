namespace OppoPods.Core;

/// <summary>HeyMelody 协议命令码。请求与响应对应:响应 = 请求 | 0x8000。</summary>
public static class OppoCommand
{
    // 请求
    public const int Handshake = 0x0100;
    public const int QueryProductID = 0x0103;
    public const int QueryBattery = 0x0106;
    public const int QueryStatus = 0x010D;
    public const int QueryANC = 0x010C;
    public const int QueryEQ = 0x010F;
    public const int QueryAllEQ = 0x0122;
    public const int QueryBroadcastCodes = 0x0200;
    public const int SubscribeBroadcast = 0x0205;
    public const int ActiveReport = 0x0204;
    public const int SetFeature = 0x0403;
    public const int SetANC = 0x0404;
    public const int SetEQ = 0x0406;
    public const int SetEQDetail = 0x0418;
    public const int SetSpatialAudio = 0x0422;
    public const int SpatialAudioNotify = 0x0510;

    // 响应
    public const int ProductIDResponse = 0x8103;
    public const int BatteryResponse = 0x8106;
    public const int StatusResponse = 0x810D;
    public const int ANCResponse = 0x810C;
    public const int EQResponse = 0x810F;
    public const int AllEQResponse = 0x8122;
    public const int SetEQDetailResponse = 0x8418;
    public const int BroadcastCodesResponse = 0x8200;

    /// <summary>防御性兼容:部分固件版本可能以 0x8203 回应事件码查询(与 0x8200 同格式)。</summary>
    public const int BroadcastCodesResponseAlt = 0x8203;
}
