namespace PhonecamCameraSource;

public static class Shared
{
    public const string CLSID_VCamNetAOT = "f15889e2-7b53-4ee1-bad4-9e7c51a94d60";
    public const string SharedMemoryName = "Local\\PhonecamFrameBuffer";
    public const uint SharedMemoryMagic = 0x5043414D; // 'PCAM'
    public const int MaxFrameWidth = 1920;
    public const int MaxFrameHeight = 1080;
    public const int HeaderSize = 32;
    public const int MaxBufferSize = HeaderSize + (MaxFrameWidth * MaxFrameHeight * 4);
}
