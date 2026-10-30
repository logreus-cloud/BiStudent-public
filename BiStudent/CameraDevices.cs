using OpenCvSharp;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace BiStudent;

internal sealed record CameraDevice(int Index, string Name, string Id)
{
    public override string ToString() => Name;
}

internal static class CameraDevices
{
    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(ref Guid type, out IEnumMoniker enumerator, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
    }

    public static List<CameraDevice> List()
    {
        object? deviceEnumerator = null;
        IEnumMoniker? enumerator = null;
        try
        {
            deviceEnumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"))!);
            var category = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
            if (((ICreateDevEnum)deviceEnumerator!).CreateClassEnumerator(ref category, out enumerator!, 0) != 0 || enumerator is null)
                return [];

            var devices = new List<CameraDevice>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    string? friendlyName = null;
                    string? devicePath = null;
                    object? storage = null;
                    try
                    {
                        var propertyBag = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");
                        moniker.BindToStorage(null!, null!, ref propertyBag, out storage!);
                        if (((IPropertyBag)storage).Read("FriendlyName", out var value, IntPtr.Zero) == 0)
                            friendlyName = value as string;
                        if (((IPropertyBag)storage).Read("DevicePath", out var path, IntPtr.Zero) == 0)
                            devicePath = path as string;
                    }
                    catch { }
                    finally { if (storage is not null) Marshal.ReleaseComObject(storage); }

                    var index = devices.Count;
                    var name = string.IsNullOrWhiteSpace(friendlyName) ? $"Камера {index + 1}" : friendlyName;
                    var idName = friendlyName ?? name;
                    var ordinal = occurrences.GetValueOrDefault(idName) + 1;
                    occurrences[idName] = ordinal;
                    var uniqueName = name;
                    for (var suffix = 2; !names.Add(uniqueName); suffix++)
                        uniqueName = $"{name} ({suffix})";
                    devices.Add(new CameraDevice(index, uniqueName, string.IsNullOrWhiteSpace(devicePath) ? $"{idName}#{ordinal}" : devicePath));
                }
                finally { Marshal.ReleaseComObject(moniker); }
            }
            return devices;
        }
        catch { return []; }
        finally
        {
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            if (deviceEnumerator is not null) Marshal.ReleaseComObject(deviceEnumerator);
        }
    }

    public static VideoCapture Open(int index, int width, int height, bool allowFallback)
    {
        var capture = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
        if (!capture.IsOpened() && allowFallback)
        {
            capture.Dispose();
            capture = new VideoCapture(index);
        }
        if (!capture.IsOpened())
        {
            capture.Dispose();
            throw new InvalidOperationException("Камера не найдена или занята другой программой.");
        }
        try
        {
            capture.FrameWidth = width;
            capture.FrameHeight = height;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
        return capture;
    }

    /// Виртуальная камера Iriun отдаёт видео только при запущенном на этом компьютере приложении Iriun Webcam.
    public static string? IriunHint(CameraDevice device)
    {
        if (!device.Name.Contains("Iriun", StringComparison.OrdinalIgnoreCase)) return null;
        var processes = System.Diagnostics.Process.GetProcessesByName("IriunWebcam");
        var running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        return running ? null : "Iriun Webcam не запущен — запустите его на этом компьютере и подключите телефон";
    }

    public static CameraDevice? Find(IReadOnlyList<CameraDevice> devices, string id)
    {
        foreach (var device in devices)
            if (device.Id == id) return device;
        return null;
    }

    public static int ResolveIndex(ClientSettings settings, IReadOnlyList<CameraDevice> devices)
    {
        var id = settings.CameraId;
        if (id is not null && Find(devices, id) is { } selected) return selected.Index;
        var name = settings.CameraName;
        if (name is not null)
        {
            foreach (var device in devices)
                if (device.Name == name) return device.Index;
        }
        if (settings.CameraIndex is { } index
            && index >= 0 && (devices.Count == 0 || index < devices.Count)) return index;
        return 0;
    }

    public static void Save(ClientSettings settings, CameraDevice device)
    {
        settings.CameraId = device.Id;
        settings.CameraName = device.Name;
        settings.CameraIndex = device.Index;
        settings.Save();
    }
}
