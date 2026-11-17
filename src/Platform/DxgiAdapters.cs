using System;
using System.Runtime.InteropServices;

namespace Forja.Platform
{
    // Finds the NVIDIA adapter's LUID through DXGI adapter enumeration, so the "GPU Engine" and
    // "GPU Process Memory" counters (named by LUID) can be matched to the NVIDIA GPU. Enumeration
    // creates no D3D device and loads no user-mode driver; it reads adapter descriptions through
    // dxgkrnl, which may or may not reach the kernel-mode driver (not proven harmless for a dGPU
    // in D3cold: hardware checklist). Never throws.
    public static class DxgiAdapters
    {
        public const uint VendorNvidia = 0x10DE;
        private const uint FlagSoftware = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        // Only EnumAdapters1 is called; the other slots just keep the vtable order right.
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            [PreserveSig] int SetPrivateData(IntPtr a, uint b, IntPtr c);
            [PreserveSig] int SetPrivateDataInterface(IntPtr a, IntPtr b);
            [PreserveSig] int GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int GetParent(IntPtr a, out IntPtr b);
            [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
            [PreserveSig] int MakeWindowAssociation(IntPtr hwnd, uint flags);
            [PreserveSig] int GetWindowAssociation(out IntPtr hwnd);
            [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
            [PreserveSig] int IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            [PreserveSig] int SetPrivateData(IntPtr a, uint b, IntPtr c);
            [PreserveSig] int SetPrivateDataInterface(IntPtr a, IntPtr b);
            [PreserveSig] int GetPrivateData(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int GetParent(IntPtr a, out IntPtr b);
            [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
            [PreserveSig] int GetDesc(IntPtr desc);
            [PreserveSig] int CheckInterfaceSupport(IntPtr guid, IntPtr umd);
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        private static readonly object CacheLock = new object();
        private static bool cached;
        private static string cachedKey;
        private static string cachedName;

        // The NVIDIA adapter's LUID key (GpuEngineMath.LuidKey) and name, enumerated ONCE per process
        // and cached, "not found" included (probes are recreated on every page open; the LUID cannot
        // change inside a boot unless the device is disabled or the driver updated, which needs a
        // restart of the app to be noticed). UNPROVEN: whether the enumeration itself can wake a
        // dGPU in D3cold; it is on the hardware checklist, and it runs only when nvidia-smi exists.
        public static bool CachedNvidia(out string luidKey, out string name)
        {
            lock (CacheLock)
            {
                if (!cached)
                {
                    cached = true;
                    uint high, low;
                    string n;
                    if (FindNvidia(out high, out low, out n))
                    {
                        cachedKey = Forja.Core.GpuEngineMath.LuidKey(high, low);
                        cachedName = n;
                    }
                }
                luidKey = cachedKey;
                name = cachedName;
                return cachedKey != null;
            }
        }

        // True when a hardware NVIDIA adapter exists; luidHigh/luidLow match the counter instance names.
        public static bool FindNvidia(out uint luidHigh, out uint luidLow, out string name)
        {
            luidHigh = 0;
            luidLow = 0;
            name = null;
            IntPtr raw = IntPtr.Zero;
            object factoryObj = null;
            try
            {
                Guid iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                if (CreateDXGIFactory1(ref iid, out raw) < 0 || raw == IntPtr.Zero) return false;
                factoryObj = Marshal.GetObjectForIUnknown(raw);
                IDXGIFactory1 factory = (IDXGIFactory1)factoryObj;
                for (uint i = 0; i < 16; i++)
                {
                    IDXGIAdapter1 adapter;
                    if (factory.EnumAdapters1(i, out adapter) < 0 || adapter == null) break;
                    try
                    {
                        DXGI_ADAPTER_DESC1 d;
                        if (adapter.GetDesc1(out d) < 0) continue;
                        if (d.VendorId != VendorNvidia || (d.Flags & FlagSoftware) != 0) continue;
                        luidHigh = unchecked((uint)d.LuidHigh);
                        luidLow = d.LuidLow;
                        name = d.Description;
                        return true;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (factoryObj != null) Marshal.ReleaseComObject(factoryObj);
                if (raw != IntPtr.Zero) Marshal.Release(raw);
            }
        }
    }
}
