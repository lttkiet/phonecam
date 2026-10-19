namespace PhonecamCameraHost;

[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.22621.0")]
internal static class Program
{
    private static int Main()
    {
        ComObject<IMFVirtualCamera>? camera = null;
        try
        {
            Functions.MFStartup(Constants.MF_VERSION, 0).ThrowOnError();
            using var name = new Pwstr("Phonecam");
            using var sourceId = new Pwstr($"{{{Shared.CLSID_VCamNetAOT}}}");
            Functions.MFCreateVirtualCamera(
                MFVirtualCameraType.MFVirtualCameraType_SoftwareCameraSource,
                MFVirtualCameraLifetime.MFVirtualCameraLifetime_Session,
                MFVirtualCameraAccess.MFVirtualCameraAccess_CurrentUser,
                name,
                sourceId,
                0,
                0,
                out var nativeCamera).ThrowOnError();
            camera = new ComObject<IMFVirtualCamera>(nativeCamera);
            camera.Object.Start(null).ThrowOnError();
            Console.WriteLine("READY");
            Console.Out.Flush();
            while (Console.ReadLine() is not null) { }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            if (camera is not null)
            {
                try { camera.Object.Stop(); } catch { }
                try { camera.Object.Remove(); } catch { }
                camera.Dispose();
            }
            try { Functions.MFShutdown().ThrowOnError(); } catch { }
        }
    }
}
