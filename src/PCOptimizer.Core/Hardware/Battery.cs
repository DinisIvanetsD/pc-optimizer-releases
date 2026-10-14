using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Hardware;

/// <summary>Uma bateria: capacidades em mWh (como o Windows as dá), ciclos (0 = o portátil não diz) e o estado agora.</summary>
public sealed record BatteryInfo(string Name, string Maker, long DesignedMWh, long FullMWh, int Cycles, long RemainingMWh, bool OnAc, bool Charging);

/// <summary>
/// Saúde da bateria pelo driver de bateria do Windows (os mesmos dados do "powercfg /batteryreport").
/// Só lê e não precisa de administrador.
/// </summary>
public static class Battery
{
    /// <summary>Abaixo disto a bateria já dura bem menos do que nova.</summary>
    public const int WornPercent = 80;
    /// <summary>Abaixo disto vale a pena pensar em trocá-la.</summary>
    public const int ReplacePercent = 60;

    /// <summary>Capacidade que resta em relação à de fábrica (100 = como nova). Null se o portátil não diz.</summary>
    public static int? HealthPercent(long designed, long full) =>
        designed > 0 && full > 0 ? (int)Math.Round(100.0 * full / designed) : null;

    public static string? Warning(BatteryInfo b) => HealthPercent(b.DesignedMWh, b.FullMWh) switch
    {
        int h when h < ReplacePercent => Loc.T($"A bateria só guarda {h}% do que guardava nova. Já dura bem menos: se te faz falta longe da tomada, vale a pena trocá-la."),
        int h when h < WornPercent => Loc.T($"A bateria guarda {h}% do que guardava nova. É desgaste normal com o uso, mas já se nota na duração."),
        _ => null,
    };

    /// <summary>Texto curto da saúde, ex.: "100% (como nova)".</summary>
    public static string HealthText(int h) => h switch
    {
        >= 95 => Loc.T($"{Math.Min(h, 100)}% (como nova)"),
        >= WornPercent => Loc.T($"{h}% (bom)"),
        >= ReplacePercent => Loc.T($"{h}% (gasta)"),
        _ => Loc.T($"{h}% (muito gasta)"),
    };

    public static List<BatteryInfo> List()
    {
        var list = new List<BatteryInfo>();
        try
        {
            foreach (var path in DevicePaths())
            {
                try { if (Read(path) is { } b) list.Add(b); }
                catch { } // uma bateria que não responde não impede as outras
            }
        }
        catch { }
        return list;
    }

    static BatteryInfo? Read(string path)
    {
        using var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h.IsInvalid) return null;

        uint wait = 0, tag;
        if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, ref wait, 4, out tag, 4, out _, IntPtr.Zero) || tag == 0) return null;

        var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = BatteryInformation };
        if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(),
                out BATTERY_INFORMATION info, Marshal.SizeOf<BATTERY_INFORMATION>(), out _, IntPtr.Zero)) return null;

        var ws = new BATTERY_WAIT_STATUS { BatteryTag = tag };
        var hasStatus = DeviceIoControl(h, IOCTL_BATTERY_QUERY_STATUS, ref ws, Marshal.SizeOf<BATTERY_WAIT_STATUS>(),
            out BATTERY_STATUS st, Marshal.SizeOf<BATTERY_STATUS>(), out _, IntPtr.Zero);

        return new BatteryInfo(
            QueryString(h, tag, BatteryDeviceName) ?? Loc.T("Bateria"),
            QueryString(h, tag, BatteryManufactureName) ?? "",
            info.DesignedCapacity, info.FullChargedCapacity, (int)info.CycleCount,
            hasStatus && st.Capacity != BATTERY_UNKNOWN_CAPACITY ? st.Capacity : 0,
            hasStatus && (st.PowerState & BATTERY_POWER_ON_LINE) != 0,
            hasStatus && (st.PowerState & BATTERY_CHARGING) != 0);
    }

    static string? QueryString(SafeFileHandle h, uint tag, int level)
    {
        var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = level };
        var buf = Marshal.AllocHGlobal(512);
        try
        {
            if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(), buf, 512, out var n, IntPtr.Zero) || n < 2)
                return null;
            var s = Marshal.PtrToStringUni(buf, n / 2).TrimEnd('\0').Trim();
            return s.Length > 0 ? s : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static IEnumerable<string> DevicePaths()
    {
        var guid = GUID_DEVCLASS_BATTERY;
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        var paths = new List<string>();
        try
        {
            for (uint i = 0; i < 8; i++)
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    // cbSize da estrutura SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 em 64 bits, 6 em 32 bits
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref did, detail, size, out _, IntPtr.Zero))
                        paths.Add(Marshal.PtrToStringUni(detail + 4)!);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    // --- Win32 (batclass.h / setupapi.h) ---

    static Guid GUID_DEVCLASS_BATTERY = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_ATTRIBUTE_NORMAL = 0x80;
    const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_INFORMATION = 0x294044, IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
    const int BatteryInformation = 0, BatteryDeviceName = 4, BatteryManufactureName = 6;
    const uint BATTERY_POWER_ON_LINE = 0x1, BATTERY_CHARGING = 0x4, BATTERY_UNKNOWN_CAPACITY = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_QUERY_INFORMATION { public uint BatteryTag; public int InformationLevel; public int AtRate; }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_INFORMATION
    {
        public uint Capabilities; public byte Technology; public byte R1, R2, R3; public uint Chemistry;
        public uint DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_WAIT_STATUS { public uint BatteryTag, Timeout, PowerState, LowCapacity, HighCapacity; }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_STATUS { public uint PowerState, Capacity, Voltage; public int Rate; }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid cls, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid cls, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint inBuf, int inSize, out uint outBuf, int outSize, out int returned, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_QUERY_INFORMATION inBuf, int inSize, out BATTERY_INFORMATION outBuf, int outSize, out int returned, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_QUERY_INFORMATION inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_WAIT_STATUS inBuf, int inSize, out BATTERY_STATUS outBuf, int outSize, out int returned, IntPtr ov);
}
