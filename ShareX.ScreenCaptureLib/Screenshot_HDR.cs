// File: ShareX.ScreenCaptureLib/Screenshot_HDR.cs
//
// HDR screenshot capture using DXGI Desktop Duplication.
// Uses [ComImport] interface declarations for DXGI interfaces (safe vtable
// dispatch via CLR) and raw vtable calls only for the few D3D11 methods
// needed (ID3D11Device has ~43 methods, making full [ComImport] brittle).
//
// Multi-monitor: DXGI Desktop Duplication is per-output. ShareX capture rects
// use virtual desktop coordinates that can span multiple monitors. This code
// enumerates all DXGI outputs, captures each monitor that intersects the
// requested rect, and composites the results onto a single bitmap.
//
// Capture sequence per output:
//   1. D3D11CreateDevice(null, HARDWARE)
//   2. QI to IDXGIDevice -> GetAdapter()
//   3. adapter.EnumOutputs(i) for each intersecting output
//   4. QI to IDXGIOutput5 -> DuplicateOutput1 with 3 format fallbacks
//   5. AcquireNextFrame (with retry loop for DWM warm-up) -> copy -> map -> convert

#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib
{
    public partial class Screenshot
    {
        public bool CaptureHDREnabled { get; set; } = false;

        // ====================================================================
        // DisplayConfig API for SDR White Level
        // ====================================================================

        #region DisplayConfig P/Invoke

        private const int QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)]
            public bool targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] modeInfoData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint SDRWhiteLevel;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(int flags, out int numPaths, out int numModes);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(int flags, ref int numPaths,
            [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref int numModes,
            [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL info);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);

        #endregion

        // ====================================================================
        // COM Interface Declarations (proper [ComImport])
        // ====================================================================
        // The CLR uses these to build correct vtables automatically.
        // Method order must exactly match the IDL/header order.
        // We use [ComImport] for all DXGI interfaces (where correct vtable
        // dispatch is critical) and raw vtable calls for the few D3D11
        // methods we need (ID3D11Device has ~43 methods, making full
        // [ComImport] declaration impractical and error-prone).

        #region COM Interfaces

        // --- DXGI Interfaces ---

        [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIDevice
        {
            // IDXGIObject methods (4 methods: SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIDevice methods
            int GetAdapter(out IDXGIAdapter adapter);
        }

        [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter
        {
            // IDXGIObject methods
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIAdapter methods
            int EnumOutputs(uint index, out IDXGIOutput output);
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput
        {
            // IDXGIObject (4 methods)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIOutput methods (12 methods)
            int GetDesc(out DXGI_OUTPUT_DESC desc);
            int GetDisplayModeList(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int WaitForVBlank();
            int TakeOwnership([MarshalAs(UnmanagedType.IUnknown)] object device, [MarshalAs(UnmanagedType.Bool)] bool exclusive);
            void ReleaseOwnership();
            int GetGammaControlCapabilities(IntPtr gammaCaps);
            int SetGammaControl(IntPtr gamma);
            int GetGammaControl(IntPtr gamma);
            int SetDisplaySurface([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetDisplaySurfaceData([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetFrameStatistics(IntPtr stats);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public RECT DesktopCoordinates;
            [MarshalAs(UnmanagedType.Bool)]
            public bool AttachedToDesktop;
            public uint Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput1
        {
            // IDXGIObject (4)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIOutput (12 methods)
            int GetDesc(out DXGI_OUTPUT_DESC desc);
            int GetDisplayModeList(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int WaitForVBlank();
            int TakeOwnership([MarshalAs(UnmanagedType.IUnknown)] object device, [MarshalAs(UnmanagedType.Bool)] bool exclusive);
            void ReleaseOwnership();
            int GetGammaControlCapabilities(IntPtr gammaCaps);
            int SetGammaControl(IntPtr gamma);
            int GetGammaControl(IntPtr gamma);
            int SetDisplaySurface([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetDisplaySurfaceData([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetFrameStatistics(IntPtr stats);

            // IDXGIOutput1 (4 methods)
            int GetDisplayModeList1(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode1(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int GetDisplaySurfaceData1([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out IDXGIOutputDuplication duplication);
        }

        // IDXGIOutput5 inherits: IDXGIOutput4 -> IDXGIOutput3 -> IDXGIOutput2 -> IDXGIOutput1
        // IDXGIOutput2 adds: SupportsOverlays (1 method)
        // IDXGIOutput3 adds: CheckOverlaySupport (1 method)
        // IDXGIOutput4 adds: CheckOverlayColorSpaceSupport (1 method)
        // IDXGIOutput5 adds: DuplicateOutput1 (1 method)
        [ComImport, Guid("80A07424-AB52-42EB-833C-0C42FD282D98"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput5
        {
            // IDXGIObject (4)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIOutput (12)
            int GetDesc(out DXGI_OUTPUT_DESC desc);
            int GetDisplayModeList(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int WaitForVBlank();
            int TakeOwnership([MarshalAs(UnmanagedType.IUnknown)] object device, [MarshalAs(UnmanagedType.Bool)] bool exclusive);
            void ReleaseOwnership();
            int GetGammaControlCapabilities(IntPtr gammaCaps);
            int SetGammaControl(IntPtr gamma);
            int GetGammaControl(IntPtr gamma);
            int SetDisplaySurface([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetDisplaySurfaceData([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetFrameStatistics(IntPtr stats);

            // IDXGIOutput1 (4)
            int GetDisplayModeList1(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode1(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int GetDisplaySurfaceData1([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out IDXGIOutputDuplication duplication);

            // IDXGIOutput2 (1)
            [PreserveSig] int SupportsOverlays();

            // IDXGIOutput3 (1)
            int CheckOverlaySupport(uint format, [MarshalAs(UnmanagedType.IUnknown)] object device, out uint flags);

            // IDXGIOutput4 (1)
            int CheckOverlayColorSpaceSupport(uint format, uint colorSpace, [MarshalAs(UnmanagedType.IUnknown)] object device, out uint flags);

            // IDXGIOutput5 (1)
            int DuplicateOutput1([MarshalAs(UnmanagedType.IUnknown)] object device, uint flags,
                uint formatCount, [MarshalAs(UnmanagedType.LPArray)] int[] formats,
                out IDXGIOutputDuplication duplication);
        }

        [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutputDuplication
        {
            // IDXGIObject (4)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIOutputDuplication methods
            void GetDesc(out DXGI_OUTDUPL_DESC desc);

            [PreserveSig]
            int AcquireNextFrame(uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO frameInfo,
                [MarshalAs(UnmanagedType.IUnknown)] out object resource);

            int GetFrameDirtyRects(uint dirtyRectsBufferSize, IntPtr dirtyRectsBuffer, out uint dirtyRectsBufferSizeRequired);
            int GetFrameMoveRects(uint moveRectsBufferSize, IntPtr moveRectsBuffer, out uint moveRectsBufferSizeRequired);
            int GetFramePointerShape(uint pointerShapeBufferSize, IntPtr pointerShapeBuffer,
                out uint pointerShapeBufferSizeRequired, IntPtr pointerShapeInfo);
            int MapDesktopSurface(out DXGI_MAPPED_RECT mappedRect);
            int UnMapDesktopSurface();

            [PreserveSig]
            int ReleaseFrame();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_DESC
        {
            public DXGI_MODE_DESC ModeDesc;
            public uint Rotation;
            [MarshalAs(UnmanagedType.Bool)]
            public bool DesktopImageInSystemMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_MODE_DESC
        {
            public uint Width, Height;
            public DXGI_RATIONAL RefreshRate;
            public uint Format;
            public uint ScanlineOrdering;
            public uint Scaling;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_RATIONAL { public uint Numerator, Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            [MarshalAs(UnmanagedType.Bool)]
            public bool RectsCoalesced;
            [MarshalAs(UnmanagedType.Bool)]
            public bool ProtectedContentMaskedOut;
            public int PointerPositionX;
            public int PointerPositionY;
            [MarshalAs(UnmanagedType.Bool)]
            public bool PointerPositionVisible;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_MAPPED_RECT
        {
            public int Pitch;
            public IntPtr pBits;
        }

        #endregion

        // ====================================================================
        // D3D11 Types and Imports (raw vtable for the few calls we need)
        // ====================================================================

        #region D3D11

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(
            IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
            int[] pFeatureLevels, uint FeatureLevels, uint SDKVersion,
            out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const uint D3D11_SDK_VERSION = 7;

        private const int DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
        private const int DXGI_FORMAT_R10G10B10A2_UNORM = 24;
        private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;

        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        private const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
        private const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);

        private const int D3D11_USAGE_STAGING = 3;
        private const uint D3D11_CPU_ACCESS_READ = 0x20000;
        private const int D3D11_MAP_READ = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize;
            public int Format;
            public uint SampleCount, SampleQuality;
            public int Usage;
            public uint BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_MAPPED_SUBRESOURCE
        {
            public IntPtr pData;
            public uint RowPitch, DepthPitch;
        }

        // ID3D11Texture2D::GetDesc -- vtable slot 10
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Del_GetTexDesc(IntPtr self, out D3D11_TEXTURE2D_DESC desc);

        // ID3D11Device::CreateTexture2D -- vtable slot 5
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Del_CreateTex2D(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr init, out IntPtr tex);

        // ID3D11DeviceContext::Map -- vtable slot 14
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Del_Map(IntPtr self, IntPtr res, uint sub, int type, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped);

        // ID3D11DeviceContext::Unmap -- vtable slot 15
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Del_Unmap(IntPtr self, IntPtr res, uint sub);

        // ID3D11DeviceContext::CopyResource -- vtable slot 47
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Del_CopyResource(IntPtr self, IntPtr dst, IntPtr src);

        // IDXGIOutputDuplication::AcquireNextFrame -- vtable slot 8
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Del_AcquireNextFrame(IntPtr self, uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO frameInfo, out IntPtr resource);

        // IDXGIOutputDuplication::ReleaseFrame -- vtable slot 14
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Del_ReleaseFrame(IntPtr self);

        private static IntPtr VT(IntPtr obj, int slot) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);

        // ----------------------------------------------------------------
        // Additional D3D11 constants for the compute shader pipeline
        // ----------------------------------------------------------------

        private const int  DXGI_FORMAT_R8G8B8A8_UNORM   = 28;
        private const int  D3D11_USAGE_DEFAULT           = 0;
        private const int  D3D11_USAGE_DYNAMIC           = 2;
        private const int  D3D11_BIND_SHADER_RESOURCE    = 0x8;
        private const int  D3D11_BIND_UNORDERED_ACCESS   = 0x80;
        private const int  D3D11_BIND_CONSTANT_BUFFER    = 0x4;
        private const int  D3D11_MAP_WRITE_DISCARD       = 4;
        private const uint D3D11_CPU_ACCESS_WRITE        = 0x10000;
        private const int  D3D11_SRV_DIMENSION_TEXTURE2D = 4;
        private const int  D3D11_UAV_DIMENSION_TEXTURE2D = 4;
        private const uint HDR_FORMAT_SCRGB              = 0;  // RGBA16F scRGB path
        private const uint HDR_FORMAT_HDR10              = 1;  // R10G10B10A2 PQ path

        // ----------------------------------------------------------------
        // D3D11_BUFFER_DESC  (24 bytes)
        // ----------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_BUFFER_DESC
        {
            public uint ByteWidth;
            public int  Usage;              // D3D11_USAGE
            public uint BindFlags;
            public uint CPUAccessFlags;
            public uint MiscFlags;
            public uint StructureByteStride;
        }

        // ----------------------------------------------------------------
        // D3D11_SHADER_RESOURCE_VIEW_DESC  for Texture2D  (24 bytes)
        // Layout: Format(4) + ViewDimension(4) + union(16) where Texture2D
        // occupies the first 8 bytes of the union.
        // ----------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_SRV_DESC_TEX2D
        {
            public int  Format;
            public int  ViewDimension;
            public uint MostDetailedMip;
            public uint MipLevels;
            public uint _unionPad0;     // union padding (union is 16 bytes)
            public uint _unionPad1;
        }

        // ----------------------------------------------------------------
        // D3D11_UNORDERED_ACCESS_VIEW_DESC  for Texture2D  (20 bytes)
        // Layout: Format(4) + ViewDimension(4) + union(12) where Texture2D
        // occupies the first 4 bytes of the union.
        // ----------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_UAV_DESC_TEX2D
        {
            public int  Format;
            public int  ViewDimension;
            public uint MipSlice;
            public uint _unionPad0;     // union padding (union is 12 bytes)
            public uint _unionPad1;
        }

        // ----------------------------------------------------------------
        // Constant buffer layout for HDRTonemap.cso  (48 bytes = 3 CB registers)
        // Must match the cbuffer Params in Screenshot_HDR_Shader.hlsl exactly.
        // ----------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct HdrTonemapCB
        {
            // Register 0
            public float NormScale;
            public uint  InputFormat;
            public uint  SrcOffsetX;
            public uint  SrcOffsetY;
            // Register 1
            public uint  CopyWidth;
            public uint  CopyHeight;
            public uint  TexRotation;
            public uint  TexWidth;
            // Register 2
            public uint  TexHeight;
            public uint  Pad0;
            public uint  Pad1;
            public uint  Pad2;
        }

        // ----------------------------------------------------------------
        // Compute shader vtable delegates
        // ----------------------------------------------------------------

        // ID3D11Device::CreateBuffer -- vtable slot 3
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Del_CreateBuffer(IntPtr self, ref D3D11_BUFFER_DESC desc,
            IntPtr pInitialData, out IntPtr ppBuffer);

        // ID3D11Device::CreateShaderResourceView -- vtable slot 7
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Del_CreateSRV(IntPtr self, IntPtr pResource,
            ref D3D11_SRV_DESC_TEX2D pDesc, out IntPtr ppSRView);

        // ID3D11Device::CreateUnorderedAccessView -- vtable slot 8
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Del_CreateUAV(IntPtr self, IntPtr pResource,
            ref D3D11_UAV_DESC_TEX2D pDesc, out IntPtr ppUAView);

        // ID3D11Device::CreateComputeShader -- vtable slot 18
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private unsafe delegate int Del_CreateComputeShader(IntPtr self, void* pShaderBytecode,
            UIntPtr BytecodeLength, IntPtr pClassLinkage, out IntPtr ppComputeShader);

        // ID3D11DeviceContext::Dispatch -- vtable slot 41
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Del_Dispatch(IntPtr self,
            uint ThreadGroupCountX, uint ThreadGroupCountY, uint ThreadGroupCountZ);

        // ID3D11DeviceContext::CSSetShaderResources -- vtable slot 67
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private unsafe delegate void Del_CSSetSRVs(IntPtr self, uint StartSlot, uint NumViews,
            IntPtr* ppShaderResourceViews);

        // ID3D11DeviceContext::CSSetUnorderedAccessViews -- vtable slot 68
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private unsafe delegate void Del_CSSetUAVs(IntPtr self, uint StartSlot, uint NumUAVs,
            IntPtr* ppUnorderedAccessViews, uint* pUAVInitialCounts);

        // ID3D11DeviceContext::CSSetShader -- vtable slot 69
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Del_CSSetShader(IntPtr self, IntPtr pComputeShader,
            IntPtr ppClassInstances, uint NumClassInstances);

        // ID3D11DeviceContext::CSSetConstantBuffers -- vtable slot 71
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private unsafe delegate void Del_CSSetCBs(IntPtr self, uint StartSlot, uint NumBuffers,
            IntPtr* ppConstantBuffers);

        #endregion

        // ====================================================================
        // Main HDR Capture (multi-monitor aware)
        // ====================================================================

        /// <summary>
        /// Builds a per-monitor SDR white level map keyed by GDI device name
        /// (e.g. "\\.\DISPLAY1"). Each value is the SDR white level in nits
        /// for that monitor. SDR monitors return ~80 nits; HDR monitors with
        /// elevated SDR white return a higher value (e.g. 160–300 nits).
        /// normScale = 80 / nits ensures each monitor's scRGB values are
        /// correctly normalised regardless of its HDR/SDR configuration.
        /// </summary>
        private static Dictionary<string, float> BuildSdrNitsMap()
        {
            var map = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            try
            {
                int r = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out int pc, out int mc);
                if (r != 0) return map;
                var paths = new DISPLAYCONFIG_PATH_INFO[pc];
                var modes = new DISPLAYCONFIG_MODE_INFO[mc];
                r = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pc, paths, ref mc, modes, IntPtr.Zero);
                if (r != 0) return map;

                for (int i = 0; i < pc; i++)
                {
                    // Resolve GDI device name for the source (e.g. "\\.\DISPLAY2")
                    var srcName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    srcName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    srcName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                    srcName.header.adapterId = paths[i].sourceInfo.adapterId;
                    srcName.header.id = paths[i].sourceInfo.id;
                    if (DisplayConfigGetDeviceInfo(ref srcName) != 0 || string.IsNullOrEmpty(srcName.viewGdiDeviceName))
                        continue;

                    // Query the SDR white level for this target
                    var white = new DISPLAYCONFIG_SDR_WHITE_LEVEL();
                    white.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
                    white.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>();
                    white.header.adapterId = paths[i].targetInfo.adapterId;
                    white.header.id = paths[i].targetInfo.id;

                    float nits = 80f;
                    if (DisplayConfigGetDeviceInfo(ref white) == 0 && white.SDRWhiteLevel > 0)
                        nits = (white.SDRWhiteLevel / 1000f) * 80f;

                    if (!map.ContainsKey(srcName.viewGdiDeviceName))
                        map[srcName.viewGdiDeviceName] = nits;

                    DebugHelper.WriteLine($"HDR: {srcName.viewGdiDeviceName} SDR white = {nits} nits (raw {white.SDRWhiteLevel})");
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR: BuildSdrNitsMap failed.");
            }
            return map;
        }

        /// <summary>
        /// Captures the specified rectangle in virtual desktop coordinates using
        /// DXGI Desktop Duplication. Handles multi-monitor setups by enumerating
        /// all outputs, capturing each intersecting monitor, and compositing the
        /// results onto a single bitmap. The D3D11 device and output duplications
        /// are cached across calls for significantly lower latency on warm shots.
        /// HDR output frames are acquired in parallel; GPU dispatches run sequentially
        /// (D3D11 immediate context is single-threaded).
        /// </summary>
        public Bitmap CaptureRectangleHDR(Rectangle rect)
        {
            IntPtr devicePtr = IntPtr.Zero;
            IntPtr contextPtr = IntPtr.Zero;

            try
            {
                // Build per-monitor SDR white level map once, then compute
                // normScale individually for each output inside the loop.
                var sdrNitsMap = BuildSdrNitsMap();

                // Steps 1 & 2: Get cached D3D11 device and DXGI adapter (~0 ms warm, ~40 ms cold).
                DxgiDeviceCache dxgiCache = GetOrCreateDxgiDeviceCache(out devicePtr, out contextPtr);
                if (dxgiCache == null) return null;

                // Create the final composite bitmap matching the requested rect size.
                Bitmap result = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                bool anyOutputCaptured = false;

                // Step 3: Enumerate outputs; build a list of HDR capture jobs.
                // SDR outputs are handled inline via GDI BitBlt.
                var hdrJobs = new List<(DxgiDeviceCache.CachedDuplication dupl, string devName,
                    Rectangle monitorRect, Rectangle intersection, float normScale)>();

                for (uint outputIdx = 0; ; outputIdx++)
                {
                    IDXGIOutput output;
                    try
                    {
                        int hr = dxgiCache.Adapter.EnumOutputs(outputIdx, out output);
                        if (hr == DXGI_ERROR_NOT_FOUND || output == null) break;
                        if (hr != 0)
                        {
                            DebugHelper.WriteLine($"HDR: EnumOutputs({outputIdx}) failed 0x{hr:X8}");
                            break;
                        }
                    }
                    catch (COMException ex) when (ex.HResult == DXGI_ERROR_NOT_FOUND)
                    {
                        break;
                    }

                    try
                    {
                        output.GetDesc(out DXGI_OUTPUT_DESC outputDesc);
                        Rectangle monitorRect = new Rectangle(
                            outputDesc.DesktopCoordinates.Left,
                            outputDesc.DesktopCoordinates.Top,
                            outputDesc.DesktopCoordinates.Right  - outputDesc.DesktopCoordinates.Left,
                            outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top);

                        Rectangle intersection = Rectangle.Intersect(rect, monitorRect);
                        if (intersection.Width <= 0 || intersection.Height <= 0)
                        {
                            DebugHelper.WriteLine($"HDR: Output {outputIdx} ({outputDesc.DeviceName}) does not intersect capture rect, skipping.");
                            continue;
                        }

                        DebugHelper.WriteLine($"HDR: Output {outputIdx} ({outputDesc.DeviceName}) {monitorRect}, intersection={intersection}");

                        float sdrWhiteNits = sdrNitsMap.TryGetValue(outputDesc.DeviceName, out float perMonNits) ? perMonNits : 80f;
                        float normScale = 80f / sdrWhiteNits;
                        DebugHelper.WriteLine($"HDR: Output {outputIdx} normScale={normScale:F3} ({sdrWhiteNits} nits)");

                        // Ensure duplication exists: HDR outputs get a cached IDXGIOutputDuplication;
                        // SDR outputs are remembered and dispatched directly to the GDI fast path.
                        bool isHDR = EnsureOutputDuplication(devicePtr, output, outputDesc.DeviceName, dxgiCache, out bool freshDupl);

                        if (!isHDR)
                        {
                            DebugHelper.WriteLine($"HDR: Output {outputIdx} is SDR (format={DXGI_FORMAT_B8G8R8A8_UNORM}), using GDI fast path.");
                            bool gdiOk = CaptureOutputGDI(intersection, rect, result);
                            if (gdiOk) anyOutputCaptured = true;
                            continue;
                        }

                        DebugHelper.WriteLine($"HDR: Output {outputIdx} ({outputDesc.DeviceName}) is HDR, queued for parallel acquisition.");
                        dxgiCache.TryGetDuplication(outputDesc.DeviceName, out var cachedDupl);
                        hdrJobs.Add((cachedDupl, outputDesc.DeviceName, monitorRect, intersection, normScale));

                        // Freshly-created duplications need a short warm-up so DWM can
                        // queue a frame before we call AcquireNextFrame.
                        if (freshDupl) Thread.Sleep(5);
                    }
                    catch (Exception e)
                    {
                        DebugHelper.WriteException(e, $"HDR: Output {outputIdx} capture failed, continuing.");
                    }
                }

                // Step 4: Launch AcquireNextFrame for all HDR outputs in parallel.
                if (hdrJobs.Count > 0)
                {
                    var tasks = new Task<AcquiredHDRFrame>[hdrJobs.Count];
                    for (int i = 0; i < hdrJobs.Count; i++)
                    {
                        var job = hdrJobs[i];   // capture loop var before Task.Run
                        tasks[i] = Task.Run(() =>
                            AcquireHDRFrame(job.dupl, job.devName, job.monitorRect, rect, job.intersection, job.normScale));
                    }

                    Task.WaitAll(tasks);

                    // Step 5: Sequential GPU dispatch (D3D11 immediate context is single-threaded).
                    bool deviceLost = false;
                    for (int i = 0; i < tasks.Length; i++)
                    {
                        AcquiredHDRFrame frame = tasks[i].Result;

                        if (frame.IsDeviceLost)
                        {
                            deviceLost = true;
                            continue;
                        }
                        if (frame.IsAccessLost)
                        {
                            dxgiCache.InvalidateDuplication(hdrJobs[i].devName);
                            continue;
                        }
                        if (!frame.Success) continue;

                        try
                        {
                            bool ok = ProcessHDRTexture(devicePtr, contextPtr,
                                frame.TexPtr, frame.TexFormat, frame.Rotation,
                                frame.TexW, frame.TexH,
                                frame.MonitorRect, frame.CaptureRect, frame.Intersection,
                                result, frame.NormScale);
                            if (ok) anyOutputCaptured = true;
                        }
                        finally
                        {
                            // ReleaseFrame frees the acquired frame but keeps the duplication alive.
                            var releaseFrame = Marshal.GetDelegateForFunctionPointer<Del_ReleaseFrame>(VT(frame.DuplicationPtr, 14));
                            releaseFrame(frame.DuplicationPtr);
                            Marshal.Release(frame.TexPtr);
                            Marshal.Release(frame.ResourcePtr);
                        }
                    }

                    if (deviceLost)
                    {
                        lock (_dxgiDevCacheLock)
                        {
                            _dxgiDevCache?.Invalidate();
                            _dxgiDevCache = null;
                        }
                    }
                }

                if (!anyOutputCaptured)
                {
                    DebugHelper.WriteLine("HDR: No outputs captured successfully.");
                    result.Dispose();
                    return null;
                }

                DebugHelper.WriteLine($"HDR: Composite output {result.Width}x{result.Height}");
                return result;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR capture failed.");
                return null;
            }
            finally
            {
                if (contextPtr != IntPtr.Zero) Marshal.Release(contextPtr);
                if (devicePtr != IntPtr.Zero) Marshal.Release(devicePtr);
            }
        }

        /// <summary>
        /// Captures an SDR output region using GDI BitBlt and draws it into the
        /// composite bitmap. Much faster than the full DXGI duplication pipeline
        /// for monitors that don't need HDR conversion.
        /// </summary>
        private bool CaptureOutputGDI(Rectangle intersection, Rectangle captureRect, Bitmap composite)
        {
            int dstX = intersection.X - captureRect.X;
            int dstY = intersection.Y - captureRect.Y;

            try
            {
                // Use GDI BitBlt to capture the SDR region. CaptureRectangleNative
                // uses virtual desktop coordinates, which is exactly what we have.
                using (Bitmap sdrCapture = CaptureRectangleNative(intersection, false))
                {
                    if (sdrCapture == null) return false;

                    using (Graphics g = Graphics.FromImage(composite))
                    {
                        g.DrawImageUnscaled(sdrCapture, dstX, dstY);
                    }
                }

                DebugHelper.WriteLine($"HDR: SDR output captured via GDI ({intersection.Width}x{intersection.Height})");
                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR: GDI fallback for SDR output failed.");
                return false;
            }
        }

        // Acquisition result returned from a Task.Run AcquireNextFrame worker.
        private struct AcquiredHDRFrame
        {
            public bool Success;
            public bool IsAccessLost;   // duplication gone; invalidate and recreate
            public bool IsDeviceLost;   // device gone; invalidate entire cache
            public IntPtr TexPtr;       // QI'd ID3D11Texture2D; caller must Marshal.Release
            public IntPtr ResourcePtr;  // IUnknown for the resource; caller must Marshal.Release
            public int TexW, TexH;
            public int TexFormat;
            public uint Rotation;
            public Rectangle Intersection;
            public Rectangle MonitorRect;
            public Rectangle CaptureRect;
            public float NormScale;
            public IntPtr DuplicationPtr;  // raw AddRef'd IDXGIOutputDuplication* for ReleaseFrame after GPU dispatch
        }

        /// <summary>
        /// Called from Task.Run threads. Acquires the next desktop frame from a cached
        /// IDXGIOutputDuplication. Returns metadata for later sequential GPU dispatch.
        /// The acquired frame is held open (caller must ReleaseFrame + Release TexPtr/ResourcePtr).
        /// </summary>
        private static AcquiredHDRFrame AcquireHDRFrame(
            DxgiDeviceCache.CachedDuplication cachedDupl, string devName,
            Rectangle monitorRect, Rectangle captureRect, Rectangle intersection, float normScale)
        {
            IntPtr duplPtr = cachedDupl.DuplicationPtr;
            var result = new AcquiredHDRFrame
            {
                MonitorRect    = monitorRect,
                CaptureRect    = captureRect,
                Intersection   = intersection,
                NormScale      = normScale,
                TexFormat      = cachedDupl.TexFormat,
                Rotation       = cachedDupl.Rotation,
                DuplicationPtr = duplPtr
            };
            var acquireNextFrame = Marshal.GetDelegateForFunctionPointer<Del_AcquireNextFrame>(VT(duplPtr, 8));
            IntPtr resourcePtr0 = IntPtr.Zero;

            for (int attempt = 0; attempt < 30; attempt++)
            {
                int acquireHr = acquireNextFrame(duplPtr, 200, out DXGI_OUTDUPL_FRAME_INFO frameInfo, out resourcePtr0);

                if (acquireHr == 0) break;

                if (resourcePtr0 != IntPtr.Zero) { Marshal.Release(resourcePtr0); resourcePtr0 = IntPtr.Zero; }

                if (acquireHr == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    DebugHelper.WriteLine($"HDR: AcquireNextFrame timeout on {devName}, attempt {attempt + 1}");
                    continue;
                }
                if (acquireHr == DXGI_ERROR_ACCESS_LOST)
                {
                    DebugHelper.WriteLine($"HDR: DXGI_ERROR_ACCESS_LOST on {devName}");
                    result.IsAccessLost = true;
                    return result;
                }
                if (acquireHr == DXGI_ERROR_DEVICE_REMOVED || acquireHr == DXGI_ERROR_DEVICE_RESET)
                {
                    DebugHelper.WriteLine($"HDR: device lost 0x{acquireHr:X8} on {devName}");
                    result.IsDeviceLost = true;
                    return result;
                }
                DebugHelper.WriteLine($"HDR: AcquireNextFrame 0x{acquireHr:X8} on {devName}, attempt {attempt + 1}");
                Thread.Sleep(5);
            }

            if (resourcePtr0 == IntPtr.Zero)
            {
                DebugHelper.WriteLine($"HDR: Timed out acquiring frame on {devName}.");
                return result;  // Success = false
            }

            // resourcePtr0 is the raw IDXGIResource* (already AddRef'd by AcquireNextFrame).
            Guid texGuid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            int qiHr = Marshal.QueryInterface(resourcePtr0, in texGuid, out IntPtr texPtr);
            if (qiHr != 0 || texPtr == IntPtr.Zero)
            {
                DebugHelper.WriteLine($"HDR: QI for ID3D11Texture2D failed 0x{qiHr:X8} on {devName}");
                Marshal.Release(resourcePtr0);
                var releaseFrameOnErr = Marshal.GetDelegateForFunctionPointer<Del_ReleaseFrame>(VT(duplPtr, 14));
                releaseFrameOnErr(duplPtr);
                return result;  // Success = false
            }

            var getDesc = Marshal.GetDelegateForFunctionPointer<Del_GetTexDesc>(VT(texPtr, 10));
            getDesc(texPtr, out D3D11_TEXTURE2D_DESC td);

            result.TexPtr      = texPtr;
            result.ResourcePtr = resourcePtr0;
            result.TexW        = (int)td.Width;
            result.TexH        = (int)td.Height;
            result.Success     = true;
            DebugHelper.WriteLine($"HDR: Texture {result.TexW}x{result.TexH} format={td.Format} acquired from {devName}");
            return result;
        }

        /// <summary>
        /// Runs the GPU compute shader to tonemap the acquired HDR texture into the
        /// composite bitmap. Falls back to CPU conversion if the GPU path fails.
        /// texPtr must remain valid for the duration of this call (caller holds the frame).
        /// </summary>
        private bool ProcessHDRTexture(
            IntPtr devicePtr, IntPtr contextPtr,
            IntPtr texPtr, int texFormat, uint texRotation, int texW, int texH,
            Rectangle monitorRect, Rectangle captureRect, Rectangle intersection,
            Bitmap composite, float normScale)
        {
            IntPtr stagingPtr = IntPtr.Zero;
            try
            {
                int logSrcX  = intersection.X - monitorRect.X;
                int logSrcY  = intersection.Y - monitorRect.Y;
                int logCopyW = intersection.Width;
                int logCopyH = intersection.Height;
                int dstX = intersection.X - captureRect.X;
                int dstY = intersection.Y - captureRect.Y;

                DebugHelper.WriteLine($"HDR: Texture {texW}x{texH} format={texFormat}");

                bool gpuOk = BlitHDRToCompositeGPU(
                    devicePtr, contextPtr, texPtr, texFormat,
                    logSrcX, logSrcY, logCopyW, logCopyH,
                    texW, texH, texRotation,
                    composite, dstX, dstY, normScale);

                if (!gpuOk)
                {
                    DebugHelper.WriteLine("HDR: GPU path failed, falling back to CPU.");

                    var getDesc = Marshal.GetDelegateForFunctionPointer<Del_GetTexDesc>(VT(texPtr, 10));
                    getDesc(texPtr, out D3D11_TEXTURE2D_DESC td);

                    td.Usage          = D3D11_USAGE_STAGING;
                    td.BindFlags      = 0;
                    td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                    td.MiscFlags      = 0;
                    var createTex = Marshal.GetDelegateForFunctionPointer<Del_CreateTex2D>(VT(devicePtr, 5));
                    int hr2 = createTex(devicePtr, ref td, IntPtr.Zero, out stagingPtr);
                    if (hr2 != 0)
                    {
                        DebugHelper.WriteLine($"HDR: CreateTexture2D staging failed 0x{hr2:X8}");
                        return false;
                    }

                    var copy = Marshal.GetDelegateForFunctionPointer<Del_CopyResource>(VT(contextPtr, 47));
                    copy(contextPtr, stagingPtr, texPtr);

                    var map = Marshal.GetDelegateForFunctionPointer<Del_Map>(VT(contextPtr, 14));
                    hr2 = map(contextPtr, stagingPtr, 0, D3D11_MAP_READ, 0, out D3D11_MAPPED_SUBRESOURCE mapped);
                    if (hr2 != 0)
                    {
                        DebugHelper.WriteLine($"HDR: Map failed 0x{hr2:X8}");
                        return false;
                    }

                    try
                    {
                        if (texRotation <= 1) // IDENTITY (or unspecified)
                        {
                            int copyW = Math.Min(logCopyW, texW - logSrcX);
                            int copyH = Math.Min(logCopyH, texH - logSrcY);
                            if (copyW > 0 && copyH > 0)
                            {
                                BlitHDRToComposite(mapped.pData, (int)mapped.RowPitch, texW, texH,
                                    td.Format, logSrcX, logSrcY, copyW, copyH,
                                    composite, dstX, dstY, normScale);
                            }
                        }
                        else
                        {
                            int phySrcX, phySrcY, phyCopyW, phyCopyH;
                            RotateFlipType flipType;

                            switch (texRotation)
                            {
                                case 2:
                                    phySrcX  = logSrcY;
                                    phySrcY  = texH - logSrcX - logCopyW;
                                    phyCopyW = logCopyH;
                                    phyCopyH = logCopyW;
                                    flipType = RotateFlipType.Rotate90FlipNone;
                                    break;
                                case 3:
                                    phySrcX  = texW - logSrcX - logCopyW;
                                    phySrcY  = texH - logSrcY - logCopyH;
                                    phyCopyW = logCopyW;
                                    phyCopyH = logCopyH;
                                    flipType = RotateFlipType.Rotate180FlipNone;
                                    break;
                                case 4:
                                    phySrcX  = texW - logSrcY - logCopyH;
                                    phySrcY  = logSrcX;
                                    phyCopyW = logCopyH;
                                    phyCopyH = logCopyW;
                                    flipType = RotateFlipType.Rotate270FlipNone;
                                    break;
                                default:
                                    phySrcX  = logSrcX;
                                    phySrcY  = logSrcY;
                                    phyCopyW = logCopyW;
                                    phyCopyH = logCopyH;
                                    flipType = RotateFlipType.RotateNoneFlipNone;
                                    break;
                            }

                            phySrcX  = Math.Max(0, phySrcX);
                            phySrcY  = Math.Max(0, phySrcY);
                            phyCopyW = Math.Min(phyCopyW, texW - phySrcX);
                            phyCopyH = Math.Min(phyCopyH, texH - phySrcY);

                            if (phyCopyW > 0 && phyCopyH > 0)
                            {
                                using (Bitmap tmp = new Bitmap(phyCopyW, phyCopyH, PixelFormat.Format32bppArgb))
                                {
                                    BlitHDRToComposite(mapped.pData, (int)mapped.RowPitch, texW, texH,
                                        td.Format, phySrcX, phySrcY, phyCopyW, phyCopyH,
                                        tmp, 0, 0, normScale);

                                    if (flipType != RotateFlipType.RotateNoneFlipNone)
                                        tmp.RotateFlip(flipType);

                                    using (Graphics g = Graphics.FromImage(composite))
                                        g.DrawImageUnscaled(tmp, dstX, dstY);
                                }
                            }
                        }
                    }
                    finally
                    {
                        var unmap = Marshal.GetDelegateForFunctionPointer<Del_Unmap>(VT(contextPtr, 15));
                        unmap(contextPtr, stagingPtr, 0);
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR: ProcessHDRTexture failed.");
                return false;
            }
            finally
            {
                if (stagingPtr != IntPtr.Zero) Marshal.Release(stagingPtr);
            }
        }


        // ====================================================================
        // D3D11 Device + DXGI Output Duplication Cache
        // ====================================================================
        // The D3D11 device and IDXGIOutputDuplication handles are cached across
        // screenshots. D3D11CreateDevice costs ~35-48 ms; creating an output
        // duplication costs ~1-3 ms per output. Caching both eliminates those
        // costs on warm screenshots and keeps the GpuResourceCache stable
        // (same device pointer → CS/CB/textures never rebuilt).
        // Cache access happens only on the capture thread; Task.Run threads
        // for parallel AcquireNextFrame access the COM objects directly without
        // touching the dictionary.
        // ====================================================================

        private sealed class DxgiDeviceCache
        {
            public IntPtr DevicePtr;   // AddRef'd reference (cache's own copy)
            public IntPtr ContextPtr;  // AddRef'd reference (cache's own copy)
            public IDXGIAdapter Adapter;

            /// <summary>Cached output duplication handle with its negotiated format and rotation.
            /// DuplicationPtr is a raw AddRef'd COM pointer so it is safe to pass to Task.Run
            /// threads without COM apartment marshaling issues.</summary>
            public sealed class CachedDuplication
            {
                public IntPtr DuplicationPtr;  // raw AddRef'd IDXGIOutputDuplication*
                public int TexFormat;
                public uint Rotation;
            }

            private readonly Dictionary<string, CachedDuplication> _duplications
                = new(StringComparer.OrdinalIgnoreCase);

            private readonly HashSet<string> _sdrOutputs
                = new(StringComparer.OrdinalIgnoreCase);

            public bool TryGetDuplication(string devName, out CachedDuplication entry)
                => _duplications.TryGetValue(devName, out entry);

            public void SetDuplication(string devName, IntPtr duplPtr, int texFormat, uint rotation)
                => _duplications[devName] = new CachedDuplication { DuplicationPtr = duplPtr, TexFormat = texFormat, Rotation = rotation };

            public bool IsSdrOutput(string devName) => _sdrOutputs.Contains(devName);
            public void MarkSdrOutput(string devName) => _sdrOutputs.Add(devName);

            public void InvalidateDuplication(string devName)
            {
                if (!_duplications.TryGetValue(devName, out var entry)) return;
                _duplications.Remove(devName);
                if (entry.DuplicationPtr != IntPtr.Zero)
                {
                    try
                    {
                        var releaseFrame = Marshal.GetDelegateForFunctionPointer<Del_ReleaseFrame>(VT(entry.DuplicationPtr, 14));
                        releaseFrame(entry.DuplicationPtr);
                    } catch { }
                    try { Marshal.Release(entry.DuplicationPtr); } catch { }
                }
            }

            public void Invalidate()
            {
                foreach (var entry in _duplications.Values)
                {
                    if (entry.DuplicationPtr == IntPtr.Zero) continue;
                    try
                    {
                        var releaseFrame = Marshal.GetDelegateForFunctionPointer<Del_ReleaseFrame>(VT(entry.DuplicationPtr, 14));
                        releaseFrame(entry.DuplicationPtr);
                    } catch { }
                    try { Marshal.Release(entry.DuplicationPtr); } catch { }
                }
                _duplications.Clear();
                _sdrOutputs.Clear();
                if (Adapter   != null)             { try { Marshal.ReleaseComObject(Adapter);      } catch { } Adapter   = null; }
                if (ContextPtr != IntPtr.Zero)     { try { Marshal.Release(ContextPtr);            } catch { } ContextPtr = IntPtr.Zero; }
                if (DevicePtr  != IntPtr.Zero)     { try { Marshal.Release(DevicePtr);             } catch { } DevicePtr  = IntPtr.Zero; }
            }
        }

        private static DxgiDeviceCache _dxgiDevCache;
        private static readonly object _dxgiDevCacheLock = new object();

        /// <summary>
        /// Returns the cached D3D11 device cache, creating it if needed.
        /// devicePtr and contextPtr are AddRef'd for the caller and MUST be Released in finally.
        /// </summary>
        private static DxgiDeviceCache GetOrCreateDxgiDeviceCache(out IntPtr devicePtr, out IntPtr contextPtr)
        {
            lock (_dxgiDevCacheLock)
            {
                if (_dxgiDevCache != null)
                {
                    Marshal.AddRef(_dxgiDevCache.DevicePtr);
                    Marshal.AddRef(_dxgiDevCache.ContextPtr);
                    devicePtr  = _dxgiDevCache.DevicePtr;
                    contextPtr = _dxgiDevCache.ContextPtr;
                    DebugHelper.WriteLine("HDR: D3D11 device from cache.");
                    return _dxgiDevCache;
                }

                int[] levels = { 0xb100, 0xb000 };
                int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, 0,
                    levels, (uint)levels.Length, D3D11_SDK_VERSION,
                    out IntPtr devPtr, out _, out IntPtr ctxPtr);
                if (hr != 0)
                {
                    DebugHelper.WriteLine($"HDR: D3D11CreateDevice failed 0x{hr:X8}");
                    devicePtr = contextPtr = IntPtr.Zero;
                    return null;
                }

                IDXGIAdapter adapter = null;
                var dxgiDev = (IDXGIDevice)Marshal.GetObjectForIUnknown(devPtr);
                try   { hr = dxgiDev.GetAdapter(out adapter); }
                finally { Marshal.ReleaseComObject(dxgiDev); }

                if (hr != 0 || adapter == null)
                {
                    DebugHelper.WriteLine($"HDR: GetAdapter failed 0x{hr:X8}");
                    Marshal.Release(ctxPtr);
                    Marshal.Release(devPtr);
                    devicePtr = contextPtr = IntPtr.Zero;
                    return null;
                }

                // Cache takes its own AddRef (ref count 1→2 for each pointer).
                Marshal.AddRef(devPtr);
                Marshal.AddRef(ctxPtr);
                _dxgiDevCache = new DxgiDeviceCache { DevicePtr = devPtr, ContextPtr = ctxPtr, Adapter = adapter };
                DebugHelper.WriteLine("HDR: D3D11 device created and cached.");

                // Caller gets the original refs from D3D11CreateDevice (count stays 2; Release → 1).
                devicePtr  = devPtr;
                contextPtr = ctxPtr;
                return _dxgiDevCache;
            }
        }

        /// <summary>
        /// Ensures a live IDXGIOutputDuplication is cached for devName (HDR path).
        /// For SDR outputs, marks them and returns false so the caller uses GDI.
        /// freshDupl is set to true when a brand-new duplication was just created.
        /// </summary>
        private static bool EnsureOutputDuplication(
            IntPtr devicePtr, IDXGIOutput output, string devName,
            DxgiDeviceCache cache, out bool freshDupl)
        {
            freshDupl = false;

            if (cache.TryGetDuplication(devName, out _)) return true;   // already cached (HDR)
            if (cache.IsSdrOutput(devName))              return false;  // already confirmed SDR

            IDXGIOutputDuplication duplication = null;
            int texFormat   = DXGI_FORMAT_B8G8R8A8_UNORM;
            uint texRotation = 1;

            try
            {
                int hr;
                try
                {
                    var output5 = (IDXGIOutput5)output;
                    int[] formats = { DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM };
                    hr = output5.DuplicateOutput1(Marshal.GetObjectForIUnknown(devicePtr), 0,
                        (uint)formats.Length, formats, out duplication);
                }
                catch (InvalidCastException)
                {
                    DebugHelper.WriteLine($"HDR: IDXGIOutput5 not supported for {devName}, trying IDXGIOutput1.");
                    var output1 = (IDXGIOutput1)output;
                    hr = output1.DuplicateOutput(Marshal.GetObjectForIUnknown(devicePtr), out duplication);
                }

                if (hr != 0 || duplication == null)
                {
                    DebugHelper.WriteLine($"HDR: DuplicateOutput failed 0x{hr:X8} for {devName}");
                    return false;
                }

                duplication.GetDesc(out DXGI_OUTDUPL_DESC dd);
                texFormat    = (int)dd.ModeDesc.Format;
                texRotation  = dd.Rotation;
                DebugHelper.WriteLine($"HDR: DuplicateOutput OK ({devName}), format={texFormat}, rotation={texRotation}");

                if (texFormat == DXGI_FORMAT_B8G8R8A8_UNORM)
                {
                    // SDR: mark and release the probe duplication; GDI handles this output.
                    cache.MarkSdrOutput(devName);
                    Marshal.ReleaseComObject(duplication);
                    DebugHelper.WriteLine($"HDR: Output {devName} is SDR (B8G8R8A8); switching to GDI path.");
                    return false;
                }

                // HDR: get the raw COM pointer, cache it, and release the managed RCW.
                // Raw IntPtr is safe to pass to Task.Run threads without apartment issues.
                IntPtr duplPtr = Marshal.GetIUnknownForObject(duplication);
                Marshal.ReleaseComObject(duplication);
                duplication = null;
                cache.SetDuplication(devName, duplPtr, texFormat, texRotation);
                freshDupl = true;
                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, $"HDR: EnsureOutputDuplication failed for {devName}");
                if (duplication != null) { try { Marshal.ReleaseComObject(duplication); } catch { } }
                return false;
            }
        }

        // ====================================================================
        // GPU Pixel Conversion (compute shader path)
        // ====================================================================

        // Cached bytecode loaded once from the embedded HDRTonemap.cso resource.
        private static byte[] _hdrShaderBytecode;
        private static readonly object _shaderBytecodeLock = new object();

        // --------------------------------------------------------------------
        // Per-device GPU resource cache.
        // CS and CB are shared across all outputs within one screenshot (same
        // D3D11 device) and reused across screenshots when the device pointer
        // is stable.  A Marshal.AddRef on DevicePtr prevents the OS from
        // recycling that address, making pointer equality a safe identity check.
        // Output/staging textures are cached per (width, height) pair.
        // --------------------------------------------------------------------
        private sealed class GpuResourceCache
        {
            public IntPtr DevicePtr;   // AddRef'd; zero when not populated
            public IntPtr CS;          // ID3D11ComputeShader
            public IntPtr CB;          // ID3D11Buffer (DYNAMIC constant buffer, 48 B)

            private readonly Dictionary<(int w, int h), (IntPtr outTex, IntPtr uav, IntPtr staging)>
                _textures = new();

            public bool TryGetTextures(int w, int h,
                out IntPtr outTex, out IntPtr uav, out IntPtr staging)
            {
                if (_textures.TryGetValue((w, h), out var t))
                {
                    (outTex, uav, staging) = t;
                    return true;
                }
                outTex = uav = staging = IntPtr.Zero;
                return false;
            }

            public void CacheTextures(int w, int h, IntPtr outTex, IntPtr uav, IntPtr staging)
                => _textures[(w, h)] = (outTex, uav, staging);

            public void Invalidate()
            {
                if (CS != IntPtr.Zero) { Marshal.Release(CS); CS = IntPtr.Zero; }
                if (CB != IntPtr.Zero) { Marshal.Release(CB); CB = IntPtr.Zero; }
                foreach (var (outTex, uav, staging) in _textures.Values)
                {
                    if (outTex   != IntPtr.Zero) Marshal.Release(outTex);
                    if (uav      != IntPtr.Zero) Marshal.Release(uav);
                    if (staging  != IntPtr.Zero) Marshal.Release(staging);
                }
                _textures.Clear();
                if (DevicePtr != IntPtr.Zero) { Marshal.Release(DevicePtr); DevicePtr = IntPtr.Zero; }
            }
        }

        private static GpuResourceCache _gpuCache;
        private static readonly object _gpuCacheLock = new object();

        private static byte[] GetHdrShaderBytecode()
        {
            if (_hdrShaderBytecode != null)
                return _hdrShaderBytecode;

            lock (_shaderBytecodeLock)
            {
                if (_hdrShaderBytecode != null)
                    return _hdrShaderBytecode;

                try
                {
                    using var stream = typeof(Screenshot).Assembly.GetManifestResourceStream("HDRTonemap.cso");
                    if (stream == null)
                    {
                        DebugHelper.WriteLine("HDR GPU: shader resource 'HDRTonemap.cso' not found in assembly.");
                        return null;
                    }
                    var bytes = new byte[stream.Length];
                    _ = stream.Read(bytes, 0, bytes.Length);
                    _hdrShaderBytecode = bytes;
                    DebugHelper.WriteLine($"HDR GPU: loaded shader bytecode ({bytes.Length} bytes).");
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "HDR GPU: failed to load shader bytecode.");
                }
                return _hdrShaderBytecode;
            }
        }

        // Returns the cache for devicePtr, creating CS + CB if the device changed.
        private static unsafe GpuResourceCache GetOrCreateGpuCache(IntPtr devicePtr, byte[] bytecode)
        {
            lock (_gpuCacheLock)
            {
                if (_gpuCache != null && _gpuCache.DevicePtr == devicePtr)
                    return _gpuCache;

                // Device changed or first call: release stale resources and rebuild.
                _gpuCache?.Invalidate();
                _gpuCache = null;

                IntPtr csPtr = IntPtr.Zero, cbPtr = IntPtr.Zero;
                try
                {
                    var createCS = Marshal.GetDelegateForFunctionPointer<Del_CreateComputeShader>(VT(devicePtr, 18));
                    fixed (byte* pBytecode = bytecode)
                    {
                        int hr = createCS(devicePtr, pBytecode, (UIntPtr)(uint)bytecode.Length, IntPtr.Zero, out csPtr);
                        if (hr != 0)
                        {
                            DebugHelper.WriteLine($"HDR GPU: CreateComputeShader failed 0x{hr:X8}");
                            return null;
                        }
                    }

                    var cbDesc = new D3D11_BUFFER_DESC
                    {
                        ByteWidth      = (uint)sizeof(HdrTonemapCB),
                        Usage          = D3D11_USAGE_DYNAMIC,
                        BindFlags      = (uint)D3D11_BIND_CONSTANT_BUFFER,
                        CPUAccessFlags = D3D11_CPU_ACCESS_WRITE,
                    };
                    var createBuf = Marshal.GetDelegateForFunctionPointer<Del_CreateBuffer>(VT(devicePtr, 3));
                    {
                        int hr = createBuf(devicePtr, ref cbDesc, IntPtr.Zero, out cbPtr);
                        if (hr != 0)
                        {
                            DebugHelper.WriteLine($"HDR GPU: CreateBuffer (CB) failed 0x{hr:X8}");
                            return null;
                        }
                    }

                    Marshal.AddRef(devicePtr);
                    _gpuCache = new GpuResourceCache { DevicePtr = devicePtr, CS = csPtr, CB = cbPtr };
                    return _gpuCache;
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "HDR GPU: GetOrCreateGpuCache failed.");
                    if (csPtr != IntPtr.Zero) Marshal.Release(csPtr);
                    if (cbPtr != IntPtr.Zero) Marshal.Release(cbPtr);
                    return null;
                }
            }
        }

        /// <summary>
        /// GPU path: runs the HDRTonemap compute shader on <paramref name="srcTexPtr"/>
        /// and blits the result (already sRGB BGRA8) into the composite bitmap.
        /// Handles display rotation in-shader; output is always in logical orientation.
        /// Returns false on any D3D11 failure so the caller can fall back to the CPU path.
        /// </summary>
        private static unsafe bool BlitHDRToCompositeGPU(
            IntPtr devicePtr, IntPtr contextPtr, IntPtr srcTexPtr,
            int texFormat, int logSrcX, int logSrcY, int logCopyW, int logCopyH,
            int texW, int texH, uint texRotation,
            Bitmap composite, int dstX, int dstY, float normScale)
        {
            byte[] bytecode = GetHdrShaderBytecode();
            if (bytecode == null)
                return false;

            GpuResourceCache cache = GetOrCreateGpuCache(devicePtr, bytecode);
            if (cache == null)
                return false;

            // SRV is always fresh (it wraps the per-frame DXGI acquired texture).
            IntPtr srvPtr = IntPtr.Zero;

            // Output/staging textures come from the per-size cache.
            bool texFromCache = cache.TryGetTextures(logCopyW, logCopyH,
                out IntPtr outTexPtr, out IntPtr uavPtr, out IntPtr stagingPtr);

            try
            {
                // ----------------------------------------------------------
                // Lazily create output texture, UAV, and staging texture for
                // this (width, height).  All three succeed or we return false
                // without caching anything.
                // ----------------------------------------------------------
                if (!texFromCache)
                {
                    var createTex = Marshal.GetDelegateForFunctionPointer<Del_CreateTex2D>(VT(devicePtr, 5));

                    var outDesc = new D3D11_TEXTURE2D_DESC
                    {
                        Width = (uint)logCopyW, Height = (uint)logCopyH,
                        MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_R8G8B8A8_UNORM,
                        SampleCount = 1, Usage = D3D11_USAGE_DEFAULT,
                        BindFlags = (uint)D3D11_BIND_UNORDERED_ACCESS,
                    };
                    {
                        int hr = createTex(devicePtr, ref outDesc, IntPtr.Zero, out outTexPtr);
                        if (hr != 0)
                        {
                            DebugHelper.WriteLine($"HDR GPU: CreateTexture2D (output) failed 0x{hr:X8}");
                            return false;
                        }
                    }

                    var uavDesc = new D3D11_UAV_DESC_TEX2D
                    {
                        Format = DXGI_FORMAT_R8G8B8A8_UNORM,
                        ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D,
                    };
                    var createUAV = Marshal.GetDelegateForFunctionPointer<Del_CreateUAV>(VT(devicePtr, 8));
                    {
                        int hr = createUAV(devicePtr, outTexPtr, ref uavDesc, out uavPtr);
                        if (hr != 0)
                        {
                            DebugHelper.WriteLine($"HDR GPU: CreateUnorderedAccessView failed 0x{hr:X8}");
                            return false;
                        }
                    }

                    var stagDesc = new D3D11_TEXTURE2D_DESC
                    {
                        Width = (uint)logCopyW, Height = (uint)logCopyH,
                        MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_R8G8B8A8_UNORM,
                        SampleCount = 1, Usage = D3D11_USAGE_STAGING,
                        CPUAccessFlags = D3D11_CPU_ACCESS_READ,
                    };
                    {
                        int hr = createTex(devicePtr, ref stagDesc, IntPtr.Zero, out stagingPtr);
                        if (hr != 0)
                        {
                            DebugHelper.WriteLine($"HDR GPU: CreateTexture2D (staging) failed 0x{hr:X8}");
                            return false;
                        }
                    }

                    // All three created: hand ownership to the cache.
                    cache.CacheTextures(logCopyW, logCopyH, outTexPtr, uavPtr, stagingPtr);
                    texFromCache = true;
                }

                // ----------------------------------------------------------
                // SRV: always created fresh for the current DXGI frame.
                // ----------------------------------------------------------
                var srvDesc = new D3D11_SRV_DESC_TEX2D
                {
                    Format = texFormat, ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D,
                    MostDetailedMip = 0, MipLevels = 0xFFFFFFFF,
                };
                var createSRV = Marshal.GetDelegateForFunctionPointer<Del_CreateSRV>(VT(devicePtr, 7));
                {
                    int hr = createSRV(devicePtr, srcTexPtr, ref srvDesc, out srvPtr);
                    if (hr != 0)
                    {
                        DebugHelper.WriteLine($"HDR GPU: CreateShaderResourceView failed 0x{hr:X8}");
                        return false;
                    }
                }

                // ----------------------------------------------------------
                // Update constant buffer with this frame's parameters.
                // ----------------------------------------------------------
                var mapCtx = Marshal.GetDelegateForFunctionPointer<Del_Map>(VT(contextPtr, 14));
                {
                    int hr = mapCtx(contextPtr, cache.CB, 0, D3D11_MAP_WRITE_DISCARD, 0,
                        out D3D11_MAPPED_SUBRESOURCE mapped);
                    if (hr != 0)
                    {
                        DebugHelper.WriteLine($"HDR GPU: Map CB failed 0x{hr:X8}");
                        return false;
                    }
                    *(HdrTonemapCB*)mapped.pData = new HdrTonemapCB
                    {
                        NormScale   = normScale,
                        InputFormat = texFormat == DXGI_FORMAT_R16G16B16A16_FLOAT
                                      ? HDR_FORMAT_SCRGB : HDR_FORMAT_HDR10,
                        SrcOffsetX  = (uint)logSrcX,  SrcOffsetY = (uint)logSrcY,
                        CopyWidth   = (uint)logCopyW, CopyHeight = (uint)logCopyH,
                        TexRotation = texRotation,
                        TexWidth    = (uint)texW,     TexHeight  = (uint)texH,
                    };
                    Marshal.GetDelegateForFunctionPointer<Del_Unmap>(VT(contextPtr, 15))(contextPtr, cache.CB, 0);
                }

                // ----------------------------------------------------------
                // Bind pipeline and dispatch.
                // ----------------------------------------------------------
                var csSetShader = Marshal.GetDelegateForFunctionPointer<Del_CSSetShader>(VT(contextPtr, 69));
                csSetShader(contextPtr, cache.CS, IntPtr.Zero, 0);

                var csSetSRVs = Marshal.GetDelegateForFunctionPointer<Del_CSSetSRVs>(VT(contextPtr, 67));
                IntPtr srvLocal = srvPtr;
                csSetSRVs(contextPtr, 0, 1, &srvLocal);

                var csSetUAVs = Marshal.GetDelegateForFunctionPointer<Del_CSSetUAVs>(VT(contextPtr, 68));
                IntPtr uavLocal  = uavPtr;
                uint   initCount = unchecked((uint)-1);
                csSetUAVs(contextPtr, 0, 1, &uavLocal, &initCount);

                var csSetCBs = Marshal.GetDelegateForFunctionPointer<Del_CSSetCBs>(VT(contextPtr, 71));
                IntPtr cbLocal = cache.CB;
                csSetCBs(contextPtr, 0, 1, &cbLocal);

                Marshal.GetDelegateForFunctionPointer<Del_Dispatch>(VT(contextPtr, 41))(
                    contextPtr,
                    (uint)((logCopyW + 7) / 8),
                    (uint)((logCopyH + 7) / 8),
                    1);

                // Unbind (suppresses D3D11 debug-layer warnings on next draw call).
                IntPtr nullPtr = IntPtr.Zero;
                csSetSRVs(contextPtr, 0, 1, &nullPtr);
                csSetUAVs(contextPtr, 0, 1, &nullPtr, &initCount);

                // ----------------------------------------------------------
                // Readback: GPU output → staging → Bitmap.
                // ----------------------------------------------------------
                Marshal.GetDelegateForFunctionPointer<Del_CopyResource>(VT(contextPtr, 47))(
                    contextPtr, stagingPtr, outTexPtr);

                {
                    int hr = mapCtx(contextPtr, stagingPtr, 0, D3D11_MAP_READ, 0,
                        out D3D11_MAPPED_SUBRESOURCE mapped);
                    if (hr != 0)
                    {
                        DebugHelper.WriteLine($"HDR GPU: Map staging failed 0x{hr:X8}");
                        return false;
                    }
                    try
                    {
                        int copyW = Math.Min(logCopyW, composite.Width  - dstX);
                        int copyH = Math.Min(logCopyH, composite.Height - dstY);
                        if (copyW > 0 && copyH > 0)
                        {
                            var bd = composite.LockBits(
                                new Rectangle(dstX, dstY, copyW, copyH),
                                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                            try
                            {
                                long rowBytes = (long)copyW * 4;
                                // Single flat copy is only valid when source and destination
                                // have the same row stride.  bd.Stride is the full composite
                                // bitmap stride (compositeW × 4); it exceeds rowBytes whenever
                                // this output is narrower than the composite, and a flat copy
                                // would then corrupt adjacent pixels.
                                if (mapped.RowPitch == (uint)bd.Stride)
                                {
                                    Buffer.MemoryCopy(
                                        (byte*)mapped.pData, (byte*)bd.Scan0,
                                        (long)copyH * bd.Stride, (long)copyH * bd.Stride);
                                }
                                else
                                {
                                    for (int y = 0; y < copyH; y++)
                                    {
                                        byte* src = (byte*)mapped.pData + (long)y * mapped.RowPitch;
                                        byte* dst = (byte*)bd.Scan0     + (long)y * bd.Stride;
                                        Buffer.MemoryCopy(src, dst, rowBytes, rowBytes);
                                    }
                                }
                            }
                            finally { composite.UnlockBits(bd); }
                        }
                    }
                    finally
                    {
                        Marshal.GetDelegateForFunctionPointer<Del_Unmap>(VT(contextPtr, 15))(
                            contextPtr, stagingPtr, 0);
                    }
                }

                DebugHelper.WriteLine(
                    $"HDR GPU: dispatch complete ({logCopyW}x{logCopyH}, fmt={texFormat}, rot={texRotation})");
                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR GPU: BlitHDRToCompositeGPU failed.");
                return false;
            }
            finally
            {
                // SRV always released (per-frame resource).
                if (srvPtr != IntPtr.Zero) Marshal.Release(srvPtr);

                // If texture creation failed before we handed them to the cache,
                // release whatever was allocated.
                if (!texFromCache)
                {
                    if (outTexPtr  != IntPtr.Zero) Marshal.Release(outTexPtr);
                    if (uavPtr     != IntPtr.Zero) Marshal.Release(uavPtr);
                    if (stagingPtr != IntPtr.Zero) Marshal.Release(stagingPtr);
                }
            }
        }

        // ====================================================================
        // CPU Pixel Conversion (fallback path)
        // ====================================================================

        /// <summary>
        /// Converts and blits a region from the mapped HDR texture directly into
        /// the composite bitmap at the specified destination offset.
        /// </summary>
        private static void BlitHDRToComposite(IntPtr data, int rowPitch, int texW, int texH,
            int fmt, int srcX, int srcY, int copyW, int copyH,
            Bitmap composite, int dstX, int dstY, float normScale)
        {
            // Clamp to valid ranges
            if (srcX < 0) { dstX -= srcX; copyW += srcX; srcX = 0; }
            if (srcY < 0) { dstY -= srcY; copyH += srcY; srcY = 0; }
            copyW = Math.Min(copyW, texW - srcX);
            copyH = Math.Min(copyH, texH - srcY);
            copyW = Math.Min(copyW, composite.Width - dstX);
            copyH = Math.Min(copyH, composite.Height - dstY);
            if (copyW <= 0 || copyH <= 0) return;

            var bd = composite.LockBits(new Rectangle(dstX, dstY, copyW, copyH),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                unsafe
                {
                    for (int y = 0; y < copyH; y++)
                    {
                        byte* dst = (byte*)bd.Scan0 + y * bd.Stride;

                        if (fmt == DXGI_FORMAT_R16G16B16A16_FLOAT)
                        {
                            // scRGB (linear, scene-referred). Normalize by SDR white level,
                            // tonemap highlights, convert to sRGB.
                            byte* src = (byte*)data + (long)(srcY + y) * rowPitch + (long)srcX * 8;
                            for (int x = 0; x < copyW; x++)
                            {
                                ushort* p = (ushort*)(src + x * 8);
                                float r = HalfToFloat(p[0]);
                                float g = HalfToFloat(p[1]);
                                float b = HalfToFloat(p[2]);
                                float a = HalfToFloat(p[3]);

                                r = Math.Max(r * normScale, 0f);
                                g = Math.Max(g * normScale, 0f);
                                b = Math.Max(b * normScale, 0f);

                                TonemapBT2390(ref r, ref g, ref b);

                                r = LinearToSRGB(r);
                                g = LinearToSRGB(g);
                                b = LinearToSRGB(b);

                                dst[x * 4 + 0] = FloatToByte(b);
                                dst[x * 4 + 1] = FloatToByte(g);
                                dst[x * 4 + 2] = FloatToByte(r);
                                dst[x * 4 + 3] = FloatToByte(Math.Clamp(a, 0f, 1f));
                            }
                        }
                        else if (fmt == DXGI_FORMAT_R10G10B10A2_UNORM)
                        {
                            // HDR10: PQ (ST.2084) encoded, BT.2020 gamut.
                            // Decode PQ to linear nits, convert BT.2020 -> BT.709,
                            // normalize to [0,1] by dividing by 80 nits, tonemap, sRGB encode.
                            byte* src = (byte*)data + (long)(srcY + y) * rowPitch + (long)srcX * 4;
                            for (int x = 0; x < copyW; x++)
                            {
                                uint pixel = *(uint*)(src + x * 4);
                                float rN = PQ_EOTF((pixel & 0x3FFu) / 1023f);
                                float gN = PQ_EOTF(((pixel >> 10) & 0x3FFu) / 1023f);
                                float bN = PQ_EOTF(((pixel >> 20) & 0x3FFu) / 1023f);

                                // BT.2020 to BT.709 color matrix, result in nits
                                float r = (1.6605f * rN - 0.5877f * gN - 0.0728f * bN) / 80f;
                                float g = (-0.1246f * rN + 1.1330f * gN - 0.0084f * bN) / 80f;
                                float b = (-0.0182f * rN - 0.1006f * gN + 1.1187f * bN) / 80f;

                                r = Math.Max(r, 0f);
                                g = Math.Max(g, 0f);
                                b = Math.Max(b, 0f);

                                TonemapBT2390(ref r, ref g, ref b);

                                r = LinearToSRGB(r);
                                g = LinearToSRGB(g);
                                b = LinearToSRGB(b);

                                dst[x * 4 + 0] = FloatToByte(b);
                                dst[x * 4 + 1] = FloatToByte(g);
                                dst[x * 4 + 2] = FloatToByte(r);
                                dst[x * 4 + 3] = 255;
                            }
                        }
                        else
                        {
                            // SDR (B8G8R8A8_UNORM). Already in the right layout for
                            // Format32bppArgb, just copy directly.
                            byte* src = (byte*)data + (long)(srcY + y) * rowPitch + (long)srcX * 4;
                            Buffer.MemoryCopy(src, dst, bd.Stride, copyW * 4);
                        }
                    }
                }
            }
            finally
            {
                composite.UnlockBits(bd);
            }
        }

        // ====================================================================
        // Color Math
        // ====================================================================

        /// <summary>
        /// BT.2390 style luminance-based tonemap. SDR content (luminance &lt;= 1.0)
        /// passes through untouched. Only highlights above 1.0 are compressed,
        /// capped at 1.5 to avoid hard clipping artifacts.
        /// </summary>
        private static void TonemapBT2390(ref float r, ref float g, ref float b)
        {
            float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            if (lum > 1.0f)
            {
                float excess = lum - 1.0f;
                float compressed = 1.0f + excess / (1.0f + excess);
                if (compressed > 1.5f) compressed = 1.5f;

                float scale = compressed / lum;
                r *= scale;
                g *= scale;
                b *= scale;
            }

            r = Math.Clamp(r, 0f, 1f);
            g = Math.Clamp(g, 0f, 1f);
            b = Math.Clamp(b, 0f, 1f);
        }

        /// <summary>
        /// ST.2084 (PQ) Electro-Optical Transfer Function.
        /// Converts PQ encoded value [0,1] to absolute luminance in nits [0,10000].
        /// </summary>
        private static float PQ_EOTF(float N)
        {
            const float m1 = 0.1593017578125f;
            const float m2 = 78.84375f;
            const float c1 = 0.8359375f;
            const float c2 = 18.8515625f;
            const float c3 = 18.6875f;

            float Np = MathF.Pow(Math.Max(N, 0f), 1f / m2);
            float num = Math.Max(Np - c1, 0f);
            float den = c2 - c3 * Np;
            if (den <= 0f) return 0f;
            return MathF.Pow(num / den, 1f / m1) * 10000f;
        }

        private static float LinearToSRGB(float x)
        {
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            return x <= 0.0031308f ? x * 12.92f : 1.055f * MathF.Pow(x, 1f / 2.4f) - 0.055f;
        }

        private static unsafe float HalfToFloat(ushort h)
        {
            uint sign = ((uint)h & 0x8000u) << 16;
            uint exp = ((uint)h >> 10) & 0x1F;
            uint man = (uint)h & 0x3FF;
            uint result;

            if (exp == 0)
            {
                if (man == 0) { result = sign; return *(float*)&result; }
                while ((man & 0x400) == 0) { man <<= 1; exp--; }
                exp++; man &= ~0x400u; exp += 127 - 15;
                result = sign | (exp << 23) | (man << 13);
            }
            else if (exp == 31)
            {
                result = sign | 0x7F800000u | (man << 13);
            }
            else
            {
                exp += 127 - 15;
                result = sign | (exp << 23) | (man << 13);
            }

            return *(float*)&result;
        }

        private static byte FloatToByte(float v)
        {
            return (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
        }
    }
}