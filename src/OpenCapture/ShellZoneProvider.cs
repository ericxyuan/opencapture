using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace OpenCapture
{
    /// <summary>
    /// Semantic Windows-shell surfaces exposed by <see cref="ShellZoneProvider"/>.
    /// The values deliberately distinguish shell affordances which often share one
    /// explorer.exe HWND but should be separate screenshot targets.
    /// </summary>
    public enum ShellZoneKind
    {
        PrimaryTaskbar,
        SecondaryTaskbar,
        TaskbarGroup,
        TaskbarAppButton,
        StartButton,
        SearchButton,
        TaskViewButton,
        WidgetsButton,
        NotificationArea,
        TrayIcon,
        ClockCalendarButton,
        QuickSettingsButton,
        NotificationCenterButton,
        TaskbarOverflow,
        StartMenu,
        SearchPanel,
        WidgetsPanel,
        QuickSettingsPanel,
        NotificationCenterPanel,
        CalendarPanel,
        ShellFlyout,
        ShellFlyoutElement
    }

    /// <summary>
    /// An immutable semantic shell target in physical virtual-desktop pixels.
    /// </summary>
    public sealed class ShellZone
    {
        private readonly Rectangle _bounds;
        private readonly ShellZoneKind _kind;
        private readonly string _name;
        private readonly string _automationId;
        private readonly string _className;
        private readonly int _processId;
        private readonly IntPtr _rootWindowHandle;
        private readonly int _specificity;

        internal ShellZone(
            Rectangle bounds,
            ShellZoneKind kind,
            string name,
            string automationId,
            string className,
            int processId,
            IntPtr rootWindowHandle,
            int specificity)
        {
            _bounds = bounds;
            _kind = kind;
            _name = name ?? string.Empty;
            _automationId = automationId ?? string.Empty;
            _className = className ?? string.Empty;
            _processId = processId;
            _rootWindowHandle = rootWindowHandle;
            _specificity = specificity;
        }

        public Rectangle Bounds
        {
            get { return _bounds; }
        }

        public ShellZoneKind Kind
        {
            get { return _kind; }
        }

        public string Name
        {
            get { return _name; }
        }

        public string AutomationId
        {
            get { return _automationId; }
        }

        public string ClassName
        {
            get { return _className; }
        }

        public int ProcessId
        {
            get { return _processId; }
        }

        /// <summary>The pre-overlay shell HWND from which this zone was found.</summary>
        public IntPtr RootWindowHandle
        {
            get { return _rootWindowHandle; }
        }

        /// <summary>
        /// True for an actual invokable/selectable shell control such as an app
        /// button, tray icon, clock, overflow button, or flyout item. These are
        /// eligible to win over their enclosing shell surface.
        /// </summary>
        public bool IsActionable
        {
            get { return _specificity >= 100; }
        }

        /// <summary>
        /// True only for accessibility layout scaffolding. Layout-only fragments
        /// have no guaranteed visible boundary and are excluded from published
        /// snapshots; the property makes that confidence rule explicit to callers.
        /// </summary>
        public bool IsLayoutOnly
        {
            get
            {
                return !IsActionable &&
                    (_kind == ShellZoneKind.TaskbarGroup ||
                        _kind == ShellZoneKind.NotificationArea ||
                        _kind == ShellZoneKind.ShellFlyoutElement);
            }
        }

        internal int Specificity
        {
            get { return _specificity; }
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
    /// Takes a non-blocking, pre-overlay snapshot of Windows shell zones.
    ///
    /// Capture must be called before the screenshot overlay is created. It records
    /// the exact shell HWND roots synchronously using User32, immediately publishes
    /// their usable rectangles, and resolves their UI Automation descendants on
    /// background MTA threads. Consequently a slow or broken foreign UIA provider
    /// cannot delay the hotkey path and an overlay created after Capture cannot be
    /// introduced into the UIA walk.
    /// </summary>
    public sealed class ShellZoneProvider : IDisposable
    {
        private const int DefaultScanBudgetMilliseconds = 900;
        private const int MaximumRootCount = 24;
        private const int MaximumAutomationRootsPerShellRoot = 128;
        private const int MaximumElementsPerRoot = 4096;
        private const int MinimumDimension = 4;
        private const long MinimumArea = 32;
        private const int ActionableSpecificity = 100;
        private const int DwmwaCloaked = 14;

        private readonly object _publishLock;
        private readonly List<ShellZone> _collectedZones;
        private readonly ReadOnlyCollection<RootDescriptor> _roots;
        private readonly Rectangle _virtualBounds;
        private readonly int _excludedProcessId;
        private readonly long _deadlineTimestamp;
        private readonly Timer _deadlineTimer;
        private volatile ReadOnlyCollection<ShellZone> _snapshot;
        private volatile bool _isComplete;
        private volatile bool _disposed;
        private int _remainingScans;

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(
            EnumWindowsCallback callback,
            IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(
            IntPtr parentWindow,
            EnumWindowsCallback callback,
            IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out NativeRectangle bounds);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(
            IntPtr window,
            StringBuilder className,
            int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(
            IntPtr window,
            StringBuilder text,
            int maximumCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(
            IntPtr window,
            int attribute,
            out int value,
            int valueSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRectangle
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        /// <summary>
        /// Captures roots now and gives their UIA descendants up to 900 ms to
        /// arrive asynchronously. Call this before showing any OpenCapture HWND.
        /// </summary>
        public static ShellZoneProvider Capture()
        {
            return new ShellZoneProvider(
                Process.GetCurrentProcess().Id,
                DefaultScanBudgetMilliseconds);
        }

        /// <summary>
        /// Test/integration overload. The budget limits publication, not the UI
        /// thread: all UI Automation calls occur on background threads.
        /// </summary>
        public static ShellZoneProvider Capture(
            int excludedProcessId,
            int scanBudgetMilliseconds)
        {
            return new ShellZoneProvider(excludedProcessId, scanBudgetMilliseconds);
        }

        private ShellZoneProvider(int excludedProcessId, int scanBudgetMilliseconds)
        {
            if (scanBudgetMilliseconds < 1)
            {
                throw new ArgumentOutOfRangeException("scanBudgetMilliseconds");
            }

            _publishLock = new object();
            _collectedZones = new List<ShellZone>();
            _excludedProcessId = excludedProcessId;
            _virtualBounds = GetVirtualBounds();

            List<RootDescriptor> roots = CaptureRoots(
                excludedProcessId,
                _virtualBounds);
            _roots = new ReadOnlyCollection<RootDescriptor>(roots.ToArray());

            int rootIndex;
            for (rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                RootDescriptor root = roots[rootIndex];
                if (root.PublishRootBounds)
                {
                    _collectedZones.Add(CreateRootZone(root));
                }
            }

            _snapshot = BuildSnapshot(_collectedZones);
            _remainingScans = roots.Count;
            _deadlineTimestamp = Stopwatch.GetTimestamp() +
                MillisecondsToStopwatchTicks(scanBudgetMilliseconds);

            if (roots.Count == 0)
            {
                _isComplete = true;
                _deadlineTimer = null;
                return;
            }

            _deadlineTimer = new Timer(
                DeadlineElapsed,
                null,
                scanBudgetMilliseconds,
                Timeout.Infinite);

            // Thread.Start does not perform UI Automation work. Starting the MTA
            // workers here avoids a ThreadPool scheduling gap between showing the
            // frozen overlay and publishing the first per-item taskbar bounds.
            StartRootScans();
        }

        /// <summary>
        /// The latest immutable snapshot. A caller can safely retain an older
        /// instance while background discovery publishes a richer replacement.
        /// </summary>
        public ReadOnlyCollection<ShellZone> Snapshot
        {
            get { return _snapshot; }
        }

        /// <summary>Alias intended for zone-detector integrations.</summary>
        public ReadOnlyCollection<ShellZone> Zones
        {
            get { return _snapshot; }
        }

        /// <summary>
        /// True once every root completed or the publication deadline elapsed.
        /// A hung UIA provider never prevents this property becoming true.
        /// </summary>
        public bool IsComplete
        {
            get { return _isComplete; }
        }

        /// <summary>
        /// Returns the smallest semantic zone containing the physical screen point.
        /// Ties prefer controls over containers and roots.
        /// </summary>
        public ShellZone FindSmallestAt(Point screenPoint)
        {
            ReadOnlyCollection<ShellZone> localSnapshot = _snapshot;
            ShellZone best = null;
            long bestArea = long.MaxValue;
            int bestSpecificity = int.MinValue;
            int index;

            for (index = 0; index < localSnapshot.Count; index++)
            {
                ShellZone candidate = localSnapshot[index];
                if (!candidate.Bounds.Contains(screenPoint))
                {
                    continue;
                }

                long area = Area(candidate.Bounds);
                bool candidateIsActionable =
                    candidate.Specificity >= ActionableSpecificity;
                bool bestIsActionable =
                    best != null && best.Specificity >= ActionableSpecificity;
                if (best == null ||
                    (candidateIsActionable && !bestIsActionable) ||
                    (candidateIsActionable == bestIsActionable &&
                        (area < bestArea ||
                            (area == bestArea &&
                                candidate.Specificity > bestSpecificity))))
                {
                    best = candidate;
                    bestArea = area;
                    bestSpecificity = candidate.Specificity;
                }
            }

            return best;
        }

        /// <summary>Compatibility alias for callers using the main detector name.</summary>
        public ShellZone FindZoneAt(Point screenPoint)
        {
            return FindSmallestAt(screenPoint);
        }

        public void Dispose()
        {
            lock (_publishLock)
            {
                _disposed = true;
                _isComplete = true;
                if (_deadlineTimer != null)
                {
                    _deadlineTimer.Dispose();
                }
            }
        }

        private void StartRootScans()
        {
            int index;
            for (index = 0; index < _roots.Count; index++)
            {
                if (_disposed || PastDeadline())
                {
                    break;
                }

                RootDescriptor root = _roots[index];
                Thread worker = new Thread(
                    new ThreadStart(delegate { ScanRootAndPublish(root); }));
                worker.IsBackground = true;
                worker.Name = "OpenCapture shell zone scan";
                try
                {
                    worker.SetApartmentState(ApartmentState.MTA);
                }
                catch (InvalidOperationException)
                {
                }

                worker.Start();
            }
        }

        private void ScanRootAndPublish(RootDescriptor root)
        {
            int maximumAttempts = root.IsTaskbar ? 4 : 2;
            int attempt;
            for (attempt = 0; attempt < maximumAttempts; attempt++)
            {
                if (_disposed || PastDeadline())
                {
                    break;
                }

                ScanResult scanResult = ScanRoot(root);
                bool usableResult = root.IsTaskbar
                    ? scanResult.ActionableZoneCount > 0
                    : scanResult.ZoneCount > 0;
                if (usableResult)
                {
                    break;
                }

                // UIA occasionally returns an empty tree or RPC/COM failure while
                // Explorer is updating the taskbar. Never turn that one transient
                // response into a completed root-only snapshot. Retrying on this
                // background MTA does not delay screen capture or overlay input.
                if (attempt + 1 < maximumAttempts && !PastDeadline())
                {
                    Thread.Sleep(20 * (attempt + 1));
                }
            }

            if (Interlocked.Decrement(ref _remainingScans) == 0)
            {
                lock (_publishLock)
                {
                    _isComplete = true;
                    if (_deadlineTimer != null)
                    {
                        _deadlineTimer.Dispose();
                    }
                }
            }
        }

        private ScanResult ScanRoot(RootDescriptor rootDescriptor)
        {
            ScanResult scanResult = new ScanResult();
            if (PastDeadline() || !RootStillMatches(rootDescriptor))
            {
                return scanResult;
            }

            int automationRootIndex;
            for (automationRootIndex = 0;
                 automationRootIndex < rootDescriptor.AutomationRoots.Count;
                 automationRootIndex++)
            {
                if (_disposed || PastDeadline())
                {
                    break;
                }

                List<ShellZone> fragmentZones = new List<ShellZone>();
                try
                {
                    ScanAutomationRoot(
                        rootDescriptor,
                        rootDescriptor.AutomationRoots[automationRootIndex],
                        fragmentZones);
                }
                catch (ElementNotAvailableException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (COMException)
                {
                }
                catch
                {
                    // A disconnected XAML island or native accessibility proxy
                    // must not abort its sibling fragments.
                }

                int zoneIndex;
                for (zoneIndex = 0; zoneIndex < fragmentZones.Count; zoneIndex++)
                {
                    ShellZone discovered = fragmentZones[zoneIndex];
                    if (!discovered.IsLayoutOnly)
                    {
                        scanResult.ZoneCount++;
                    }
                    if (discovered.Specificity >= ActionableSpecificity)
                    {
                        scanResult.ActionableZoneCount++;
                    }
                }
                PublishDiscoveredZones(fragmentZones);
            }

            return scanResult;
        }

        private void PublishDiscoveredZones(IList<ShellZone> zones)
        {
            if (zones == null || zones.Count == 0 || _disposed ||
                _isComplete || PastDeadline())
            {
                return;
            }

            lock (_publishLock)
            {
                if (_disposed || _isComplete || PastDeadline())
                {
                    return;
                }

                _collectedZones.AddRange(zones);
                _snapshot = BuildSnapshot(_collectedZones);
            }
        }

        /// <summary>
        /// Scans one native accessibility fragment. Windows 11 commonly hosts the
        /// taskbar and overflow flyout in DesktopWindowXamlSource/InputSite child
        /// HWNDs whose elements are not guaranteed to appear below the top-level
        /// HWND in every UIA view. Each of those HWNDs is therefore an explicit
        /// pre-overlay automation root.
        /// </summary>
        private void ScanAutomationRoot(
            RootDescriptor shellRoot,
            AutomationRootDescriptor automationRoot,
            List<ShellZone> result)
        {
            if (!AutomationRootStillMatches(automationRoot))
            {
                return;
            }

            AutomationElement root = AutomationElement.FromHandle(
                automationRoot.Handle);
            if (root == null || PastDeadline())
            {
                return;
            }

            AutomationElement cachedRoot;
            AutomationElementCollection elements;
            CacheRequest cache = new CacheRequest();
            // Every property read below comes from this cache. None mode avoids
            // retaining live cross-process ElementProxy objects after a scan,
            // reducing both UIA leaks and finalizer-time RemoteMemoryBlock faults.
            cache.AutomationElementMode = AutomationElementMode.None;
            cache.TreeFilter = Automation.RawViewCondition;
            cache.TreeScope = TreeScope.Element;
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.ProcessIdProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.AutomationIdProperty);
            cache.Add(AutomationElement.ClassNameProperty);
            cache.Add(AutomationElement.IsInvokePatternAvailableProperty);
            cache.Add(AutomationElement.IsSelectionItemPatternAvailableProperty);
            cache.Add(AutomationElement.IsTogglePatternAvailableProperty);
            cache.Add(AutomationElement.IsExpandCollapsePatternAvailableProperty);
            cache.Add(AutomationElement.IsRangeValuePatternAvailableProperty);

            using (cache.Activate())
            {
                cachedRoot = root.GetUpdatedCache(cache);
                elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            }

            if (PastDeadline())
            {
                return;
            }

            // A native child HWND can itself be the actionable UIA fragment root.
            // The former descendants-only walk lost those controls entirely.
            AddCachedElementZone(shellRoot, cachedRoot, result);

            int count = elements == null
                ? 0
                : Math.Min(elements.Count, MaximumElementsPerRoot);
            int index;
            for (index = 0; index < count; index++)
            {
                if (_disposed || PastDeadline())
                {
                    break;
                }

                AddCachedElementZone(shellRoot, elements[index], result);
            }
        }

        private void AddCachedElementZone(
            RootDescriptor rootDescriptor,
            AutomationElement element,
            List<ShellZone> result)
        {
            ElementData data;
            if (element == null ||
                !TryReadCachedElement(element, out data) ||
                data.IsOffscreen ||
                data.ProcessId == _excludedProcessId)
            {
                return;
            }

            Rectangle bounds;
            if (!TryConvertBounds(data.Bounds, _virtualBounds, out bounds))
            {
                return;
            }

            bounds = Rectangle.Intersect(bounds, rootDescriptor.Bounds);
            if (!IsUsable(bounds) || bounds == rootDescriptor.Bounds)
            {
                return;
            }

            ShellZoneKind kind;
            int specificity;
            if (!TryClassifyElement(
                rootDescriptor,
                data,
                out kind,
                out specificity))
            {
                return;
            }

            result.Add(new ShellZone(
                bounds,
                kind,
                data.Name,
                data.AutomationId,
                data.ClassName,
                rootDescriptor.ProcessId,
                rootDescriptor.Handle,
                specificity));
        }

        private static bool TryReadCachedElement(
            AutomationElement element,
            out ElementData data)
        {
            data = new ElementData();
            try
            {
                object bounds = element.GetCachedPropertyValue(
                    AutomationElement.BoundingRectangleProperty,
                    true);
                if (!(bounds is System.Windows.Rect))
                {
                    return false;
                }

                data.Bounds = (System.Windows.Rect)bounds;
                data.IsOffscreen = GetCachedBoolean(
                    element,
                    AutomationElement.IsOffscreenProperty,
                    false);
                data.ProcessId = GetCachedInteger(
                    element,
                    AutomationElement.ProcessIdProperty,
                    0);
                data.ControlType = GetCachedControlType(element);
                data.Name = GetCachedString(element, AutomationElement.NameProperty);
                data.AutomationId = GetCachedString(
                    element,
                    AutomationElement.AutomationIdProperty);
                data.ClassName = GetCachedString(
                    element,
                    AutomationElement.ClassNameProperty);
                data.HasInvokePattern = GetCachedBoolean(
                    element,
                    AutomationElement.IsInvokePatternAvailableProperty,
                    false);
                data.HasSelectionItemPattern = GetCachedBoolean(
                    element,
                    AutomationElement.IsSelectionItemPatternAvailableProperty,
                    false);
                data.HasTogglePattern = GetCachedBoolean(
                    element,
                    AutomationElement.IsTogglePatternAvailableProperty,
                    false);
                data.HasExpandCollapsePattern = GetCachedBoolean(
                    element,
                    AutomationElement.IsExpandCollapsePatternAvailableProperty,
                    false);
                data.HasRangeValuePattern = GetCachedBoolean(
                    element,
                    AutomationElement.IsRangeValuePatternAvailableProperty,
                    false);
                return true;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (COMException)
            {
                return false;
            }
        }

        private static bool GetCachedBoolean(
            AutomationElement element,
            AutomationProperty property,
            bool fallback)
        {
            object value = element.GetCachedPropertyValue(property, true);
            return value is bool ? (bool)value : fallback;
        }

        private static int GetCachedInteger(
            AutomationElement element,
            AutomationProperty property,
            int fallback)
        {
            object value = element.GetCachedPropertyValue(property, true);
            return value is int ? (int)value : fallback;
        }

        private static string GetCachedString(
            AutomationElement element,
            AutomationProperty property)
        {
            object value = element.GetCachedPropertyValue(property, true);
            return value as string ?? string.Empty;
        }

        private static ControlType GetCachedControlType(AutomationElement element)
        {
            object value = element.GetCachedPropertyValue(
                AutomationElement.ControlTypeProperty,
                true);
            return value as ControlType;
        }

        private static bool TryClassifyElement(
            RootDescriptor root,
            ElementData element,
            out ShellZoneKind kind,
            out int specificity)
        {
            string key = ((element.AutomationId ?? string.Empty) + " " +
                (element.ClassName ?? string.Empty)).ToLowerInvariant();
            string name = (element.Name ?? string.Empty).Trim().ToLowerInvariant();

            if (ContainsAny(key, "startbutton", "start button", "startmenu button") ||
                name == "start")
            {
                kind = ShellZoneKind.StartButton;
                specificity = 120;
                return true;
            }

            if (ContainsAny(key, "searchbutton", "search button", "searchbox") ||
                name == "search" || name.StartsWith("search ", StringComparison.Ordinal))
            {
                kind = ShellZoneKind.SearchButton;
                specificity = 120;
                return true;
            }

            if (ContainsAny(key, "taskview", "task view") || name == "task view")
            {
                kind = ShellZoneKind.TaskViewButton;
                specificity = 120;
                return true;
            }

            if (ContainsAny(key, "widgetsbutton", "widgetbutton", "taskbar.widget") ||
                name == "widgets")
            {
                kind = ShellZoneKind.WidgetsButton;
                specificity = 120;
                return true;
            }

            if (ContainsAny(
                    key,
                    "systemtrayiconclock",
                    "trayclock",
                    "clockbutton",
                    "calendarbutton",
                    "dateandtime",
                    "timeanddate") ||
                ContainsAny(name, "clock", "date and time", "time and date"))
            {
                kind = ShellZoneKind.ClockCalendarButton;
                specificity = 125;
                return true;
            }

            if (ContainsAny(
                    key,
                    "notificationcenterbutton",
                    "actioncenterbutton",
                    "systemtrayiconbell",
                    "notificationbutton") ||
                ContainsAny(name, "notification center", "action center"))
            {
                kind = ShellZoneKind.NotificationCenterButton;
                specificity = 125;
                return true;
            }

            if (ContainsAny(
                    key,
                    "quicksettings",
                    "controlcenterbutton",
                    "systemtrayiconnetwork",
                    "systemtrayiconvolume",
                    "systemtrayiconpower") ||
                ContainsAny(
                    name,
                    "quick settings",
                    "control center",
                    "network ",
                    "volume ",
                    "power battery",
                    "battery status"))
            {
                kind = ShellZoneKind.QuickSettingsButton;
                specificity = 125;
                return true;
            }

            if (ContainsAny(key, "chevron", "overflowbutton", "showhiddenicons") ||
                ContainsAny(name, "hidden icons", "notification overflow"))
            {
                kind = ShellZoneKind.TaskbarOverflow;
                specificity = 120;
                return true;
            }

            if (ContainsAny(
                    key,
                    "systemtrayicon",
                    "systemtray.",
                    "notifyicon",
                    "notifyitemicon",
                    "trayicon",
                    "notificationicon",
                    "normalbutton",
                    "accentbutton",
                    "omnibutton"))
            {
                kind = ShellZoneKind.TrayIcon;
                specificity = 115;
                return IsActionable(element);
            }

            if (root.Kind == ShellZoneKind.TaskbarOverflow && IsActionable(element))
            {
                kind = ShellZoneKind.TrayIcon;
                specificity = 115;
                return true;
            }

            if (ContainsAny(
                key,
                "notificationarea",
                "traynotify",
                "systemtray",
                "statusarea",
                "systray"))
            {
                kind = ShellZoneKind.NotificationArea;
                specificity = 70;
                return true;
            }

            if (root.IsTaskbar)
            {
                if (IsActionable(element))
                {
                    kind = ShellZoneKind.TaskbarAppButton;
                    specificity = 105;
                    return true;
                }

                if (IsContainer(element.ControlType))
                {
                    kind = ShellZoneKind.TaskbarGroup;
                    specificity = 55;
                    return true;
                }

                kind = ShellZoneKind.TaskbarGroup;
                specificity = 0;
                return false;
            }

            if (IsActionable(element) || IsContainer(element.ControlType))
            {
                kind = ShellZoneKind.ShellFlyoutElement;
                specificity = IsActionable(element) ? ActionableSpecificity : 50;
                return true;
            }

            kind = ShellZoneKind.ShellFlyoutElement;
            specificity = 0;
            return false;
        }

        private static bool IsActionable(ElementData element)
        {
            return IsInteractiveControlType(element.ControlType) ||
                element.HasInvokePattern ||
                element.HasSelectionItemPattern ||
                element.HasTogglePattern ||
                element.HasExpandCollapsePattern ||
                element.HasRangeValuePattern;
        }

        private static bool IsInteractiveControlType(ControlType controlType)
        {
            return controlType == ControlType.Button ||
                controlType == ControlType.CheckBox ||
                controlType == ControlType.ComboBox ||
                controlType == ControlType.DataItem ||
                controlType == ControlType.Edit ||
                controlType == ControlType.Hyperlink ||
                controlType == ControlType.ListItem ||
                controlType == ControlType.MenuItem ||
                controlType == ControlType.RadioButton ||
                controlType == ControlType.Slider ||
                controlType == ControlType.Spinner ||
                controlType == ControlType.SplitButton ||
                controlType == ControlType.TabItem ||
                controlType == ControlType.Thumb ||
                controlType == ControlType.TreeItem;
        }

        private static bool IsContainer(ControlType controlType)
        {
            return controlType == ControlType.Calendar ||
                controlType == ControlType.Group ||
                controlType == ControlType.List ||
                controlType == ControlType.Menu ||
                controlType == ControlType.Pane ||
                controlType == ControlType.Tab ||
                controlType == ControlType.ToolBar ||
                controlType == ControlType.Tree;
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            int index;
            for (index = 0; index < terms.Length; index++)
            {
                if (value.IndexOf(terms[index], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool StartsWithOrdinal(string value, string prefix)
        {
            return !string.IsNullOrEmpty(value) &&
                value.StartsWith(prefix, StringComparison.Ordinal);
        }

        private static List<RootDescriptor> CaptureRoots(
            int excludedProcessId,
            Rectangle virtualBounds)
        {
            List<RootDescriptor> roots = new List<RootDescriptor>();
            HashSet<IntPtr> handles = new HashSet<IntPtr>();
            Dictionary<uint, string> processNames = new Dictionary<uint, string>();

            EnumWindowsCallback callback = delegate(IntPtr window, IntPtr ignored)
            {
                try
                {
                    if (roots.Count >= MaximumRootCount ||
                        window == IntPtr.Zero ||
                        !IsWindowVisible(window) ||
                        IsCloaked(window))
                    {
                        return roots.Count < MaximumRootCount;
                    }

                    uint processId;
                    GetWindowThreadProcessId(window, out processId);
                    if (processId == (uint)excludedProcessId)
                    {
                        return true;
                    }

                    string className = ReadClassName(window);
                    if (!IsPotentialShellClass(className))
                    {
                        return true;
                    }

                    string processName = GetProcessName(processId, processNames);
                    string title = ReadWindowText(window);
                    ShellZoneKind kind;
                    bool isTaskbar;
                    if (!TryClassifyRoot(
                        className,
                        title,
                        processName,
                        out kind,
                        out isTaskbar))
                    {
                        return true;
                    }

                    NativeRectangle nativeBounds;
                    if (!GetWindowRect(window, out nativeBounds))
                    {
                        return true;
                    }

                    Rectangle nativeWindowBounds = Rectangle.FromLTRB(
                        nativeBounds.Left,
                        nativeBounds.Top,
                        nativeBounds.Right,
                        nativeBounds.Bottom);
                    nativeWindowBounds = Rectangle.Intersect(
                        nativeWindowBounds,
                        virtualBounds);
                    bool nativeBoundsAreUsable = IsUsable(nativeWindowBounds);
                    if (!handles.Add(window))
                    {
                        return true;
                    }

                    // A few Windows 11 DesktopWindowXamlSource bridge HWNDs expose
                    // an empty native rectangle while their UIA descendants expose
                    // correct screen bounds. Keep such a root, use the desktop only
                    // as a clipping extent, and never publish the placeholder HWND.
                    Rectangle bounds = nativeBoundsAreUsable
                        ? nativeWindowBounds
                        : virtualBounds;

                    // Some XAML shell hosts use a monitor-sized transparent HWND.
                    // Walk it for useful descendants, but do not publish that host
                    // itself as a target over the wallpaper or other applications.
                    bool publishRoot = nativeBoundsAreUsable && (isTaskbar ||
                        Area(bounds) < (Area(virtualBounds) * 3L) / 4L);
                    ReadOnlyCollection<AutomationRootDescriptor> automationRoots =
                        CaptureAutomationRoots(
                            window,
                            (int)processId,
                            excludedProcessId,
                            bounds,
                            virtualBounds);
                    roots.Add(new RootDescriptor(
                        window,
                        bounds,
                        kind,
                        (int)processId,
                        className,
                        title,
                        isTaskbar,
                        publishRoot,
                        automationRoots));
                }
                catch
                {
                    // Windows can disappear while EnumWindows is in progress.
                }

                return roots.Count < MaximumRootCount;
            };

            try
            {
                EnumWindows(callback, IntPtr.Zero);
            }
            catch
            {
            }

            // Favor taskbar discovery when the accessibility budget is tight.
            roots.Sort(delegate(RootDescriptor left, RootDescriptor right)
            {
                if (left.IsTaskbar != right.IsTaskbar)
                {
                    return left.IsTaskbar ? -1 : 1;
                }

                return Area(left.Bounds).CompareTo(Area(right.Bounds));
            });
            return roots;
        }

        private static ReadOnlyCollection<AutomationRootDescriptor>
            CaptureAutomationRoots(
                IntPtr shellRoot,
                int shellProcessId,
                int excludedProcessId,
                Rectangle shellBounds,
                Rectangle virtualBounds)
        {
            List<AutomationRootDescriptor> result =
                new List<AutomationRootDescriptor>();
            HashSet<IntPtr> seen = new HashSet<IntPtr>();

            EnumWindowsCallback callback = delegate(IntPtr child, IntPtr ignored)
            {
                try
                {
                    if (result.Count >= MaximumAutomationRootsPerShellRoot)
                    {
                        return false;
                    }
                    if (child == IntPtr.Zero || !seen.Add(child))
                    {
                        return true;
                    }

                    uint processId;
                    GetWindowThreadProcessId(child, out processId);
                    if (processId == (uint)excludedProcessId)
                    {
                        return true;
                    }

                    Rectangle bounds = GetClippedNativeBounds(
                        child,
                        shellBounds,
                        virtualBounds);
                    string className = ReadClassName(child);
                    if (!IsUsable(bounds) && !IsAccessibilityBridgeClass(className))
                    {
                        return true;
                    }

                    result.Add(new AutomationRootDescriptor(
                        child,
                        (int)processId,
                        className,
                        bounds,
                        false));
                }
                catch
                {
                    // Child HWNDs are volatile while the taskbar animates.
                }

                return result.Count < MaximumAutomationRootsPerShellRoot;
            };

            try
            {
                EnumChildWindows(shellRoot, callback, IntPtr.Zero);
            }
            catch
            {
            }

            // Order concrete native/XAML child fragments so the most useful
            // fallbacks are adjacent. The shell root itself is inserted first
            // below: on normal Windows 11 builds it exposes all taskbar buttons in
            // one inexpensive query, allowing the first overlay refresh to use
            // exact current app bounds. Child fragments then cover builds where
            // XAML islands are disconnected from that root's raw UIA tree.
            result.Sort(delegate(
                AutomationRootDescriptor left,
                AutomationRootDescriptor right)
            {
                int priority = AutomationRootPriority(left.ClassName).CompareTo(
                    AutomationRootPriority(right.ClassName));
                if (priority != 0)
                {
                    return priority;
                }
                return Area(left.Bounds).CompareTo(Area(right.Bounds));
            });

            result.Insert(0, new AutomationRootDescriptor(
                shellRoot,
                shellProcessId,
                ReadClassName(shellRoot),
                shellBounds,
                true));
            return new ReadOnlyCollection<AutomationRootDescriptor>(
                result.ToArray());
        }

        private static Rectangle GetClippedNativeBounds(
            IntPtr window,
            Rectangle shellBounds,
            Rectangle virtualBounds)
        {
            NativeRectangle nativeBounds;
            if (!GetWindowRect(window, out nativeBounds))
            {
                return Rectangle.Empty;
            }

            Rectangle bounds = Rectangle.FromLTRB(
                nativeBounds.Left,
                nativeBounds.Top,
                nativeBounds.Right,
                nativeBounds.Bottom);
            bounds = Rectangle.Intersect(bounds, virtualBounds);
            return Rectangle.Intersect(bounds, shellBounds);
        }

        private static int AutomationRootPriority(string className)
        {
            if (string.Equals(
                    className,
                    "Windows.UI.Input.InputSite.WindowClass",
                    StringComparison.Ordinal) ||
                StartsWithOrdinal(className, "XamlExplorerHostIslandWindow"))
            {
                return 0;
            }
            if (string.Equals(
                className,
                "Windows.UI.Composition.DesktopWindowContentBridge",
                StringComparison.Ordinal) ||
                string.Equals(
                    className,
                    "Windows.UI.Core.CoreWindow",
                    StringComparison.Ordinal))
            {
                return 1;
            }
            if (string.Equals(className, "MSTaskListWClass", StringComparison.Ordinal) ||
                string.Equals(className, "MSTaskSwWClass", StringComparison.Ordinal) ||
                string.Equals(className, "ToolbarWindow32", StringComparison.Ordinal) ||
                string.Equals(className, "TrayNotifyWnd", StringComparison.Ordinal) ||
                string.Equals(className, "TrayClockWClass", StringComparison.Ordinal) ||
                string.Equals(className, "Start", StringComparison.Ordinal))
            {
                return 2;
            }
            return 10;
        }

        private static bool IsAccessibilityBridgeClass(string className)
        {
            return AutomationRootPriority(className) <= 1;
        }

        private static bool IsPotentialShellClass(string className)
        {
            return string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal) ||
                string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.Ordinal) ||
                string.Equals(className, "NotifyIconOverflowWindow", StringComparison.Ordinal) ||
                string.Equals(className, "ControlCenterWindow", StringComparison.Ordinal) ||
                string.Equals(className, "NotificationCenterWindow", StringComparison.Ordinal) ||
                string.Equals(className, "Windows.UI.Core.CoreWindow", StringComparison.Ordinal) ||
                string.Equals(className, "ApplicationFrameWindow", StringComparison.Ordinal) ||
                StartsWithOrdinal(className, "XamlExplorerHostIslandWindow") ||
                string.Equals(className, "TopLevelWindowForOverflowXamlIsland", StringComparison.Ordinal) ||
                string.Equals(className, "Windows.UI.Composition.DesktopWindowContentBridge", StringComparison.Ordinal) ||
                string.Equals(className, "Xaml_WindowedPopupClass", StringComparison.Ordinal) ||
                string.Equals(className, "PopupHost", StringComparison.Ordinal);
        }

        private static bool TryClassifyRoot(
            string className,
            string title,
            string processName,
            out ShellZoneKind kind,
            out bool isTaskbar)
        {
            isTaskbar = false;
            if (string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal))
            {
                kind = ShellZoneKind.PrimaryTaskbar;
                isTaskbar = true;
                return true;
            }

            if (string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.Ordinal))
            {
                kind = ShellZoneKind.SecondaryTaskbar;
                isTaskbar = true;
                return true;
            }

            if (string.Equals(className, "NotifyIconOverflowWindow", StringComparison.Ordinal) ||
                string.Equals(className, "TopLevelWindowForOverflowXamlIsland", StringComparison.Ordinal))
            {
                kind = ShellZoneKind.TaskbarOverflow;
                return IsExplorerOrShellProcess(processName);
            }

            if (StartsWithOrdinal(className, "XamlExplorerHostIslandWindow"))
            {
                kind = ShellZoneKind.ShellFlyout;
                return IsExplorerOrShellProcess(processName);
            }

            string process = (processName ?? string.Empty).ToLowerInvariant();
            string description = ((className ?? string.Empty) + " " +
                (title ?? string.Empty)).ToLowerInvariant();

            if (process == "startmenuexperiencehost")
            {
                kind = ShellZoneKind.StartMenu;
                return true;
            }

            if (process == "searchhost" || process == "searchapp")
            {
                kind = ShellZoneKind.SearchPanel;
                return true;
            }

            if (process == "widgets" || process == "widgetservice")
            {
                kind = ShellZoneKind.WidgetsPanel;
                return true;
            }

            if (!IsExplorerOrShellProcess(processName))
            {
                kind = ShellZoneKind.ShellFlyout;
                return false;
            }

            if (ContainsAny(description, "controlcenter", "quick settings"))
            {
                kind = ShellZoneKind.QuickSettingsPanel;
                return true;
            }

            if (ContainsAny(description, "notificationcenter", "notification center", "action center"))
            {
                kind = ShellZoneKind.NotificationCenterPanel;
                return true;
            }

            if (ContainsAny(description, "clock", "calendar"))
            {
                kind = ShellZoneKind.CalendarPanel;
                return true;
            }

            // Generic CoreWindow/XAML roots are accepted only for dedicated shell
            // processes; explorer.exe's File Explorer ApplicationFrameWindows are
            // intentionally not shell capture zones.
            if (process == "shellexperiencehost" || process == "shellhost")
            {
                kind = ShellZoneKind.ShellFlyout;
                return true;
            }

            kind = ShellZoneKind.ShellFlyout;
            return false;
        }

        private static bool IsExplorerOrShellProcess(string processName)
        {
            string process = (processName ?? string.Empty).ToLowerInvariant();
            return process == "explorer" ||
                process == "shellexperiencehost" ||
                process == "shellhost" ||
                process == "startmenuexperiencehost" ||
                process == "searchhost" ||
                process == "searchapp" ||
                process == "widgets" ||
                process == "widgetservice";
        }

        private static string GetProcessName(
            uint processId,
            Dictionary<uint, string> cache)
        {
            string name;
            if (cache.TryGetValue(processId, out name))
            {
                return name;
            }

            name = string.Empty;
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    name = process.ProcessName;
                }
            }
            catch
            {
            }

            cache[processId] = name;
            return name;
        }

        private static bool RootStillMatches(RootDescriptor root)
        {
            if (!IsWindow(root.Handle) || !IsWindowVisible(root.Handle))
            {
                return false;
            }

            uint currentProcessId;
            GetWindowThreadProcessId(root.Handle, out currentProcessId);
            return currentProcessId == (uint)root.ProcessId;
        }

        private static bool AutomationRootStillMatches(
            AutomationRootDescriptor root)
        {
            if (!IsWindow(root.Handle))
            {
                return false;
            }

            uint currentProcessId;
            GetWindowThreadProcessId(root.Handle, out currentProcessId);
            return currentProcessId == (uint)root.ProcessId;
        }

        private static ShellZone CreateRootZone(RootDescriptor root)
        {
            string name;
            if (!string.IsNullOrEmpty(root.Title))
            {
                name = root.Title;
            }
            else if (root.Kind == ShellZoneKind.PrimaryTaskbar)
            {
                name = "Primary taskbar";
            }
            else if (root.Kind == ShellZoneKind.SecondaryTaskbar)
            {
                name = "Secondary taskbar";
            }
            else
            {
                name = root.Kind.ToString();
            }

            return new ShellZone(
                root.Bounds,
                root.Kind,
                name,
                string.Empty,
                root.ClassName,
                root.ProcessId,
                root.Handle,
                RootSpecificity(root.Kind));
        }

        private static int RootSpecificity(ShellZoneKind kind)
        {
            if (kind == ShellZoneKind.PrimaryTaskbar ||
                kind == ShellZoneKind.SecondaryTaskbar)
            {
                return 20;
            }

            return 30;
        }

        private static ReadOnlyCollection<ShellZone> BuildSnapshot(
            IList<ShellZone> candidates)
        {
            Dictionary<ZoneKey, ShellZone> unique =
                new Dictionary<ZoneKey, ShellZone>();
            int index;
            for (index = 0; index < candidates.Count; index++)
            {
                ShellZone candidate = candidates[index];
                if (!IsUsable(candidate.Bounds) || candidate.IsLayoutOnly)
                {
                    continue;
                }

                ZoneKey key = new ZoneKey(
                    candidate.Bounds,
                    candidate.RootWindowHandle);
                ShellZone existing;
                if (!unique.TryGetValue(key, out existing) ||
                    candidate.Specificity > existing.Specificity)
                {
                    unique[key] = candidate;
                }
            }

            List<ShellZone> deduplicated = new List<ShellZone>(unique.Values);
            List<ShellZone> result = new List<ShellZone>();
            for (index = 0; index < deduplicated.Count; index++)
            {
                ShellZone candidate = deduplicated[index];
                if (!IsLayoutContainerInsideAction(candidate, deduplicated))
                {
                    result.Add(candidate);
                }
            }

            result.Sort(delegate(ShellZone left, ShellZone right)
            {
                bool leftIsActionable =
                    left.Specificity >= ActionableSpecificity;
                bool rightIsActionable =
                    right.Specificity >= ActionableSpecificity;
                if (leftIsActionable != rightIsActionable)
                {
                    return leftIsActionable ? -1 : 1;
                }

                long leftArea = Area(left.Bounds);
                long rightArea = Area(right.Bounds);
                int areaComparison = leftArea.CompareTo(rightArea);
                if (areaComparison != 0)
                {
                    return areaComparison;
                }

                int specificityComparison =
                    right.Specificity.CompareTo(left.Specificity);
                if (specificityComparison != 0)
                {
                    return specificityComparison;
                }

                int xComparison = left.Bounds.Left.CompareTo(right.Bounds.Left);
                return xComparison != 0
                    ? xComparison
                    : left.Bounds.Top.CompareTo(right.Bounds.Top);
            });
            return new ReadOnlyCollection<ShellZone>(result.ToArray());
        }

        private static bool IsLayoutContainerInsideAction(
            ShellZone candidate,
            IList<ShellZone> zones)
        {
            if (candidate.Specificity >= ActionableSpecificity ||
                (candidate.Kind != ShellZoneKind.TaskbarGroup &&
                    candidate.Kind != ShellZoneKind.NotificationArea &&
                    candidate.Kind != ShellZoneKind.ShellFlyoutElement))
            {
                return false;
            }

            int index;
            for (index = 0; index < zones.Count; index++)
            {
                ShellZone action = zones[index];
                if (action.Specificity < ActionableSpecificity ||
                    action.RootWindowHandle != candidate.RootWindowHandle)
                {
                    continue;
                }

                if (action.Bounds.Contains(candidate.Bounds))
                {
                    return true;
                }
            }

            return false;
        }

        private void DeadlineElapsed(object ignored)
        {
            // Completion must follow any snapshot already being built. Callers
            // read IsComplete before Snapshot and stop polling once both are
            // consumed, so publishing completion early can lose the final zones.
            lock (_publishLock)
            {
                _isComplete = true;
            }
        }

        private bool PastDeadline()
        {
            return Stopwatch.GetTimestamp() >= _deadlineTimestamp;
        }

        private static long MillisecondsToStopwatchTicks(int milliseconds)
        {
            double ticks = ((double)Stopwatch.Frequency * milliseconds) / 1000.0;
            return Math.Max(1L, (long)Math.Ceiling(ticks));
        }

        private static Rectangle GetVirtualBounds()
        {
            const int SmXVirtualScreen = 76;
            const int SmYVirtualScreen = 77;
            const int SmCxVirtualScreen = 78;
            const int SmCyVirtualScreen = 79;
            return new Rectangle(
                GetSystemMetrics(SmXVirtualScreen),
                GetSystemMetrics(SmYVirtualScreen),
                GetSystemMetrics(SmCxVirtualScreen),
                GetSystemMetrics(SmCyVirtualScreen));
        }

        private static bool IsCloaked(IntPtr window)
        {
            try
            {
                int cloaked;
                return DwmGetWindowAttribute(
                    window,
                    DwmwaCloaked,
                    out cloaked,
                    sizeof(int)) >= 0 && cloaked != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        private static string ReadClassName(IntPtr window)
        {
            StringBuilder text = new StringBuilder(256);
            int count = GetClassName(window, text, text.Capacity);
            return count > 0 ? text.ToString(0, count) : string.Empty;
        }

        private static string ReadWindowText(IntPtr window)
        {
            StringBuilder text = new StringBuilder(512);
            int count = GetWindowText(window, text, text.Capacity);
            return count > 0 ? text.ToString(0, count) : string.Empty;
        }

        private static bool TryConvertBounds(
            System.Windows.Rect source,
            Rectangle virtualBounds,
            out Rectangle result)
        {
            result = Rectangle.Empty;
            if (source.IsEmpty ||
                double.IsNaN(source.Left) ||
                double.IsNaN(source.Top) ||
                double.IsNaN(source.Right) ||
                double.IsNaN(source.Bottom) ||
                double.IsInfinity(source.Left) ||
                double.IsInfinity(source.Top) ||
                double.IsInfinity(source.Right) ||
                double.IsInfinity(source.Bottom))
            {
                return false;
            }

            double left = Math.Max(source.Left, virtualBounds.Left);
            double top = Math.Max(source.Top, virtualBounds.Top);
            double right = Math.Min(source.Right, virtualBounds.Right);
            double bottom = Math.Min(source.Bottom, virtualBounds.Bottom);
            if (right <= left || bottom <= top)
            {
                return false;
            }

            result = Rectangle.FromLTRB(
                (int)Math.Floor(left),
                (int)Math.Floor(top),
                (int)Math.Ceiling(right),
                (int)Math.Ceiling(bottom));
            return IsUsable(result);
        }

        private static bool IsUsable(Rectangle bounds)
        {
            return bounds.Width >= MinimumDimension &&
                bounds.Height >= MinimumDimension &&
                Area(bounds) >= MinimumArea;
        }

        private static long Area(Rectangle bounds)
        {
            return (long)bounds.Width * bounds.Height;
        }

        private sealed class RootDescriptor
        {
            internal readonly IntPtr Handle;
            internal readonly Rectangle Bounds;
            internal readonly ShellZoneKind Kind;
            internal readonly int ProcessId;
            internal readonly string ClassName;
            internal readonly string Title;
            internal readonly bool IsTaskbar;
            internal readonly bool PublishRootBounds;
            internal readonly ReadOnlyCollection<AutomationRootDescriptor>
                AutomationRoots;

            internal RootDescriptor(
                IntPtr handle,
                Rectangle bounds,
                ShellZoneKind kind,
                int processId,
                string className,
                string title,
                bool isTaskbar,
                bool publishRootBounds,
                ReadOnlyCollection<AutomationRootDescriptor> automationRoots)
            {
                Handle = handle;
                Bounds = bounds;
                Kind = kind;
                ProcessId = processId;
                ClassName = className ?? string.Empty;
                Title = title ?? string.Empty;
                IsTaskbar = isTaskbar;
                PublishRootBounds = publishRootBounds;
                AutomationRoots = automationRoots ??
                    new ReadOnlyCollection<AutomationRootDescriptor>(
                        new AutomationRootDescriptor[0]);
            }
        }

        private sealed class AutomationRootDescriptor
        {
            internal readonly IntPtr Handle;
            internal readonly int ProcessId;
            internal readonly string ClassName;
            internal readonly Rectangle Bounds;
            internal readonly bool IsShellRoot;

            internal AutomationRootDescriptor(
                IntPtr handle,
                int processId,
                string className,
                Rectangle bounds,
                bool isShellRoot)
            {
                Handle = handle;
                ProcessId = processId;
                ClassName = className ?? string.Empty;
                Bounds = bounds;
                IsShellRoot = isShellRoot;
            }
        }

        private struct ZoneKey : IEquatable<ZoneKey>
        {
            private readonly Rectangle _bounds;
            private readonly IntPtr _rootWindowHandle;

            internal ZoneKey(Rectangle bounds, IntPtr rootWindowHandle)
            {
                _bounds = bounds;
                _rootWindowHandle = rootWindowHandle;
            }

            public bool Equals(ZoneKey other)
            {
                return _bounds.Equals(other._bounds) &&
                    _rootWindowHandle == other._rootWindowHandle;
            }

            public override bool Equals(object value)
            {
                return value is ZoneKey && Equals((ZoneKey)value);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (_bounds.GetHashCode() * 397) ^
                        _rootWindowHandle.GetHashCode();
                }
            }
        }

        private struct ScanResult
        {
            internal int ZoneCount;
            internal int ActionableZoneCount;
        }

        private struct ElementData
        {
            internal System.Windows.Rect Bounds;
            internal bool IsOffscreen;
            internal int ProcessId;
            internal ControlType ControlType;
            internal string Name;
            internal string AutomationId;
            internal string ClassName;
            internal bool HasInvokePattern;
            internal bool HasSelectionItemPattern;
            internal bool HasTogglePattern;
            internal bool HasExpandCollapsePattern;
            internal bool HasRangeValuePattern;
        }
    }
}
