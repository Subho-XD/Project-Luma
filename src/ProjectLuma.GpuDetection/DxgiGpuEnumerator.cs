using System.Runtime.InteropServices;

namespace ProjectLuma.GpuDetection;

/// <summary>Native DXGI adapter descriptor reader avoiding WMI 32-bit 4 GB VRAM overflow.</summary>
public static class DxgiGpuEnumerator
{
    public sealed record DxgiAdapterRecord(
        string Description,
        uint VendorId,
        uint DeviceId,
        uint SubSysId,
        uint Revision,
        ulong DedicatedVideoMemoryBytes,
        ulong SharedSystemMemoryBytes,
        uint Flags);

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    [DllImport("dxgi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr ppFactory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr factory, uint adapterIndex, out IntPtr ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr adapter, out DXGI_ADAPTER_DESC1 desc);

    public static IReadOnlyList<DxgiAdapterRecord> EnumerateHardwareAdapters()
    {
        var results = new List<DxgiAdapterRecord>();
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            int hr = CreateDXGIFactory1(in IID_IDXGIFactory1, out factoryPtr);
            if (hr < 0 || factoryPtr == IntPtr.Zero) return results;

            IntPtr factoryVtbl = Marshal.ReadIntPtr(factoryPtr);
            // IDXGIFactory1::EnumAdapters1 is vtable slot 12 (IUnknown 3 + IDXGIObject 4 + IDXGIFactory 5 = 12)
            IntPtr enumAdapters1Ptr = Marshal.ReadIntPtr(factoryVtbl, 12 * IntPtr.Size);
            var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(enumAdapters1Ptr);

            for (uint index = 0; ; index++)
            {
                int enumHr = enumAdapters1(factoryPtr, index, out IntPtr adapterPtr);
                if (enumHr == DXGI_ERROR_NOT_FOUND || enumHr < 0 || adapterPtr == IntPtr.Zero) break;

                try
                {
                    IntPtr adapterVtbl = Marshal.ReadIntPtr(adapterPtr);
                    // IDXGIAdapter1::GetDesc1 is vtable slot 10 (IUnknown 3 + IDXGIObject 4 + IDXGIAdapter 3 = 10)
                    IntPtr getDesc1Ptr = Marshal.ReadIntPtr(adapterVtbl, 10 * IntPtr.Size);
                    var getDesc1 = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(getDesc1Ptr);

                    if (getDesc1(adapterPtr, out DXGI_ADAPTER_DESC1 desc) >= 0)
                    {
                        if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
                        results.Add(new DxgiAdapterRecord(
                            desc.Description?.Trim() ?? "Unknown Adapter",
                            desc.VendorId,
                            desc.DeviceId,
                            desc.SubSysId,
                            desc.Revision,
                            desc.DedicatedVideoMemory,
                            desc.SharedSystemMemory,
                            desc.Flags));
                    }
                }
                finally
                {
                    Marshal.Release(adapterPtr);
                }
            }
        }
        catch
        {
            // Fallback to WMI/Registry if DXGI cannot initialize
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
        }

        return results;
    }
}
