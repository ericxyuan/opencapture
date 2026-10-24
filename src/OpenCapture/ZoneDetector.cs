using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace OpenCapture
{
    public enum ZoneKind
    {
        Monitor,
        TopLevelWindow,
        ChildWindow,
        ShellElement,
        ColorBlock
    }

    /// <summary>
    /// An immutable capture target expressed in physical virtual-desktop pixels.
    /// Bounds use the usual half-open Rectangle convention: right and bottom are
    /// not part of the zone.
    /// </summary>
    public sealed class Zone
    {
        private readonly Rectangle _bounds;
        private readonly ZoneKind _kind;
        private readonly IntPtr _windowHandle;
        private readonly IntPtr _topLevelWindowHandle;
        private readonly int _depth;

        internal Zone(
            Rectangle bounds,
            ZoneKind kind,
            IntPtr windowHandle,
            IntPtr topLevelWindowHandle,
            int depth)
        {
            _bounds = bounds;
            _kind = kind;
            _windowHandle = windowHandle;
            _topLevelWindowHandle = topLevelWindowHandle;
            _depth = depth;
        }

        public Rectangle Bounds
        {
            get { return _bounds; }
        }

        public ZoneKind Kind
        {
            get { return _kind; }
        }

        public IntPtr WindowHandle
        {
            get { return _windowHandle; }
        }

        public IntPtr TopLevelWindowHandle
        {
            get { return _topLevelWindowHandle; }
        }

        /// <summary>
        /// Zero for a monitor or top-level window, otherwise the child-window
        /// distance from its top-level ancestor (capped to guard corrupt trees).
        /// </summary>
        public int Depth
        {
            get { return _depth; }
        }

        public override string ToString()
        {
            return string.Format(
                "{0}: {1},{2} {3}x{4}",
                _kind,
                _bounds.Left,
                _bounds.Top,
                _bounds.Width,
                _bounds.Height);
        }
    }

    /// <summary>
    /// Takes a pre-overlay snapshot of top-level window geometry and z-order.
    /// Child hit tests start from the selected cached HWND, rather than using a
    /// global hit test that could see OpenCapture's overlay. A new instance should
    /// be created for each capture session before any overlay window is shown.
    /// </summary>
    public sealed class ZoneDetector : IDisposable
    {
        private const int MinimumChildDimension = 8;
        private const int MaximumParentWalk = 64;

        private readonly Rectangle _virtualBounds;
        private readonly ReadOnlyCollection<Zone> _monitorZones;
        private readonly ReadOnlyCollection<WindowHierarchy> _windowHierarchies;
        private readonly ReadOnlyCollection<Zone> _zones;
        private readonly ShellZoneProvider _shellZones;
        private readonly ApplicationZoneProvider _applicationZones;
        private readonly ReadOnlyCollection<ShellZone> _prewarmedShellZones;
        private readonly VisualZoneDetector _visualZones;
        private bool _hasAppliedCompletedShellSnapshot;
        private bool _disposed;

        /// <summary>
        /// Captures the current window/monitor geometry and excludes this process.
        /// Construct this object before displaying OpenCapture's overlay windows.
        /// </summary>
        public ZoneDetector()
            : this(Process.GetCurrentProcess().Id, null, null)
        {
        }

        /// <summary>
        /// Captures the current geometry, excluding windows owned by the supplied
        /// process. This overload is also useful to deterministic test harnesses.
        /// </summary>
        public ZoneDetector(int excludedProcessId)
            : this(excludedProcessId, null, null)
        {
        }

        private ZoneDetector(
            int excludedProcessId,
            CapturedFrame frame,
            IList<ShellZone> prewarmedShellZones)
        {
            List<Zone> monitors = CaptureMonitors();
            _virtualBounds = CalculateVirtualBounds(monitors);
            _monitorZones = new ReadOnlyCollection<Zone>(monitors.ToArray());

            List<WindowHierarchy> hierarchies = CaptureWindowHierarchies(
                excludedProcessId,
                _virtualBounds);
            _windowHierarchies = new ReadOnlyCollection<WindowHierarchy>(
                hierarchies.ToArray());

            List<Zone> allZones = new List<Zone>();
            int hierarchyIndex;
            for (hierarchyIndex = 0; hierarchyIndex < hierarchies.Count; hierarchyIndex++)
            {
                WindowHierarchy hierarchy = hierarchies[hierarchyIndex];
                allZones.Add(hierarchy.TopLevel);
            }

            allZones.AddRange(monitors);
            _zones = new ReadOnlyCollection<Zone>(allZones.ToArray());
            _prewarmedShellZones = prewarmedShellZones == null
                ? new ReadOnlyCollection<ShellZone>(new ShellZone[0])
                : new ReadOnlyCollection<ShellZone>(
                    new List<ShellZone>(prewarmedShellZones).ToArray());

            try
            {
                _shellZones = ShellZoneProvider.Capture(excludedProcessId, 900);
            }
            catch
            {
                _shellZones = null;
            }

            _applicationZones = new ApplicationZoneProvider();

            if (frame != null)
            {
                try
                {
                    _visualZones = new VisualZoneDetector(frame);
                }
                catch
                {
                    _visualZones = null;
                }
            }

            Point initialPointer;
            if (NativeMethods.TryGetPhysicalCursorPosition(out initialPointer))
            {
                Zone initialFallback = FindWindowOrMonitorAt(initialPointer);
                StartApplicationZoneScan(initialFallback);
            }
        }

        public Rectangle VirtualBounds
        {
            get { return _virtualBounds; }
        }

        /// <summary>
        /// All pre-overlay snapshot zones. Top-level windows are ordered
        /// front-to-back and monitors are last. Child HWNDs are resolved within
        /// the already-selected underlying hierarchy by FindZoneAt.
        /// </summary>
        public ReadOnlyCollection<Zone> Zones
        {
            get { return _zones; }
        }

        public static ZoneDetector Capture()
        {
            return new ZoneDetector();
        }

        internal static ZoneDetector Capture(CapturedFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException("frame");
            }

            return new ZoneDetector(Process.GetCurrentProcess().Id, frame, null);
        }

        internal static ZoneDetector Capture(
            CapturedFrame frame,
            IList<ShellZone> prewarmedShellZones)
        {
            if (frame == null)
            {
                throw new ArgumentNullException("frame");
            }

            return new ZoneDetector(
                Process.GetCurrentProcess().Id,
                frame,
                prewarmedShellZones);
        }

        internal bool IsRefinementComplete
        {
            get
            {
                // Do not let the overlay stop polling in the small interval
                // between the provider publishing completion and FindZoneAt
                // actually consuming that completed snapshot.
                bool shellComplete = _shellZones == null ||
                    (_shellZones.IsComplete && _hasAppliedCompletedShellSnapshot);
                return shellComplete &&
                    (_applicationZones == null || _applicationZones.IsComplete);
            }
        }

        /// <summary>
        /// Returns the smallest meaningful child window belonging to the
        /// frontmost top-level window at the point. If none exists, returns that
        /// top-level window; if no window contains the point, returns its monitor.
        /// </summary>
        public Zone FindZoneAt(Point screenPoint)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(typeof(ZoneDetector).FullName);
            }

            if (!IsPointOnMonitor(screenPoint))
            {
                return null;
            }

            Zone fallback = FindWindowOrMonitorAt(screenPoint);
            if (fallback == null)
            {
                return null;
            }

            // Shell/UIA and frozen-pixel refinements overlap frequently. In
            // particular, the taskbar publishes its full root and notification
            // area immediately, while individual buttons arrive later. Do not let
            // the first broad semantic match short-circuit a smaller child or
            // pixel-derived target.
            Zone best = fallback;
            Zone actionableApplicationZone = null;
            StartApplicationZoneScan(fallback);

            if (_applicationZones != null &&
                fallback.TopLevelWindowHandle != IntPtr.Zero)
            {
                Rectangle topLevelBounds = FindTopLevelBounds(
                    fallback.TopLevelWindowHandle);
                IList<ApplicationZone> applicationCandidates =
                    _applicationZones.FindZonesAt(
                        fallback.TopLevelWindowHandle,
                        topLevelBounds,
                        screenPoint);
                int applicationIndex;
                for (applicationIndex = 0;
                     applicationIndex < applicationCandidates.Count;
                     applicationIndex++)
                {
                    ApplicationZone applicationCandidate =
                        applicationCandidates[applicationIndex];
                    Rectangle applicationBounds = Rectangle.Intersect(
                        applicationCandidate.Bounds,
                        _virtualBounds);
                    if (applicationBounds.Width <= 0 ||
                        applicationBounds.Height <= 0 ||
                        !applicationBounds.Contains(screenPoint))
                    {
                        continue;
                    }

                    if (!applicationCandidate.IsActionable)
                    {
                        Rectangle refinedBounds;
                        if (_visualZones == null ||
                            !_visualZones.TryValidateCandidate(
                                screenPoint,
                                applicationBounds,
                                topLevelBounds,
                                out refinedBounds))
                        {
                            continue;
                        }
                        applicationBounds = refinedBounds;
                    }

                    Zone applicationZone = new Zone(
                        applicationBounds,
                        ZoneKind.ShellElement,
                        fallback.TopLevelWindowHandle,
                        fallback.TopLevelWindowHandle,
                        fallback.Depth + 1);
                    if (applicationCandidate.IsActionable)
                    {
                        actionableApplicationZone = ChooseSmallerZone(
                            actionableApplicationZone,
                            applicationZone);
                    }
                    best = ChooseSmallerZone(best, applicationZone);
                }
            }

            Zone semanticZone = null;
            ShellZone semantic = FindSmallestLiveShellZoneAt(screenPoint, fallback);
            if (semantic != null)
            {
                Rectangle semanticBounds;
                if (TryGetLiveShellZoneBounds(
                    semantic,
                    fallback,
                    screenPoint,
                    out semanticBounds))
                {
                    semanticZone = new Zone(
                        semanticBounds,
                        ZoneKind.ShellElement,
                        semantic.RootWindowHandle,
                        semantic.RootWindowHandle,
                        fallback.Depth + 1);
                    best = ChooseSmallerZone(best, semanticZone);
                }
            }

            if (_visualZones != null)
            {
                Rectangle visualBounds;
                if (_visualZones.TryFindZoneAt(
                    screenPoint,
                    fallback.Bounds,
                    out visualBounds))
                {
                    visualBounds = Rectangle.Intersect(visualBounds, fallback.Bounds);
                    visualBounds = Rectangle.Intersect(visualBounds, _virtualBounds);
                    if (visualBounds.Width > 0 &&
                        visualBounds.Height > 0 &&
                        visualBounds.Contains(screenPoint) &&
                        visualBounds != fallback.Bounds)
                    {
                        Zone visualZone = new Zone(
                            visualBounds,
                            ZoneKind.ColorBlock,
                            fallback.WindowHandle,
                            fallback.TopLevelWindowHandle,
                            fallback.Depth + 1);

                        // A high-specificity UIA leaf (for example a taskbar app
                        // button or tray icon) is the actionable target. A colour
                        // block wholly inside it is normally just the glyph/fill,
                        // not a separate screenshot zone. Broad shell containers
                        // have lower specificity and remain eligible to lose to the
                        // smaller frozen-pixel candidate.
                        bool visualReplacesApplicationControl =
                            actionableApplicationZone == null ||
                            !actionableApplicationZone.Bounds.Contains(visualBounds);
                        bool visualReplacesShellControl =
                            !ReferenceEquals(best, semanticZone) ||
                            !IsActionableShellZone(semantic) ||
                            !semanticZone.Bounds.Contains(visualBounds);
                        if (visualReplacesApplicationControl &&
                            visualReplacesShellControl)
                        {
                            best = ChooseSmallerZone(best, visualZone);
                        }
                    }
                }
            }

            return best;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_shellZones != null)
            {
                _shellZones.Dispose();
            }
            if (_visualZones != null)
            {
                _visualZones.Dispose();
            }
            if (_applicationZones != null)
            {
                _applicationZones.Dispose();
            }
        }

        private void StartApplicationZoneScan(Zone fallback)
        {
            if (_applicationZones == null ||
                fallback == null ||
                fallback.TopLevelWindowHandle == IntPtr.Zero)
            {
                return;
            }

            Rectangle topLevelBounds = FindTopLevelBounds(
                fallback.TopLevelWindowHandle);
            _applicationZones.StartScan(
                fallback.TopLevelWindowHandle,
                topLevelBounds);
        }

        private Rectangle FindTopLevelBounds(IntPtr topLevelWindow)
        {
            int index;
            for (index = 0; index < _windowHierarchies.Count; index++)
            {
                Zone topLevel = _windowHierarchies[index].TopLevel;
                if (topLevel.WindowHandle == topLevelWindow)
                {
                    return topLevel.Bounds;
                }
            }
            return Rectangle.Empty;
        }

        private Zone FindWindowOrMonitorAt(Point screenPoint)
        {

            int hierarchyIndex;
            for (hierarchyIndex = 0;
                 hierarchyIndex < _windowHierarchies.Count;
                 hierarchyIndex++)
            {
                WindowHierarchy hierarchy = _windowHierarchies[hierarchyIndex];
                if (!hierarchy.TopLevel.Bounds.Contains(screenPoint))
                {
                    continue;
                }

                Zone child = FindSmallestChildAt(hierarchy.TopLevel, screenPoint);
                return child ?? hierarchy.TopLevel;
            }

            int monitorIndex;
            for (monitorIndex = 0; monitorIndex < _monitorZones.Count; monitorIndex++)
            {
                Zone monitor = _monitorZones[monitorIndex];
                if (monitor.Bounds.Contains(screenPoint))
                {
                    return monitor;
                }
            }

            return null;
        }

        private ShellZone FindSmallestLiveShellZoneAt(
            Point screenPoint,
            Zone fallback)
        {
            ShellZone currentBest = null;
            if (_shellZones != null)
            {
                // Read completion before the snapshot. When true, the volatile
                // read also makes the provider's last published snapshot visible.
                bool currentSnapshotComplete = _shellZones.IsComplete;
                IList<ShellZone> currentSnapshot = _shellZones.Snapshot;
                bool currentActionableFound;
                currentBest = FindSmallestLiveShellZoneAt(
                    currentSnapshot,
                    screenPoint,
                    fallback,
                    null,
                    out currentActionableFound);

                // Current-session UIA geometry is authoritative once discovery
                // completes. Comparing it by area with a prewarmed rectangle is
                // unsafe: taskbar/tray items can move while their root HWND and
                // process remain unchanged, making an old, smaller leaf appear
                // live. A freshly discovered actionable leaf at this point is
                // likewise stronger evidence even while unrelated roots finish.
                if (currentSnapshotComplete || currentActionableFound)
                {
                    if (currentSnapshotComplete)
                    {
                        _hasAppliedCompletedShellSnapshot = true;
                    }
                    return currentBest;
                }
            }

            ShellZone best = FindSmallestLivePrewarmedShellZoneAt(
                _prewarmedShellZones,
                screenPoint,
                fallback,
                currentBest);

            // Completion may have raced the first read. Re-read and replace the
            // tentative prewarmed result before returning. IsRefinementComplete
            // remains false until this path (or the completed path above) runs,
            // so the overlay cannot retain a stale final highlight.
            if (_shellZones != null && _shellZones.IsComplete)
            {
                bool ignoredActionable;
                currentBest = FindSmallestLiveShellZoneAt(
                    _shellZones.Snapshot,
                    screenPoint,
                    fallback,
                    null,
                    out ignoredActionable);
                _hasAppliedCompletedShellSnapshot = true;
                return currentBest;
            }

            return best;
        }

        private ShellZone FindSmallestLivePrewarmedShellZoneAt(
            IList<ShellZone> candidates,
            Point screenPoint,
            Zone fallback,
            ShellZone best)
        {
            int index;
            for (index = 0; index < candidates.Count; index++)
            {
                ShellZone candidate = candidates[index];
                Rectangle liveBounds;
                if (!TryGetLiveShellZoneBounds(
                    candidate,
                    fallback,
                    screenPoint,
                    out liveBounds))
                {
                    continue;
                }

                ShellZone scoredCandidate = candidate;
                if (IsActionableShellZone(candidate))
                {
                    // Root HWND/PID validation cannot prove that a cached UIA
                    // child rectangle stayed put. During the short prewarm-only
                    // interval, require independent evidence from the immutable
                    // captured pixels before trusting an actionable leaf.
                    Rectangle refinedBounds;
                    bool visuallyValidated = false;
                    if (_visualZones != null)
                    {
                        try
                        {
                            visuallyValidated = _visualZones.TryValidateCandidate(
                                screenPoint,
                                liveBounds,
                                fallback.Bounds,
                                out refinedBounds);
                        }
                        catch
                        {
                            refinedBounds = Rectangle.Empty;
                        }
                    }
                    else
                    {
                        refinedBounds = Rectangle.Empty;
                    }

                    refinedBounds = Rectangle.Intersect(refinedBounds, fallback.Bounds);
                    refinedBounds = Rectangle.Intersect(refinedBounds, _virtualBounds);
                    if (!visuallyValidated ||
                        refinedBounds.Width <= 0 ||
                        refinedBounds.Height <= 0 ||
                        !refinedBounds.Contains(screenPoint))
                    {
                        continue;
                    }

                    if (refinedBounds != candidate.Bounds)
                    {
                        scoredCandidate = new ShellZone(
                            refinedBounds,
                            candidate.Kind,
                            candidate.Name,
                            candidate.AutomationId,
                            candidate.ClassName,
                            candidate.ProcessId,
                            candidate.RootWindowHandle,
                            candidate.Specificity);
                    }
                }

                best = ChooseSmallerShellZone(best, scoredCandidate);
            }
            return best;
        }

        private static ShellZone FindSmallestLiveShellZoneAt(
            IList<ShellZone> candidates,
            Point screenPoint,
            Zone fallback,
            ShellZone best,
            out bool actionableFound)
        {
            actionableFound = false;
            int index;
            for (index = 0; index < candidates.Count; index++)
            {
                ShellZone candidate = candidates[index];
                Rectangle liveBounds;
                if (!TryGetLiveShellZoneBounds(
                    candidate,
                    fallback,
                    screenPoint,
                    out liveBounds))
                {
                    continue;
                }

                if (IsActionableShellZone(candidate))
                {
                    actionableFound = true;
                }
                best = ChooseSmallerShellZone(best, candidate);
            }
            return best;
        }

        private static bool TryGetLiveShellZoneBounds(
            ShellZone shellZone,
            Zone fallback,
            Point screenPoint,
            out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (shellZone == null ||
                shellZone.RootWindowHandle == IntPtr.Zero ||
                fallback.TopLevelWindowHandle == IntPtr.Zero ||
                shellZone.RootWindowHandle != fallback.TopLevelWindowHandle ||
                !shellZone.Bounds.Contains(screenPoint))
            {
                return false;
            }

            try
            {
                IntPtr root = shellZone.RootWindowHandle;
                if (!NativeMethods.IsWindow(root) ||
                    !NativeMethods.IsWindowVisible(root) ||
                    NativeMethods.IsIconic(root) ||
                    NativeMethods.IsWindowCloaked(root))
                {
                    return false;
                }

                uint currentProcessId;
                NativeMethods.GetWindowThreadProcessId(root, out currentProcessId);
                if (shellZone.ProcessId != 0 &&
                    currentProcessId != 0 &&
                    currentProcessId != (uint)shellZone.ProcessId)
                {
                    return false;
                }

                Rectangle rootBounds;
                if (!NativeMethods.TryGetExtendedFrameBounds(root, out rootBounds) ||
                    !rootBounds.Contains(screenPoint))
                {
                    return false;
                }

                bounds = Rectangle.Intersect(shellZone.Bounds, rootBounds);
                return bounds.Width > 0 &&
                    bounds.Height > 0 &&
                    bounds.Contains(screenPoint);
            }
            catch
            {
                // Foreign shell HWNDs can disappear or be recycled while the
                // overlay is open. A stale cache entry must never displace the
                // still-safe child/window/monitor fallback.
                bounds = Rectangle.Empty;
                return false;
            }
        }

        private static ShellZone ChooseSmallerShellZone(
            ShellZone first,
            ShellZone second)
        {
            if (first == null)
            {
                return second;
            }
            if (second == null)
            {
                return first;
            }

            long firstArea = Area(first.Bounds);
            long secondArea = Area(second.Bounds);
            if (secondArea < firstArea ||
                (secondArea == firstArea && second.Specificity > first.Specificity))
            {
                return second;
            }
            return first;
        }

        private static Zone ChooseSmallerZone(Zone first, Zone second)
        {
            if (first == null)
            {
                return second;
            }
            if (second == null)
            {
                return first;
            }

            long firstArea = Area(first.Bounds);
            long secondArea = Area(second.Bounds);
            if (secondArea < firstArea ||
                (secondArea == firstArea &&
                 ZoneSpecificity(second.Kind) > ZoneSpecificity(first.Kind)))
            {
                return second;
            }
            return first;
        }

        private static bool IsActionableShellZone(ShellZone shellZone)
        {
            // ShellZoneProvider assigns 100+ only to actionable controls; roots
            // and layout containers intentionally remain below this boundary.
            return shellZone != null && shellZone.Specificity >= 100;
        }

        private static int ZoneSpecificity(ZoneKind kind)
        {
            if (kind == ZoneKind.ShellElement)
            {
                return 4;
            }
            if (kind == ZoneKind.ColorBlock)
            {
                return 3;
            }
            if (kind == ZoneKind.ChildWindow)
            {
                return 2;
            }
            if (kind == ZoneKind.TopLevelWindow)
            {
                return 1;
            }
            return 0;
        }

        private bool IsPointOnMonitor(Point screenPoint)
        {
            int index;
            for (index = 0; index < _monitorZones.Count; index++)
            {
                if (_monitorZones[index].Bounds.Contains(screenPoint))
                {
                    return true;
                }
            }

            return false;
        }

        private Zone FindSmallestChildAt(Zone topLevel, Point screenPoint)
        {
            Zone best = null;
            long bestArea = long.MaxValue;
            int bestDepth = -1;
            int depth = 0;
            IntPtr current = topLevel.WindowHandle;
            HashSet<IntPtr> visited = new HashSet<IntPtr>();
            visited.Add(current);

            // ChildWindowFromPointEx only searches children of the supplied HWND.
            // Starting from the cached underlying top-level window therefore
            // cannot accidentally hit OpenCapture's unrelated overlay HWND.
            while (current != IntPtr.Zero && depth < MaximumParentWalk)
            {
                NativeMethods.POINT clientPoint = new NativeMethods.POINT(screenPoint);
                IntPtr child;

                try
                {
                    if (!NativeMethods.ScreenToClient(current, ref clientPoint))
                    {
                        break;
                    }

                    child = NativeMethods.ChildWindowFromPointEx(
                        current,
                        clientPoint,
                        NativeMethods.CWP_SKIPINVISIBLE |
                        NativeMethods.CWP_SKIPTRANSPARENT);
                }
                catch
                {
                    break;
                }

                if (child == IntPtr.Zero || child == current || !visited.Add(child))
                {
                    break;
                }

                depth++;

                Rectangle bounds;
                if (NativeMethods.IsWindowVisible(child) &&
                    !NativeMethods.IsIconic(child) &&
                    NativeMethods.TryGetExtendedFrameBounds(child, out bounds))
                {
                    bounds = Rectangle.Intersect(bounds, topLevel.Bounds);
                    bounds = Rectangle.Intersect(bounds, _virtualBounds);

                    if (bounds.Contains(screenPoint) &&
                        IsMeaningfulChildRectangle(bounds, topLevel.Bounds))
                    {
                        long area = Area(bounds);
                        if (area < bestArea || (area == bestArea && depth > bestDepth))
                        {
                            best = new Zone(
                                bounds,
                                ZoneKind.ChildWindow,
                                child,
                                topLevel.WindowHandle,
                                depth);
                            bestArea = area;
                            bestDepth = depth;
                        }
                    }
                }

                // Descend even if this HWND's own rectangle was too small or
                // duplicated its parent; a meaningful nested child may still exist.
                current = child;
            }

            return best;
        }

        private static List<Zone> CaptureMonitors()
        {
            List<Zone> monitors = new List<Zone>();
            HashSet<Rectangle> seenBounds = new HashSet<Rectangle>();
            Screen[] screens = Screen.AllScreens;
            int index;

            for (index = 0; index < screens.Length; index++)
            {
                Rectangle bounds = screens[index].Bounds;
                if (!IsMeaningfulRectangle(bounds) || !seenBounds.Add(bounds))
                {
                    continue;
                }

                monitors.Add(new Zone(
                    bounds,
                    ZoneKind.Monitor,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0));
            }

            return monitors;
        }

        private static Rectangle CalculateVirtualBounds(List<Zone> monitors)
        {
            if (monitors.Count == 0)
            {
                return Rectangle.Empty;
            }

            Rectangle result = monitors[0].Bounds;
            int index;
            for (index = 1; index < monitors.Count; index++)
            {
                result = Rectangle.Union(result, monitors[index].Bounds);
            }

            return result;
        }

        private static List<WindowHierarchy> CaptureWindowHierarchies(
            int excludedProcessId,
            Rectangle virtualBounds)
        {
            List<WindowHierarchy> result = new List<WindowHierarchy>();
            HashSet<Rectangle> seenTopLevelBounds = new HashSet<Rectangle>();

            if (virtualBounds.IsEmpty)
            {
                return result;
            }

            IList<IntPtr> windows;
            try
            {
                // EnumWindows is wrapped by EnumerateTopLevelWindows, which keeps
                // the native front-to-back order in its returned snapshot.
                windows = NativeMethods.EnumerateTopLevelWindows();
            }
            catch
            {
                return result;
            }

            int index;
            for (index = 0; index < windows.Count; index++)
            {
                IntPtr window = windows[index];
                try
                {
                    if (!IsEligibleTopLevelWindow(window, excludedProcessId))
                    {
                        continue;
                    }

                    Rectangle bounds;
                    if (!NativeMethods.TryGetExtendedFrameBounds(window, out bounds))
                    {
                        continue;
                    }

                    bounds = Rectangle.Intersect(bounds, virtualBounds);
                    if (!IsMeaningfulRectangle(bounds) ||
                        !seenTopLevelBounds.Add(bounds))
                    {
                        continue;
                    }

                    Zone topLevel = new Zone(
                        bounds,
                        ZoneKind.TopLevelWindow,
                        window,
                        window,
                        0);
                    result.Add(new WindowHierarchy(topLevel));
                }
                catch
                {
                    // Never allow a transient/broken foreign HWND to abort the
                    // rest of the z-ordered desktop snapshot.
                }
            }

            return result;
        }

        private static bool IsEligibleTopLevelWindow(IntPtr window, int excludedProcessId)
        {
            if (window == IntPtr.Zero ||
                window == NativeMethods.GetShellWindow() ||
                !NativeMethods.IsWindowVisible(window) ||
                NativeMethods.IsIconic(window) ||
                NativeMethods.IsWindowCloaked(window))
            {
                return false;
            }

            // Desktop-host HWNDs otherwise cover an entire monitor (or the whole
            // virtual desktop) and prevent the explicit monitor fallback from ever
            // winning over empty wallpaper.
            string className = NativeMethods.GetWindowClassName(window);
            if (string.Equals(className, "Progman", StringComparison.Ordinal) ||
                string.Equals(className, "WorkerW", StringComparison.Ordinal))
            {
                return false;
            }

            long extendedStyle = NativeMethods.GetWindowStyle(
                window,
                NativeMethods.GWL_EXSTYLE);
            if (IsTransparentOverlayStyle(extendedStyle))
            {
                return false;
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            return processId == 0 || processId != (uint)excludedProcessId;
        }

        internal static bool IsTransparentOverlayStyle(long extendedStyle)
        {
            bool clickThrough =
                (extendedStyle & NativeMethods.WS_EX_TRANSPARENT) != 0;
            bool overlaySurface =
                (extendedStyle & NativeMethods.WS_EX_LAYERED) != 0 ||
                (extendedStyle & NativeMethods.WS_EX_NOACTIVATE) != 0;
            return clickThrough && overlaySurface;
        }

        private static bool IsMeaningfulRectangle(Rectangle bounds)
        {
            return bounds.Width > 0 && bounds.Height > 0;
        }

        private static bool IsMeaningfulChildRectangle(
            Rectangle bounds,
            Rectangle topLevelBounds)
        {
            if (bounds.Width < MinimumChildDimension ||
                bounds.Height < MinimumChildDimension ||
                bounds == topLevelBounds)
            {
                return false;
            }

            return Area(bounds) >=
                (long)MinimumChildDimension * MinimumChildDimension;
        }

        private static long Area(Rectangle bounds)
        {
            return (long)bounds.Width * bounds.Height;
        }

        private sealed class WindowHierarchy
        {
            private readonly Zone _topLevel;

            internal WindowHierarchy(Zone topLevel)
            {
                _topLevel = topLevel;
            }

            internal Zone TopLevel
            {
                get { return _topLevel; }
            }
        }
    }
}
