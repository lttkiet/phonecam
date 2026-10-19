namespace PhonecamCameraSource;

public class FrameGenerator : IDisposable
{
    private static readonly System.Net.Http.HttpClient FrameClient = new() { Timeout = TimeSpan.FromMilliseconds(500) };
    private static readonly System.Net.Http.HttpClient ModeClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private const string FrameUrl = "http://127.0.0.1:18766/camera/frame";
    private const string ModeUrl = "http://127.0.0.1:18766/camera/mode";
    public const float DIVISOR = 20;

    private bool _disposedValue;
    private uint _width;
    private uint _height;
    private ulong _frameCount;
    private HANDLE _deviceHandle;
    private IComObject<ID3D11Texture2D>? _texture;
    private IComObject<ID2D1RenderTarget>? _renderTarget;
    private IComObject<ID2D1SolidColorBrush>? _whiteBrush;
    private IComObject<ID2D1SolidColorBrush>[]? _blockBrushes;
    private IComObject<IDWriteTextFormat>? _textFormat;
    private IComObject<IDWriteFactory>? _dwrite;
    private IComObject<IMFTransform>? _converter;
    private IComObject<IWICBitmap>? _bitmap;
    private IComObject<IMFDXGIDeviceManager>? _dxgiManager;
    private System.IO.MemoryMappedFiles.MemoryMappedFile? _mmf;
    private System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? _mmfAccessor;

    public bool HasD3DManager => _texture != null;
    public ulong FrameCount => _frameCount;

    private unsafe bool TryReadSharedMemory(nint targetPointer, uint targetLength)
    {
        try
        {
            if (_mmf == null)
            {
                _mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(Shared.SharedMemoryName, System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
                _mmfAccessor = _mmf.CreateViewAccessor(0, Shared.MaxBufferSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            }

            if (_mmfAccessor == null) return false;

            byte* ptr = null;
            _mmfAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            try
            {
                if (ptr == null) return false;
                var magic = *(uint*)(ptr + 0);
                if (magic != Shared.SharedMemoryMagic) return false;
                var w = *(uint*)(ptr + 4);
                var h = *(uint*)(ptr + 8);
                if (w != _width || h != _height) return false;
                var copyLen = Math.Min(targetLength, w * h * 4);
                Buffer.MemoryCopy(ptr + Shared.HeaderSize, (void*)targetPointer, targetLength, copyLen);
                return true;
            }
            finally
            {
                if (ptr != null) _mmfAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        catch
        {
            _mmfAccessor?.Dispose();
            _mmf?.Dispose();
            _mmfAccessor = null;
            _mmf = null;
            return false;
        }
    }

    private unsafe bool TryReadIntoBitmap()
    {
        if (_bitmap == null) return false;
        try
        {
            using var writeLock = _bitmap.Lock(WICBitmapLockFlags.WICBitmapLockWrite);
            writeLock.Object.GetStride(out var stride).ThrowOnError();
            writeLock.Object.GetDataPointer(out var length, out var pointer).ThrowOnError();
            if (stride == _width * 4 && length >= _width * _height * 4)
            {
                if (TryReadSharedMemory(pointer, length))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            var pixels = FrameClient.GetByteArrayAsync(FrameUrl).GetAwaiter().GetResult();
            if (pixels.Length == _width * _height * 4)
            {
                using var writeLock = _bitmap.Lock(WICBitmapLockFlags.WICBitmapLockWrite);
                writeLock.Object.GetStride(out var stride).ThrowOnError();
                writeLock.Object.GetDataPointer(out var length, out var pointer).ThrowOnError();
                if (stride == _width * 4 && length >= pixels.Length)
                {
                    Marshal.Copy(pixels, 0, pointer, pixels.Length);
                    return true;
                }
            }
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
        }

        return false;
    }

    public static (uint Width, uint Height) ReadOutputSize()
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(ModeClient.GetStringAsync(ModeUrl).GetAwaiter().GetResult());
            var width = document.RootElement.GetProperty("width").GetUInt32();
            var height = document.RootElement.GetProperty("height").GetUInt32();
            if (width is >= 160 and <= 1920 && height is >= 120 and <= 1080 && width % 2 == 0 && height % 2 == 0)
                return (width, height);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
        }
        return (640, 480);
    }

    // common to CPU & GPU
    private HRESULT CreateRenderTargetResources(uint width, uint height)
    {
        if (_renderTarget == null)
            return Constants.E_FAIL;

        _whiteBrush = _renderTarget.CreateSolidColorBrush(new D3DCOLORVALUE(1, 1, 1, 1));
        _dwrite = DWriteFunctions.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED);
        _textFormat = _dwrite.CreateTextFormat("Segoe UI", 40);
        _textFormat.Object.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        _textFormat.Object.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);

        // colors for HSL blocks
        var brushes = new List<IComObject<ID2D1SolidColorBrush>>();
        for (var i = 0; i < width / DIVISOR; i++)
        {
            for (var j = 0; j < height / DIVISOR; j++)
            {
                var color = LocalExtensions.HSL2RGB(i / (height / DIVISOR), 1, (j / (width / DIVISOR)));
                brushes.Add(_renderTarget.CreateSolidColorBrush(color));
            }
        }

        _blockBrushes = [.. brushes];
        _width = width;
        _height = height;
        return Constants.S_OK;
    }

    private void SetConverterTypes(uint width, uint height)
    {
        Functions.MFCreateMediaType(out var inputObj).ThrowOnError();
        using var inputType = new ComObject<IMFMediaType>(inputObj);
        inputType.Set(Constants.MF_MT_MAJOR_TYPE, Constants.MFMediaType_Video);
        inputType.Set(Constants.MF_MT_SUBTYPE, Constants.MFVideoFormat_RGB32);
        inputType.SetSize(Constants.MF_MT_FRAME_SIZE, width, height);
        _converter!.Object.SetInputType(0, inputType.Object, 0).ThrowOnError();

        Functions.MFCreateMediaType(out var outputObj).ThrowOnError();
        using var outputType = new ComObject<IMFMediaType>(inputObj);
        outputType.Set(Constants.MF_MT_MAJOR_TYPE, Constants.MFMediaType_Video);
        outputType.Set(Constants.MF_MT_SUBTYPE, Constants.MFVideoFormat_NV12);
        outputType.SetSize(Constants.MF_MT_FRAME_SIZE, width, height);
        _converter!.Object.SetOutputType(0, outputType.Object, 0).ThrowOnError();
    }

    public HRESULT SetD3DManager(nint manager, uint width, uint height)
    {
        if (manager == 0)
            return Constants.E_POINTER;

        if (width == 0 || height == 0)
            return Constants.E_INVALIDARG;

        _dxgiManager = DirectN.Extensions.Com.ComObject.FromPointer<IMFDXGIDeviceManager>(manager);
        if (_dxgiManager == null)
            return Constants.E_NOINTERFACE;

        _dxgiManager.Object.OpenDeviceHandle(out _deviceHandle).ThrowOnError();
        _dxgiManager.Object.GetVideoService(_deviceHandle, typeof(ID3D11Device).GUID, out var unk).ThrowOnError();

        // create a texture/surface to write
        using var device = DirectN.Extensions.Com.ComObject.FromPointer<ID3D11Device>(unk)!;
        _texture = device.CreateTexture2D(new D3D11_TEXTURE2D_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            Width = width,
            Height = height,
            ArraySize = 1,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET)
        });

