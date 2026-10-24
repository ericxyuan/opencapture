using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenCapture
{
    /// <summary>
    /// Describes the inverse SDR-white boost required for one active HDR output.
    /// Bounds are physical virtual-desktop pixels and may have a negative origin.
    /// </summary>
    internal sealed class HdrMonitorAdjustment
    {
        internal HdrMonitorAdjustment(
            string deviceName,
            Rectangle bounds,
            uint sdrWhiteLevel)
        {
            DeviceName = deviceName ?? string.Empty;
            Bounds = bounds;
            SdrWhiteLevel = sdrWhiteLevel;
        }

        internal string DeviceName { get; private set; }
        internal Rectangle Bounds { get; private set; }

        /// <summary>
        /// Windows expresses this as an 80-nit multiplier times 1000. Therefore
        /// 1000 is the neutral 80-nit SDR reference white and 2000 is 160 nits.
        /// </summary>
        internal uint SdrWhiteLevel { get; private set; }

        internal double WhiteLevelMultiplier
        {
            get { return Math.Max(1.0, SdrWhiteLevel / 1000.0); }
        }
    }

    /// <summary>
    /// Discovers which active outputs are actually in HDR mode and reads each
    /// output's Windows SDR-reference-white value.
    /// </summary>
    internal static class HdrDisplayCompensation
    {
        private const uint QdcOnlyActivePaths = 0x00000002;
        private const int ErrorSuccess = 0;
        private const int ErrorInsufficientBuffer = 122;
        private const int GetSourceName = 1;
        private const int GetAdvancedColorInfo = 9;
        private const int GetSdrWhiteLevel = 11;

        internal static IList<HdrMonitorAdjustment> DiscoverActiveAdjustments()
        {
            List<HdrMonitorAdjustment> adjustments = new List<HdrMonitorAdjustment>();
            try
            {
                Dictionary<string, Rectangle> monitorBounds = GetMonitorBoundsByDeviceName();
                if (monitorBounds.Count == 0)
                {
                    return adjustments.AsReadOnly();
                }

                DisplayConfigPathInfo[] paths;
                if (!TryGetActivePaths(out paths))
                {
                    return adjustments.AsReadOnly();
                }

                HashSet<string> addedDevices = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (DisplayConfigPathInfo path in paths)
                {
                    string deviceName;
                    if (!TryGetSourceDeviceName(path.SourceInfo, out deviceName) ||
                        string.IsNullOrEmpty(deviceName) ||
                        addedDevices.Contains(deviceName))
                    {
                        continue;
                    }

                    Rectangle bounds;
                    if (!monitorBounds.TryGetValue(deviceName, out bounds))
                    {
                        continue;
                    }

                    bool hdrEnabled;
                    if (!TryGetHdrEnabled(path.TargetInfo, out hdrEnabled) || !hdrEnabled)
                    {
                        continue;
                    }

                    uint whiteLevel;
                    if (!TryGetSdrWhiteLevel(path.TargetInfo, out whiteLevel) ||
                        whiteLevel == 0)
                    {
                        continue;
                    }

                    // 1000 is the valid 80-nit reference white. HDR still needs
                    // native FP16 capture when no SDR white boost is configured.
                    adjustments.Add(new HdrMonitorAdjustment(
                        deviceName,
                        bounds,
                        whiteLevel));
                    addedDevices.Add(deviceName);
                }
            }
            catch (DllNotFoundException)
            {
                // DisplayConfig is unavailable on older Windows versions.
            }
            catch (EntryPointNotFoundException)
            {
                // The capture remains valid and unmodified when the HDR APIs are absent.
            }
            catch (ExternalException)
            {
                // A display can be disconnected while its configuration is queried.
            }

            return adjustments.AsReadOnly();
        }

        private static Dictionary<string, Rectangle> GetMonitorBoundsByDeviceName()
        {
            Dictionary<string, Rectangle> result = new Dictionary<string, Rectangle>(
                StringComparer.OrdinalIgnoreCase);
            foreach (IntPtr monitor in NativeMethods.EnumerateMonitors())
            {
                NativeMethods.MONITORINFOEX info;
                if (NativeMethods.TryGetMonitorInfo(monitor, out info) &&
                    !string.IsNullOrEmpty(info.szDevice))
                {
                    result[info.szDevice] = info.rcMonitor.ToRectangle();
                }
            }
            return result;
        }

        private static bool TryGetActivePaths(out DisplayConfigPathInfo[] paths)
        {
            paths = null;
            int attempt;
            for (attempt = 0; attempt < 4; attempt++)
            {
                uint pathCount;
                uint modeCount;
                int result = GetDisplayConfigBufferSizes(
                    QdcOnlyActivePaths,
                    out pathCount,
                    out modeCount);
                if (result != ErrorSuccess || pathCount == 0)
                {
                    return false;
                }

                DisplayConfigPathInfo[] candidatePaths =
                    new DisplayConfigPathInfo[pathCount];
                DisplayConfigModeInfo[] modes =
                    new DisplayConfigModeInfo[modeCount];
                result = QueryDisplayConfig(
                    QdcOnlyActivePaths,
                    ref pathCount,
                    candidatePaths,
                    ref modeCount,
                    modes,
                    IntPtr.Zero);
                if (result == ErrorInsufficientBuffer)
                {
                    continue;
                }
                if (result != ErrorSuccess)
                {
                    return false;
                }

                if (pathCount != candidatePaths.Length)
                {
                    Array.Resize(ref candidatePaths, (int)pathCount);
                }
                paths = candidatePaths;
                return true;
            }
            return false;
        }

        private static bool TryGetSourceDeviceName(
            DisplayConfigPathSourceInfo source,
            out string deviceName)
        {
            DisplayConfigSourceDeviceName request = new DisplayConfigSourceDeviceName();
            request.Header = DisplayConfigDeviceInfoHeader.Create(
                GetSourceName,
                typeof(DisplayConfigSourceDeviceName),
                source.AdapterId,
                source.Id);
            request.ViewGdiDeviceName = string.Empty;

            int result = DisplayConfigGetSourceDeviceInfo(ref request);
            deviceName = result == ErrorSuccess
                ? request.ViewGdiDeviceName
                : string.Empty;
            return result == ErrorSuccess;
        }

        private static bool TryGetHdrEnabled(
            DisplayConfigPathTargetInfo target,
            out bool enabled)
        {
            DisplayConfigAdvancedColorInfo request = new DisplayConfigAdvancedColorInfo();
            request.Header = DisplayConfigDeviceInfoHeader.Create(
                GetAdvancedColorInfo,
                typeof(DisplayConfigAdvancedColorInfo),
                target.AdapterId,
                target.Id);

            int result = DisplayConfigGetAdvancedColorInfo(ref request);
            // bit 0 = supported, bit 1 = enabled. Requiring both avoids applying
            // the HDR curve to an HDR-capable display while HDR is switched off.
            enabled = result == ErrorSuccess &&
                (request.Value & 0x00000003U) == 0x00000003U;
            return result == ErrorSuccess;
        }

        private static bool TryGetSdrWhiteLevel(
            DisplayConfigPathTargetInfo target,
            out uint whiteLevel)
        {
            DisplayConfigSdrWhiteLevel request = new DisplayConfigSdrWhiteLevel();
            request.Header = DisplayConfigDeviceInfoHeader.Create(
                GetSdrWhiteLevel,
                typeof(DisplayConfigSdrWhiteLevel),
                target.AdapterId,
                target.Id);

            int result = DisplayConfigGetSdrWhiteLevelInfo(ref request);
            whiteLevel = result == ErrorSuccess ? request.SdrWhiteLevel : 0;
            return result == ErrorSuccess;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            internal uint LowPart;
            internal int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigRational
        {
            internal uint Numerator;
            internal uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigPathSourceInfo
        {
            internal Luid AdapterId;
            internal uint Id;
            internal uint ModeInfoIdx;
            internal uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigPathTargetInfo
        {
            internal Luid AdapterId;
            internal uint Id;
            internal uint ModeInfoIdx;
            internal int OutputTechnology;
            internal int Rotation;
            internal int Scaling;
            internal DisplayConfigRational RefreshRate;
            internal int ScanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool TargetAvailable;
            internal uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigPathInfo
        {
            internal DisplayConfigPathSourceInfo SourceInfo;
            internal DisplayConfigPathTargetInfo TargetInfo;
            internal uint Flags;
        }

        // The largest member of DISPLAYCONFIG_MODE_INFO's native union is 48
        // bytes. QueryDisplayConfig writes these records even though OpenCapture
        // only needs the source/target IDs from the path array.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct DisplayConfigModeInfo
        {
            [FieldOffset(0)]
            internal int InfoType;
            [FieldOffset(4)]
            internal uint Id;
            [FieldOffset(8)]
            internal Luid AdapterId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigDeviceInfoHeader
        {
            internal int Type;
            internal uint Size;
            internal Luid AdapterId;
            internal uint Id;

            internal static DisplayConfigDeviceInfoHeader Create(
                int type,
                Type structureType,
                Luid adapterId,
                uint id)
            {
                DisplayConfigDeviceInfoHeader header = new DisplayConfigDeviceInfoHeader();
                header.Type = type;
                header.Size = (uint)Marshal.SizeOf(structureType);
                header.AdapterId = adapterId;
                header.Id = id;
                return header;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayConfigSourceDeviceName
        {
            internal DisplayConfigDeviceInfoHeader Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            internal string ViewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigAdvancedColorInfo
        {
            internal DisplayConfigDeviceInfoHeader Header;
            internal uint Value;
            internal int ColorEncoding;
            internal uint BitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigSdrWhiteLevel
        {
            internal DisplayConfigDeviceInfoHeader Header;
            internal uint SdrWhiteLevel;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint pathCount,
            out uint modeCount);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint pathCount,
            [Out] DisplayConfigPathInfo[] paths,
            ref uint modeCount,
            [Out] DisplayConfigModeInfo[] modes,
            IntPtr currentTopologyId);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int DisplayConfigGetSourceDeviceInfo(
            ref DisplayConfigSourceDeviceName request);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int DisplayConfigGetAdvancedColorInfo(
            ref DisplayConfigAdvancedColorInfo request);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int DisplayConfigGetSdrWhiteLevelInfo(
            ref DisplayConfigSdrWhiteLevel request);
    }
}
