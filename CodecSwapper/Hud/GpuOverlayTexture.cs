using System.Runtime.InteropServices;

namespace CodecSwapper;

/// <summary>
/// One Direct3D 11 texture that the HUD's picture is written into in place. SteamVR keeps showing the same texture while it is rewritten,
/// where re-sending the image with SetOverlayRaw builds a new texture every time and flickers. The slot numbers are the declaration order
/// in d3d11.h (ID3D11Device::CreateTexture2D 5, ID3D11DeviceContext::UpdateSubresource 48 and Flush 111, IUnknown::Release 2).
/// </summary>
public sealed unsafe class GpuOverlayTexture : IDisposable
{
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint levels, uint levelCount, uint sdk, out nint device, out int level, out nint context);

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, MiscFlags;
    }

    private const uint HardwareDriver = 1, Sdk = 7, RgbaUnorm = 28, DefaultUsage = 0, ShaderResource = 8;

    private nint _device, _context, _texture;
    private readonly int _width, _height;

    private GpuOverlayTexture(int width, int height) { _width = width; _height = height; }

    /// <summary>The ID3D11Texture2D to hand to SteamVR.</summary>
    public nint Texture => _texture;

    /// <summary>Null when this PC has no usable Direct3D 11 hardware device.</summary>
    public static GpuOverlayTexture? TryCreate(int width, int height)
    {
        var result = new GpuOverlayTexture(width, height);
        try
        {
            if (D3D11CreateDevice(0, HardwareDriver, 0, 0, 0, 0, Sdk, out result._device, out _, out result._context) < 0) { result.Dispose(); return null; }
            var description = new TextureDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = RgbaUnorm, SampleCount = 1, Usage = DefaultUsage, BindFlags = ShaderResource
            };
            nint texture;
            var hr = ((delegate* unmanaged<nint, TextureDescription*, void*, nint*, int>)Slot(result._device, 5))(result._device, &description, null, &texture);
            if (hr < 0 || texture == 0) { result.Dispose(); return null; }
            result._texture = texture;
            return result;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            result.Dispose();
            return null;
        }
    }

    /// <summary>Overwrites the texture with RGBA bytes (width × height × 4).</summary>
    public void Update(byte[] rgba)
    {
        if (_texture == 0 || rgba.Length < _width * _height * 4) return;
        fixed (byte* pixels = rgba)
            ((delegate* unmanaged<nint, nint, uint, void*, void*, uint, uint, void>)Slot(_context, 48))(_context, _texture, 0, null, pixels, (uint)(_width * 4), 0);
        // The compositor reads on another device: make sure the write has been submitted before it does.
        ((delegate* unmanaged<nint, void>)Slot(_context, 111))(_context);
    }

    public void Dispose()
    {
        Release(ref _texture); Release(ref _context); Release(ref _device);
    }

    private static void Release(ref nint instance)
    {
        if (instance != 0) ((delegate* unmanaged<nint, uint>)Slot(instance, 2))(instance);
        instance = 0;
    }

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];
}