        // create a D2D1 render target from 2D GPU surface
        var surface = _texture.As<IDXGISurface>()!;
        using var factory = D2D1Functions.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED);
        _renderTarget = factory.CreateDxgiSurfaceRenderTarget(surface, new D2D1_RENDER_TARGET_PROPERTIES { pixelFormat = new D2D1_PIXEL_FORMAT { alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED } });

        CreateRenderTargetResources(width, height).ThrowOnError();

        // create GPU RGB => NV12 converter
        _converter = DirectN.Extensions.Com.ComObject.CoCreate<IMFTransform>(Constants.CLSID_VideoProcessorMFT)!;
        SetConverterTypes(width, height);

        // make sure the video processor works on GPU
        DirectN.Extensions.Com.ComObject.WithComInstance(_dxgiManager, unk =>
        {
            _converter.Object.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)unk).ThrowOnError();
        });

        ComHosting.Trace($"w:{width} h:{height} OK");
        return Constants.S_OK;
    }

    public HRESULT EnsureRenderTarget(uint width, uint height)
    {
        try
        {
            if (!HasD3DManager)
            {
                // create a D2D1 render target from WIC bitmap
                using var factory = D2D1Functions.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED);
                _bitmap = WicImagingFactory.CreateBitmap(width, height, Constants.GUID_WICPixelFormat32bppPBGRA, WICBitmapCreateCacheOption.WICBitmapCacheOnDemand);
                _renderTarget = factory.CreateWicBitmapRenderTarget(_bitmap, new D2D1_RENDER_TARGET_PROPERTIES
                {
                    pixelFormat = new D2D1_PIXEL_FORMAT
                    {
                        alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
                        format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM
                    }
                });

                CreateRenderTargetResources(width, height).ThrowOnError();

                // create CPU RGB => NV12 converter
                _converter = DirectN.Extensions.Com.ComObject.CoCreate<IMFTransform>(Constants.CLSID_CColorConvertDMO);
                SetConverterTypes(width, height);
            }

            _frameCount = 0;
            return Constants.S_OK;
        }
        catch (Exception e)
        {
            ComHosting.Trace(e.ToString());
            throw;
        }
    }

    public IComObject<IMFSample> Generate(IComObject<IMFSample> sample, Guid format)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(sample);
            IComObject<IMFSample> outSample;

            if (!HasD3DManager)
            {
                var loaded = TryReadIntoBitmap();
                if (!loaded && _renderTarget != null && _textFormat != null && _dwrite != null && _whiteBrush != null)
                {
                    _renderTarget.BeginDraw();
                    _renderTarget.Clear(new D3DCOLORVALUE(0.06f, 0.08f, 0.11f, 1));
                    var text = $"Phonecam\nWaiting for phone camera connection...\n{_width} x {_height}";
                    using var layout = _dwrite.CreateTextLayout(_textFormat, text, text.Length, _width, _height);
                    _renderTarget.DrawTextLayout(new D2D_POINT_2F(0, 0), layout, _whiteBrush);
                    _renderTarget.EndDraw();
                }
            }
            else
            {
                sample.RemoveAllBuffers();

                DirectN.Extensions.Com.ComObject.WithComInstance(_texture, unk =>
                {
                    Functions.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, unk, 0, false, out var obj).ThrowOnError();
                    using var mediaBuffer = new ComObject<IMFMediaBuffer>(obj);
                    sample.Object.AddBuffer(mediaBuffer.Object).ThrowOnError();
                });

                if (format == Constants.MFVideoFormat_NV12)
                {
                    _converter!.Object.ProcessInput(0, sample.Object, 0).ThrowOnError();

                    var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
                    _converter.Object.ProcessOutput(0, (uint)buffers.Length, buffers, out var status).ThrowOnError();
                    outSample = DirectN.Extensions.Com.ComObject.FromPointer<IMFSample>(buffers[0].pSample)!;
                }
                else
                {
                    outSample = sample;
                }

                _frameCount++;
                return outSample;
            }
            // lock WIC bitmap to write to sample
            using var locked = _bitmap!.Lock(WICBitmapLockFlags.WICBitmapLockRead);
            locked.Object.GetSize(out var w, out var h).ThrowOnError();
            locked.Object.GetStride(out var wicStride).ThrowOnError();
            locked.Object.GetDataPointer(out var wicSize, out var wicPointer).ThrowOnError();

            // if we're on CPU & format is NOT RGB, convert using CPU (CColorConvertDMO)
            if (format == Constants.MFVideoFormat_NV12)
            {
                // create temp RGB sample for WIC bitmap
                Functions.MFCreateSample(out var obj).ThrowOnError();
                using var wicSample = new ComObject<IMFSample>(obj);
                Functions.MFCreateMemoryBuffer(wicSize, out var bufferObj);
                using var wicBuffer = new ComObject<IMFMediaBuffer>(bufferObj);
                wicSample.AddBuffer(wicBuffer);
                wicBuffer.WithLock((scanline, length, _) => wicPointer.CopyTo(scanline, length));
                wicBuffer.SetCurrentLength(wicSize);

                _converter!.Object.ProcessInput(0, wicSample.Object, 0).ThrowOnError();

                // convert RGB sample to NV12 sample
                DirectN.Extensions.Com.ComObject.WithComInstance(sample, outSamplePtr =>
                {
                    var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
                    buffers[0].pSample = outSamplePtr;
                    _converter.Object.ProcessOutput(0, 1, buffers, out var status).ThrowOnError();
                });
            }
            else
            {
                // sample is already for RGB
                using var buffer = sample.GetBufferByIndex(0);
                buffer.WithLock((scanline, length, _) => wicPointer.CopyTo(scanline, length));
                ComHosting.Trace("format: " + format + " max: " + buffer.GetMaxLength() + " wicSize:" + wicSize);
                buffer.SetCurrentLength(wicSize);
            }

            _frameCount++;
            return sample;
        }
        catch (Exception e)
        {
            ComHosting.Trace(e.ToString());
            throw;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        ComHosting.Trace();
        try
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // dispose managed state (managed objects)
                    var dxgiManager = _dxgiManager;
                    if (_deviceHandle != 0 && dxgiManager != null)
                    {
                        dxgiManager.Object.CloseDeviceHandle(_deviceHandle);
                    }

                    _whiteBrush.SafeDispose();
                    _blockBrushes.Dispose();
                    _bitmap.SafeDispose();
                    _texture.SafeDispose();
                    _textFormat.SafeDispose();
                    _dwrite.SafeDispose();
                    _renderTarget.SafeDispose();
                    _converter.SafeDispose();
                    _mmfAccessor.SafeDispose();
                    _mmf.SafeDispose();
                }

                // free unmanaged resources (unmanaged objects) and override finalizer
                // set large fields to null
                _disposedValue = true;
                ComHosting.Trace("Disposed");
            }
        }
        catch (Exception e)
        {
            ComHosting.Trace(e.ToString());
            throw;
        }
    }

    ~FrameGenerator() { Dispose(disposing: false); }
    public void Dispose() { Dispose(disposing: true); GC.SuppressFinalize(this); }
}
