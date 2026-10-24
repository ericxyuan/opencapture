using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OpenCapture
{
    /// <summary>
    /// Captures HDR outputs in their native scRGB FP16 composition format and
    /// converts them to an SDR bitmap. This avoids applying a brightness curve to
    /// ordinary 8-bit application pixels, which GDI already captures exactly.
    /// </summary>
    internal static class HdrDesktopCapture
    {
        private const int DxgiFormatR16G16B16A16Float = 10;
        private const int DxgiFormatB8G8R8A8Unorm = 87;
        private const int D3dDriverTypeUnknown = 0;
        private const uint D3d11CreateDeviceBgraSupport = 0x20;
        private const uint D3d11SdkVersion = 7;
        private const int D3d11UsageStaging = 3;
        private const uint D3d11CpuAccessRead = 0x00020000;
        private const int D3d11MapRead = 1;
        private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
        private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);

        private static readonly Guid IidDxgiFactory1 =
            new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IidDxgiOutput5 =
            new Guid("80a07424-ab52-42eb-833c-0c42fd282d98");
        private static readonly Guid IidD3d11Texture2D =
            new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        private static readonly float[] HalfValues = CreateHalfLookup();
        private static readonly byte[] LinearToSrgbValues = CreateLinearToSrgbLookup();

        /// <summary>
        /// Replaces only HDR-monitor rectangles that can be acquired as FP16.
        /// GDI pixels remain untouched for SDR displays and as a safe fallback.
        /// </summary>
        internal static int ReplaceHdrOutputs(
            Bitmap destination,
            Rectangle desktopBounds,
            IList<HdrMonitorAdjustment> adjustments)
        {
            if (destination == null)
            {
                throw new ArgumentNullException("destination");
            }
            if (adjustments == null || adjustments.Count == 0)
            {
                return 0;
            }

            Dictionary<string, HdrMonitorAdjustment> byDevice =
                new Dictionary<string, HdrMonitorAdjustment>(StringComparer.OrdinalIgnoreCase);
            foreach (HdrMonitorAdjustment adjustment in adjustments)
            {
                if (adjustment != null && adjustment.Bounds.IntersectsWith(desktopBounds))
                {
                    byDevice[adjustment.DeviceName] = adjustment;
                }
            }
            if (byDevice.Count == 0)
            {
                return 0;
            }

            IntPtr factory = IntPtr.Zero;
            int replaced = 0;
            try
            {
                Guid factoryId = IidDxgiFactory1;
                int result = CreateDXGIFactory1(ref factoryId, out factory);
                if (result < 0 || factory == IntPtr.Zero)
                {
                    return 0;
                }

                EnumAdapters1Delegate enumAdapters = GetDelegate<EnumAdapters1Delegate>(factory, 12);
                uint adapterIndex;
                for (adapterIndex = 0; ; adapterIndex++)
                {
                    IntPtr adapter;
                    result = enumAdapters(factory, adapterIndex, out adapter);
                    if (result == DxgiErrorNotFound)
                    {
                        break;
                    }
                    if (result < 0 || adapter == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        replaced += ReplaceAdapterOutputs(
                            adapter,
                            destination,
                            desktopBounds,
                            byDevice);
                    }
                    finally
                    {
                        Release(adapter);
                    }
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (ExternalException)
            {
            }
            finally
            {
                Release(factory);
            }
            return replaced;
        }

        internal static RecordingSession CreateRecordingSession(Rectangle captureBounds)
        {
            return new RecordingSession(
                captureBounds,
                HdrDisplayCompensation.DiscoverActiveAdjustments());
        }

        internal static bool CanReuseCachedFrame(long lastPresentTime, bool cacheValid)
        {
            // DXGI reports a zero LastPresentTime for pointer-only updates.
            // The recorder omits the pointer, so its converted pixels still apply.
            return cacheValid && lastPresentTime == 0;
        }

        private static int ReplaceAdapterOutputs(
            IntPtr adapter,
            Bitmap destination,
            Rectangle desktopBounds,
            Dictionary<string, HdrMonitorAdjustment> adjustments)
        {
            IntPtr device = IntPtr.Zero;
            IntPtr context = IntPtr.Zero;
            int featureLevel;
            int result = D3D11CreateDevice(
                adapter,
                D3dDriverTypeUnknown,
                IntPtr.Zero,
                D3d11CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                D3d11SdkVersion,
                out device,
                out featureLevel,
                out context);
            if (result < 0 || device == IntPtr.Zero || context == IntPtr.Zero)
            {
                Release(context);
                Release(device);
                return 0;
            }

            int replaced = 0;
            try
            {
                EnumOutputsDelegate enumOutputs = GetDelegate<EnumOutputsDelegate>(adapter, 7);
                uint outputIndex;
                for (outputIndex = 0; ; outputIndex++)
                {
                    IntPtr output;
                    result = enumOutputs(adapter, outputIndex, out output);
                    if (result == DxgiErrorNotFound)
                    {
                        break;
                    }
                    if (result < 0 || output == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        DxgiOutputDescription description;
                        GetOutputDescriptionDelegate getDescription =
                            GetDelegate<GetOutputDescriptionDelegate>(output, 7);
                        if (getDescription(output, out description) < 0 ||
                            !description.AttachedToDesktop)
                        {
                            continue;
                        }

                        HdrMonitorAdjustment adjustment;
                        if (!adjustments.TryGetValue(description.DeviceName, out adjustment))
                        {
                            continue;
                        }

                        if (TryReplaceOutput(
                            output,
                            device,
                            context,
                            description,
                            adjustment,
                            destination,
                            desktopBounds))
                        {
                            replaced++;
                        }
                    }
                    finally
                    {
                        Release(output);
                    }
                }
            }
            finally
            {
                Release(context);
                Release(device);
            }
            return replaced;
        }

        private static bool TryReplaceOutput(
            IntPtr output,
            IntPtr device,
            IntPtr context,
            DxgiOutputDescription outputDescription,
            HdrMonitorAdjustment adjustment,
            Bitmap destination,
            Rectangle desktopBounds)
        {
            IntPtr output5 = IntPtr.Zero;
            IntPtr duplication = IntPtr.Zero;
            IntPtr desktopResource = IntPtr.Zero;
            IntPtr desktopTexture = IntPtr.Zero;
            IntPtr stagingTexture = IntPtr.Zero;
            bool frameAcquired = false;
            bool mapped = false;
            try
            {
                Guid output5Id = IidDxgiOutput5;
                if (QueryInterface(output, ref output5Id, out output5) < 0 ||
                    output5 == IntPtr.Zero)
                {
                    return false;
                }

                int[] formats = new int[]
                {
                    DxgiFormatR16G16B16A16Float,
                    DxgiFormatB8G8R8A8Unorm
                };
                GCHandle formatsHandle = GCHandle.Alloc(formats, GCHandleType.Pinned);
                try
                {
                    DuplicateOutput1Delegate duplicateOutput =
                        GetDelegate<DuplicateOutput1Delegate>(output5, 26);
                    int result = duplicateOutput(
                        output5,
                        device,
                        0,
                        (uint)formats.Length,
                        formatsHandle.AddrOfPinnedObject(),
                        out duplication);
                    if (result < 0 || duplication == IntPtr.Zero)
                    {
                        return false;
                    }
                }
                finally
                {
                    formatsHandle.Free();
                }

                NativeMethods.TryDwmFlush();
                AcquireNextFrameDelegate acquire =
                    GetDelegate<AcquireNextFrameDelegate>(duplication, 8);
                ReleaseFrameDelegate releaseAcquiredFrame =
                    GetDelegate<ReleaseFrameDelegate>(duplication, 14);
                DxgiOutputDuplicationFrameInfo frameInfo =
                    default(DxgiOutputDuplicationFrameInfo);
                int frameAttempt;
                for (frameAttempt = 0; frameAttempt < 4; frameAttempt++)
                {
                    int acquireResult = acquire(
                        duplication,
                        100,
                        out frameInfo,
                        out desktopResource);
                    if (acquireResult < 0 || desktopResource == IntPtr.Zero)
                    {
                        return false;
                    }
                    frameAcquired = true;
                    if (frameInfo.LastPresentTime != 0)
                    {
                        break;
                    }

                    Release(desktopResource);
                    desktopResource = IntPtr.Zero;
                    releaseAcquiredFrame(duplication);
                    frameAcquired = false;
                }
                if (!frameAcquired || frameInfo.LastPresentTime == 0)
                {
                    return false;
                }

                Guid textureId = IidD3d11Texture2D;
                if (QueryInterface(desktopResource, ref textureId, out desktopTexture) < 0 ||
                    desktopTexture == IntPtr.Zero)
                {
                    return false;
                }

                D3d11Texture2DDescription textureDescription;
                GetTextureDescriptionDelegate getTextureDescription =
                    GetDelegate<GetTextureDescriptionDelegate>(desktopTexture, 10);
                getTextureDescription(desktopTexture, out textureDescription);
                if (textureDescription.Format != DxgiFormatR16G16B16A16Float ||
                    textureDescription.Width == 0 ||
                    textureDescription.Height == 0)
                {
                    return false;
                }

                D3d11Texture2DDescription stagingDescription = textureDescription;
                stagingDescription.MipLevels = 1;
                stagingDescription.ArraySize = 1;
                stagingDescription.Usage = D3d11UsageStaging;
                stagingDescription.BindFlags = 0;
                stagingDescription.CpuAccessFlags = D3d11CpuAccessRead;
                stagingDescription.MiscFlags = 0;

                CreateTexture2DDelegate createTexture =
                    GetDelegate<CreateTexture2DDelegate>(device, 5);
                int createResult = createTexture(
                    device,
                    ref stagingDescription,
                    IntPtr.Zero,
                    out stagingTexture);
                if (createResult < 0 || stagingTexture == IntPtr.Zero)
                {
                    return false;
                }

                CopyResourceDelegate copyResource =
                    GetDelegate<CopyResourceDelegate>(context, 47);
                copyResource(context, stagingTexture, desktopTexture);

                MapDelegate map = GetDelegate<MapDelegate>(context, 14);
                D3d11MappedSubresource mappedData;
                int mapResult = map(
                    context,
                    stagingTexture,
                    0,
                    D3d11MapRead,
                    0,
                    out mappedData);
                if (mapResult < 0 || mappedData.Data == IntPtr.Zero)
                {
                    return false;
                }
                mapped = true;

                Rectangle outputBounds = outputDescription.DesktopCoordinates.ToRectangle();
                Rectangle destinationRegion = Rectangle.Intersect(outputBounds, desktopBounds);
                if (destinationRegion.Width <= 0 || destinationRegion.Height <= 0)
                {
                    return false;
                }

                CopyFp16ToSdrBitmap(
                    mappedData,
                    textureDescription,
                    outputDescription.Rotation,
                    outputBounds,
                    destinationRegion,
                    desktopBounds,
                    adjustment.WhiteLevelMultiplier,
                    destination);
                return true;
            }
            finally
            {
                if (mapped && context != IntPtr.Zero && stagingTexture != IntPtr.Zero)
                {
                    UnmapDelegate unmap = GetDelegate<UnmapDelegate>(context, 15);
                    unmap(context, stagingTexture, 0);
                }
                Release(stagingTexture);
                Release(desktopTexture);
                Release(desktopResource);
                if (frameAcquired && duplication != IntPtr.Zero)
                {
                    ReleaseFrameDelegate releaseFrame =
                        GetDelegate<ReleaseFrameDelegate>(duplication, 14);
                    releaseFrame(duplication);
                }
                Release(duplication);
                Release(output5);
            }
        }

        private static unsafe void CopyFp16ToSdrBitmap(
            D3d11MappedSubresource source,
            D3d11Texture2DDescription sourceDescription,
            int rotation,
            Rectangle outputBounds,
            Rectangle destinationRegion,
            Rectangle desktopBounds,
            double whiteLevelMultiplier,
            Bitmap destination)
        {
            BitmapData destinationData = null;
            try
            {
                destinationData = destination.LockBits(
                    new Rectangle(0, 0, destination.Width, destination.Height),
                    ImageLockMode.ReadWrite,
                    PixelFormat.Format32bppRgb);

                float inverseWhite = (float)(1.0 / Math.Max(1.0, whiteLevelMultiplier));
                int screenY;
                for (screenY = destinationRegion.Top;
                     screenY < destinationRegion.Bottom;
                     screenY++)
                {
                    int destinationY = screenY - desktopBounds.Top;
                    int* destinationRow = (int*)((byte*)destinationData.Scan0 +
                        destinationY * destinationData.Stride);
                    int screenX;
                    for (screenX = destinationRegion.Left;
                         screenX < destinationRegion.Right;
                         screenX++)
                    {
                        int localX = screenX - outputBounds.Left;
                        int localY = screenY - outputBounds.Top;
                        int sourceX;
                        int sourceY;
                        MapRotatedCoordinate(
                            localX,
                            localY,
                            (int)sourceDescription.Width,
                            (int)sourceDescription.Height,
                            rotation,
                            out sourceX,
                            out sourceY);
                        if (sourceX < 0 || sourceY < 0 ||
                            sourceX >= sourceDescription.Width ||
                            sourceY >= sourceDescription.Height)
                        {
                            continue;
                        }

                        ushort* pixel = (ushort*)((byte*)source.Data +
                            sourceY * source.RowPitch +
                            sourceX * 8);
                        byte red = ScRgbToSrgbByte(HalfValues[pixel[0]] * inverseWhite);
                        byte green = ScRgbToSrgbByte(HalfValues[pixel[1]] * inverseWhite);
                        byte blue = ScRgbToSrgbByte(HalfValues[pixel[2]] * inverseWhite);
                        destinationRow[screenX - desktopBounds.Left] =
                            (red << 16) | (green << 8) | blue;
                    }
                }
            }
            finally
            {
                if (destinationData != null)
                {
                    destination.UnlockBits(destinationData);
                }
            }
        }

        /// <summary>
        /// Persistent FP16 desktop-duplication resources for the 60 FPS recorder.
        /// Each HDR output is acquired without rebuilding its D3D device or staging
        /// texture. When the compositor has not published a new frame yet, the last
        /// tone-mapped output region is reused instead of exposing GDI's bright HDR
        /// representation for one video frame.
        /// </summary>
        internal sealed class RecordingSession : IDisposable
        {
            private readonly Rectangle captureBounds;
            private readonly List<AdapterCapture> adapters;
            private bool disposed;

            internal RecordingSession(
                Rectangle bounds,
                IList<HdrMonitorAdjustment> adjustments)
            {
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    throw new ArgumentOutOfRangeException("bounds");
                }
                captureBounds = bounds;
                adapters = new List<AdapterCapture>();
                Initialize(adjustments);
            }

            internal int ActiveOutputCount
            {
                get
                {
                    int count = 0;
                    foreach (AdapterCapture adapter in adapters)
                    {
                        count += adapter.OutputCount;
                    }
                    return count;
                }
            }

            internal int ReplaceHdrPixels(Bitmap destination)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(typeof(RecordingSession).FullName);
                }
                if (destination == null)
                {
                    throw new ArgumentNullException("destination");
                }
                if (destination.Width != captureBounds.Width ||
                    destination.Height != captureBounds.Height ||
                    destination.PixelFormat != PixelFormat.Format32bppRgb)
                {
                    throw new ArgumentException(
                        "The recording bitmap must be a matching 32-bit RGB frame.",
                        "destination");
                }

                int replaced = 0;
                foreach (AdapterCapture adapter in adapters)
                {
                    replaced += adapter.ReplaceHdrPixels(destination, captureBounds);
                }
                return replaced;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                foreach (AdapterCapture adapter in adapters)
                {
                    adapter.Dispose();
                }
                adapters.Clear();
            }

            private void Initialize(IList<HdrMonitorAdjustment> adjustments)
            {
                if (adjustments == null || adjustments.Count == 0)
                {
                    return;
                }

                Dictionary<string, HdrMonitorAdjustment> byDevice =
                    new Dictionary<string, HdrMonitorAdjustment>(
                        StringComparer.OrdinalIgnoreCase);
                foreach (HdrMonitorAdjustment adjustment in adjustments)
                {
                    if (adjustment != null &&
                        adjustment.Bounds.IntersectsWith(captureBounds))
                    {
                        byDevice[adjustment.DeviceName] = adjustment;
                    }
                }
                if (byDevice.Count == 0)
                {
                    return;
                }

                IntPtr factory = IntPtr.Zero;
                try
                {
                    Guid factoryId = IidDxgiFactory1;
                    if (CreateDXGIFactory1(ref factoryId, out factory) < 0 ||
                        factory == IntPtr.Zero)
                    {
                        return;
                    }

                    EnumAdapters1Delegate enumerateAdapters =
                        GetDelegate<EnumAdapters1Delegate>(factory, 12);
                    uint adapterIndex;
                    for (adapterIndex = 0; ; adapterIndex++)
                    {
                        IntPtr adapterPointer;
                        int result = enumerateAdapters(
                            factory,
                            adapterIndex,
                            out adapterPointer);
                        if (result == DxgiErrorNotFound)
                        {
                            break;
                        }
                        if (result < 0 || adapterPointer == IntPtr.Zero)
                        {
                            continue;
                        }

                        try
                        {
                            AdapterCapture adapter = AdapterCapture.TryCreate(
                                adapterPointer,
                                captureBounds,
                                byDevice);
                            if (adapter != null)
                            {
                                adapters.Add(adapter);
                            }
                        }
                        finally
                        {
                            Release(adapterPointer);
                        }
                    }
                }
                catch (DllNotFoundException)
                {
                }
                catch (EntryPointNotFoundException)
                {
                }
                catch (ExternalException)
                {
                }
                finally
                {
                    Release(factory);
                }
            }
        }

        private sealed class AdapterCapture : IDisposable
        {
            private IntPtr device;
            private IntPtr context;
            private readonly List<OutputCapture> outputs;

            private AdapterCapture(IntPtr devicePointer, IntPtr contextPointer)
            {
                device = devicePointer;
                context = contextPointer;
                outputs = new List<OutputCapture>();
            }

            internal int OutputCount
            {
                get { return outputs.Count; }
            }

            internal static AdapterCapture TryCreate(
                IntPtr adapterPointer,
                Rectangle captureBounds,
                Dictionary<string, HdrMonitorAdjustment> adjustments)
            {
                IntPtr devicePointer = IntPtr.Zero;
                IntPtr contextPointer = IntPtr.Zero;
                int featureLevel;
                int result = D3D11CreateDevice(
                    adapterPointer,
                    D3dDriverTypeUnknown,
                    IntPtr.Zero,
                    D3d11CreateDeviceBgraSupport,
                    IntPtr.Zero,
                    0,
                    D3d11SdkVersion,
                    out devicePointer,
                    out featureLevel,
                    out contextPointer);
                if (result < 0 || devicePointer == IntPtr.Zero ||
                    contextPointer == IntPtr.Zero)
                {
                    Release(contextPointer);
                    Release(devicePointer);
                    return null;
                }

                AdapterCapture capture = new AdapterCapture(
                    devicePointer,
                    contextPointer);
                try
                {
                    EnumOutputsDelegate enumerateOutputs =
                        GetDelegate<EnumOutputsDelegate>(adapterPointer, 7);
                    uint outputIndex;
                    for (outputIndex = 0; ; outputIndex++)
                    {
                        IntPtr output;
                        result = enumerateOutputs(adapterPointer, outputIndex, out output);
                        if (result == DxgiErrorNotFound)
                        {
                            break;
                        }
                        if (result < 0 || output == IntPtr.Zero)
                        {
                            continue;
                        }

                        try
                        {
                            DxgiOutputDescription description;
                            GetOutputDescriptionDelegate getDescription =
                                GetDelegate<GetOutputDescriptionDelegate>(output, 7);
                            HdrMonitorAdjustment adjustment;
                            if (getDescription(output, out description) < 0 ||
                                !description.AttachedToDesktop ||
                                !adjustments.TryGetValue(
                                    description.DeviceName,
                                    out adjustment))
                            {
                                continue;
                            }

                            Rectangle outputBounds =
                                description.DesktopCoordinates.ToRectangle();
                            Rectangle region = Rectangle.Intersect(
                                outputBounds,
                                captureBounds);
                            if (region.Width <= 0 || region.Height <= 0)
                            {
                                continue;
                            }

                            IntPtr duplication = CreateDuplication(output, devicePointer);
                            if (duplication != IntPtr.Zero)
                            {
                                capture.outputs.Add(new OutputCapture(
                                    contextPointer,
                                    devicePointer,
                                    duplication,
                                    description,
                                    adjustment,
                                    region));
                            }
                        }
                        finally
                        {
                            Release(output);
                        }
                    }

                    if (capture.outputs.Count == 0)
                    {
                        capture.Dispose();
                        return null;
                    }
                    return capture;
                }
                catch
                {
                    capture.Dispose();
                    throw;
                }
            }

            internal int ReplaceHdrPixels(Bitmap destination, Rectangle captureBounds)
            {
                int replaced = 0;
                foreach (OutputCapture output in outputs)
                {
                    if (output.TryApply(destination, captureBounds))
                    {
                        replaced++;
                    }
                }
                return replaced;
            }

            public void Dispose()
            {
                foreach (OutputCapture output in outputs)
                {
                    output.Dispose();
                }
                outputs.Clear();
                Release(context);
                Release(device);
                context = IntPtr.Zero;
                device = IntPtr.Zero;
            }

            private static IntPtr CreateDuplication(IntPtr output, IntPtr device)
            {
                IntPtr output5 = IntPtr.Zero;
                try
                {
                    Guid output5Id = IidDxgiOutput5;
                    if (QueryInterface(output, ref output5Id, out output5) < 0 ||
                        output5 == IntPtr.Zero)
                    {
                        return IntPtr.Zero;
                    }

                    int[] formats = new int[]
                    {
                        DxgiFormatR16G16B16A16Float,
                        DxgiFormatB8G8R8A8Unorm
                    };
                    GCHandle formatsHandle = GCHandle.Alloc(
                        formats,
                        GCHandleType.Pinned);
                    try
                    {
                        IntPtr duplication;
                        DuplicateOutput1Delegate duplicate =
                            GetDelegate<DuplicateOutput1Delegate>(output5, 26);
                        int result = duplicate(
                            output5,
                            device,
                            0,
                            (uint)formats.Length,
                            formatsHandle.AddrOfPinnedObject(),
                            out duplication);
                        return result >= 0 ? duplication : IntPtr.Zero;
                    }
                    finally
                    {
                        formatsHandle.Free();
                    }
                }
                finally
                {
                    Release(output5);
                }
            }
        }

        private sealed class OutputCapture : IDisposable
        {
            private readonly IntPtr context;
            private readonly IntPtr device;
            private IntPtr duplication;
            private IntPtr stagingTexture;
            private D3d11Texture2DDescription stagingDescription;
            private readonly DxgiOutputDescription outputDescription;
            private readonly HdrMonitorAdjustment adjustment;
            private readonly Rectangle destinationRegion;
            private readonly int[] cachedPixels;
            private bool cacheValid;

            internal OutputCapture(
                IntPtr contextPointer,
                IntPtr devicePointer,
                IntPtr duplicationPointer,
                DxgiOutputDescription description,
                HdrMonitorAdjustment monitorAdjustment,
                Rectangle region)
            {
                context = contextPointer;
                device = devicePointer;
                duplication = duplicationPointer;
                outputDescription = description;
                adjustment = monitorAdjustment;
                destinationRegion = region;
                cachedPixels = new int[region.Width * region.Height];
            }

            internal bool TryApply(Bitmap destination, Rectangle captureBounds)
            {
                if (duplication == IntPtr.Zero)
                {
                    return ApplyCached(destination, captureBounds);
                }

                IntPtr desktopResource = IntPtr.Zero;
                IntPtr desktopTexture = IntPtr.Zero;
                bool frameAcquired = false;
                bool mapped = false;
                try
                {
                    AcquireNextFrameDelegate acquire =
                        GetDelegate<AcquireNextFrameDelegate>(duplication, 8);
                    DxgiOutputDuplicationFrameInfo frameInfo =
                        default(DxgiOutputDuplicationFrameInfo);
                    int result = acquire(
                        duplication,
                        cacheValid ? 0U : 12U,
                        out frameInfo,
                        out desktopResource);
                    if (result == DxgiErrorWaitTimeout ||
                        result < 0 || desktopResource == IntPtr.Zero)
                    {
                        return ApplyCached(destination, captureBounds);
                    }
                    frameAcquired = true;

                    if (CanReuseCachedFrame(frameInfo.LastPresentTime, cacheValid))
                    {
                        return ApplyCached(destination, captureBounds);
                    }

                    Guid textureId = IidD3d11Texture2D;
                    if (QueryInterface(
                        desktopResource,
                        ref textureId,
                        out desktopTexture) < 0 || desktopTexture == IntPtr.Zero)
                    {
                        return ApplyCached(destination, captureBounds);
                    }

                    D3d11Texture2DDescription description;
                    GetTextureDescriptionDelegate getDescription =
                        GetDelegate<GetTextureDescriptionDelegate>(desktopTexture, 10);
                    getDescription(desktopTexture, out description);
                    if (description.Format != DxgiFormatR16G16B16A16Float ||
                        description.Width == 0 || description.Height == 0 ||
                        !EnsureStagingTexture(description))
                    {
                        return ApplyCached(destination, captureBounds);
                    }

                    CopyResourceDelegate copy =
                        GetDelegate<CopyResourceDelegate>(context, 47);
                    copy(context, stagingTexture, desktopTexture);
                    MapDelegate map = GetDelegate<MapDelegate>(context, 14);
                    D3d11MappedSubresource mappedData;
                    if (map(
                        context,
                        stagingTexture,
                        0,
                        D3d11MapRead,
                        0,
                        out mappedData) < 0 || mappedData.Data == IntPtr.Zero)
                    {
                        return ApplyCached(destination, captureBounds);
                    }
                    mapped = true;

                    CopyFp16ToSdrBitmap(
                        mappedData,
                        description,
                        outputDescription.Rotation,
                        outputDescription.DesktopCoordinates.ToRectangle(),
                        destinationRegion,
                        captureBounds,
                        adjustment.WhiteLevelMultiplier,
                        destination);
                    UpdateCache(destination, captureBounds);
                    return true;
                }
                catch (ExternalException)
                {
                    return ApplyCached(destination, captureBounds);
                }
                finally
                {
                    if (mapped && stagingTexture != IntPtr.Zero)
                    {
                        UnmapDelegate unmap = GetDelegate<UnmapDelegate>(context, 15);
                        unmap(context, stagingTexture, 0);
                    }
                    Release(desktopTexture);
                    Release(desktopResource);
                    if (frameAcquired && duplication != IntPtr.Zero)
                    {
                        ReleaseFrameDelegate releaseFrame =
                            GetDelegate<ReleaseFrameDelegate>(duplication, 14);
                        releaseFrame(duplication);
                    }
                }
            }

            public void Dispose()
            {
                Release(stagingTexture);
                Release(duplication);
                stagingTexture = IntPtr.Zero;
                duplication = IntPtr.Zero;
            }

            private bool EnsureStagingTexture(D3d11Texture2DDescription description)
            {
                if (stagingTexture != IntPtr.Zero &&
                    stagingDescription.Width == description.Width &&
                    stagingDescription.Height == description.Height &&
                    stagingDescription.Format == description.Format)
                {
                    return true;
                }

                Release(stagingTexture);
                stagingTexture = IntPtr.Zero;
                stagingDescription = description;
                stagingDescription.MipLevels = 1;
                stagingDescription.ArraySize = 1;
                stagingDescription.Usage = D3d11UsageStaging;
                stagingDescription.BindFlags = 0;
                stagingDescription.CpuAccessFlags = D3d11CpuAccessRead;
                stagingDescription.MiscFlags = 0;
                CreateTexture2DDelegate create =
                    GetDelegate<CreateTexture2DDelegate>(device, 5);
                return create(
                    device,
                    ref stagingDescription,
                    IntPtr.Zero,
                    out stagingTexture) >= 0 && stagingTexture != IntPtr.Zero;
            }

            private unsafe void UpdateCache(Bitmap destination, Rectangle captureBounds)
            {
                Rectangle local = new Rectangle(
                    destinationRegion.Left - captureBounds.Left,
                    destinationRegion.Top - captureBounds.Top,
                    destinationRegion.Width,
                    destinationRegion.Height);
                BitmapData data = null;
                try
                {
                    data = destination.LockBits(
                        new Rectangle(0, 0, destination.Width, destination.Height),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format32bppRgb);
                    int y;
                    for (y = 0; y < local.Height; y++)
                    {
                        IntPtr row = IntPtr.Add(
                            data.Scan0,
                            (local.Top + y) * data.Stride + local.Left * 4);
                        Marshal.Copy(
                            row,
                            cachedPixels,
                            y * local.Width,
                            local.Width);
                    }
                    cacheValid = true;
                }
                finally
                {
                    if (data != null)
                    {
                        destination.UnlockBits(data);
                    }
                }
            }

            private bool ApplyCached(Bitmap destination, Rectangle captureBounds)
            {
                if (!cacheValid)
                {
                    return false;
                }

                Rectangle local = new Rectangle(
                    destinationRegion.Left - captureBounds.Left,
                    destinationRegion.Top - captureBounds.Top,
                    destinationRegion.Width,
                    destinationRegion.Height);
                BitmapData data = null;
                try
                {
                    data = destination.LockBits(
                        new Rectangle(0, 0, destination.Width, destination.Height),
                        ImageLockMode.WriteOnly,
                        PixelFormat.Format32bppRgb);
                    int y;
                    for (y = 0; y < local.Height; y++)
                    {
                        IntPtr row = IntPtr.Add(
                            data.Scan0,
                            (local.Top + y) * data.Stride + local.Left * 4);
                        Marshal.Copy(
                            cachedPixels,
                            y * local.Width,
                            row,
                            local.Width);
                    }
                    return true;
                }
                finally
                {
                    if (data != null)
                    {
                        destination.UnlockBits(data);
                    }
                }
            }
        }

        private static void MapRotatedCoordinate(
            int x,
            int y,
            int sourceWidth,
            int sourceHeight,
            int rotation,
            out int sourceX,
            out int sourceY)
        {
            // DXGI_MODE_ROTATION: 1 identity, 2 rotate 90, 3 rotate 180, 4 rotate 270.
            if (rotation == 2)
            {
                sourceX = y;
                sourceY = sourceHeight - 1 - x;
            }
            else if (rotation == 3)
            {
                sourceX = sourceWidth - 1 - x;
                sourceY = sourceHeight - 1 - y;
            }
            else if (rotation == 4)
            {
                sourceX = sourceWidth - 1 - y;
                sourceY = x;
            }
            else
            {
                sourceX = x;
                sourceY = y;
            }
        }

        internal static byte ScRgbToSrgbByte(float linear)
        {
            if (float.IsNaN(linear) || linear <= 0.0f)
            {
                return 0;
            }
            if (linear >= 1.0f)
            {
                return 255;
            }
            int index = (int)(linear * 65535.0f + 0.5f);
            return LinearToSrgbValues[Math.Max(0, Math.Min(65535, index))];
        }

        private static float[] CreateHalfLookup()
        {
            float[] result = new float[65536];
            int value;
            for (value = 0; value < result.Length; value++)
            {
                int sign = (value >> 15) & 1;
                int exponent = (value >> 10) & 0x1f;
                int mantissa = value & 0x3ff;
                double decoded;
                if (exponent == 0)
                {
                    decoded = mantissa == 0
                        ? 0.0
                        : Math.Pow(2.0, -14.0) * (mantissa / 1024.0);
                }
                else if (exponent == 31)
                {
                    decoded = mantissa == 0
                        ? double.PositiveInfinity
                        : double.NaN;
                }
                else
                {
                    decoded = Math.Pow(2.0, exponent - 15.0) *
                        (1.0 + mantissa / 1024.0);
                }
                result[value] = (float)(sign == 0 ? decoded : -decoded);
            }
            return result;
        }

        private static byte[] CreateLinearToSrgbLookup()
        {
            byte[] result = new byte[65536];
            int index;
            for (index = 0; index < result.Length; index++)
            {
                double linear = index / 65535.0;
                double encoded = linear <= 0.0031308
                    ? linear * 12.92
                    : (1.055 * Math.Pow(linear, 1.0 / 2.4)) - 0.055;
                result[index] = (byte)Math.Max(
                    0,
                    Math.Min(255, (int)Math.Round(encoded * 255.0)));
            }
            return result;
        }

        private static int QueryInterface(IntPtr instance, ref Guid interfaceId, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (instance == IntPtr.Zero)
            {
                return unchecked((int)0x80004003);
            }
            QueryInterfaceDelegate query = GetDelegate<QueryInterfaceDelegate>(instance, 0);
            return query(instance, ref interfaceId, out result);
        }

        private static void Release(IntPtr instance)
        {
            if (instance != IntPtr.Zero)
            {
                ReleaseDelegate release = GetDelegate<ReleaseDelegate>(instance, 2);
                release(instance);
            }
        }

        private static T GetDelegate<T>(IntPtr instance, int methodIndex)
            where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(instance);
            IntPtr function = Marshal.ReadIntPtr(table, methodIndex * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(function, typeof(T));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DxgiOutputDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            internal string DeviceName;
            internal NativeMethods.RECT DesktopCoordinates;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool AttachedToDesktop;
            internal int Rotation;
            internal IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DxgiOutputDuplicationPointerPosition
        {
            internal NativeMethods.POINT Position;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool Visible;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DxgiOutputDuplicationFrameInfo
        {
            internal long LastPresentTime;
            internal long LastMouseUpdateTime;
            internal uint AccumulatedFrames;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool RectsCoalesced;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool ProtectedContentMaskedOut;
            internal DxgiOutputDuplicationPointerPosition PointerPosition;
            internal uint TotalMetadataBufferSize;
            internal uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3d11Texture2DDescription
        {
            internal uint Width;
            internal uint Height;
            internal uint MipLevels;
            internal uint ArraySize;
            internal int Format;
            internal uint SampleCount;
            internal uint SampleQuality;
            internal int Usage;
            internal uint BindFlags;
            internal uint CpuAccessFlags;
            internal uint MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3d11MappedSubresource
        {
            internal IntPtr Data;
            internal uint RowPitch;
            internal uint DepthPitch;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryInterfaceDelegate(
            IntPtr instance,
            ref Guid interfaceId,
            out IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseDelegate(IntPtr instance);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1Delegate(
            IntPtr factory,
            uint adapterIndex,
            out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumOutputsDelegate(
            IntPtr adapter,
            uint outputIndex,
            out IntPtr output);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetOutputDescriptionDelegate(
            IntPtr output,
            out DxgiOutputDescription description);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DuplicateOutput1Delegate(
            IntPtr output,
            IntPtr device,
            uint flags,
            uint formatCount,
            IntPtr formats,
            out IntPtr duplication);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AcquireNextFrameDelegate(
            IntPtr duplication,
            uint timeoutMilliseconds,
            out DxgiOutputDuplicationFrameInfo frameInfo,
            out IntPtr desktopResource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseFrameDelegate(IntPtr duplication);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetTextureDescriptionDelegate(
            IntPtr texture,
            out D3d11Texture2DDescription description);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateTexture2DDelegate(
            IntPtr device,
            ref D3d11Texture2DDescription description,
            IntPtr initialData,
            out IntPtr texture);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void CopyResourceDelegate(
            IntPtr context,
            IntPtr destinationResource,
            IntPtr sourceResource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MapDelegate(
            IntPtr context,
            IntPtr resource,
            uint subresource,
            int mapType,
            uint mapFlags,
            out D3d11MappedSubresource mappedResource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void UnmapDelegate(
            IntPtr context,
            IntPtr resource,
            uint subresource);

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(
            ref Guid interfaceId,
            out IntPtr factory);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(
            IntPtr adapter,
            int driverType,
            IntPtr software,
            uint flags,
            IntPtr featureLevels,
            uint featureLevelCount,
            uint sdkVersion,
            out IntPtr device,
            out int featureLevel,
            out IntPtr immediateContext);
    }
}
