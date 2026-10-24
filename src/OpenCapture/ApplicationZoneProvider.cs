using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace OpenCapture
{
    /// <summary>
    /// An immutable UI Automation region belonging to a normal application.
    /// Unlike ShellZoneProvider, these regions cover browser and app content.
    /// </summary>
    internal sealed class ApplicationZone
    {
        internal ApplicationZone(Rectangle bounds, bool actionable)
        {
            Bounds = bounds;
            IsActionable = actionable;
        }

        internal Rectangle Bounds { get; private set; }

        internal bool IsActionable { get; private set; }
    }

    /// <summary>
    /// Lazily discovers semantic regions in the application below the cursor.
    /// All UI Automation work runs on a dedicated MTA thread so opening and
    /// moving the capture overlay never waits for a foreign accessibility tree.
    /// </summary>
    internal sealed class ApplicationZoneProvider : IDisposable
    {
        private const int MinimumDimension = 8;
        private const int MaximumElements = 4096;
        private const int MaximumPublishedZones = 1536;
        private const int MaximumCandidatesAtPoint = 24;
        private const int ScanBudgetMilliseconds = 1200;

        private readonly object syncRoot = new object();
        private readonly Dictionary<IntPtr, RootState> roots =
            new Dictionary<IntPtr, RootState>();
        private bool disposed;
        private int pendingScans;

        internal bool IsComplete
        {
            get
            {
                lock (syncRoot)
                {
                    return pendingScans == 0;
                }
            }
        }

        internal void StartScan(IntPtr rootWindow, Rectangle rootBounds)
        {
            if (!CanScan(rootWindow, rootBounds))
            {
                return;
            }

            RootState state;
            lock (syncRoot)
            {
                if (disposed || roots.ContainsKey(rootWindow))
                {
                    return;
                }

                state = new RootState(rootWindow, rootBounds);
                roots.Add(rootWindow, state);
                pendingScans++;
            }

            state.DeadlineTimer = new Timer(
                delegate { ExpireScan(state); },
                null,
                ScanBudgetMilliseconds,
                Timeout.Infinite);

            Thread worker = new Thread(
                new ThreadStart(delegate { ScanAndPublish(state); }));
            worker.IsBackground = true;
            worker.Name = "OpenCapture application zone scan";
            try
            {
                worker.SetApartmentState(ApartmentState.MTA);
            }
            catch (InvalidOperationException)
            {
            }
            worker.Start();
        }

        internal IList<ApplicationZone> FindZonesAt(
            IntPtr rootWindow,
            Rectangle rootBounds,
            Point screenPoint)
        {
            StartScan(rootWindow, rootBounds);

            ReadOnlyCollection<ApplicationZone> snapshot;
            lock (syncRoot)
            {
                RootState state;
                if (disposed ||
                    !roots.TryGetValue(rootWindow, out state) ||
                    !state.IsComplete)
                {
                    return new ApplicationZone[0];
                }
                snapshot = state.Zones;
            }

            return SelectCandidatesAt(snapshot, screenPoint);
        }

        internal static IList<ApplicationZone> SelectCandidatesAt(
            IList<ApplicationZone> snapshot,
            Point screenPoint)
        {
            List<ApplicationZone> matches = new List<ApplicationZone>();
            int index;
            for (index = 0; index < snapshot.Count; index++)
            {
                ApplicationZone candidate = snapshot[index];
                if (candidate.Bounds.Contains(screenPoint))
                {
                    matches.Add(candidate);
                }
            }

            matches.Sort(delegate(ApplicationZone left, ApplicationZone right)
            {
                long leftArea = Area(left.Bounds);
                long rightArea = Area(right.Bounds);
                int areaOrder = leftArea.CompareTo(rightArea);
                if (areaOrder != 0)
                {
                    return areaOrder;
                }
                if (left.IsActionable != right.IsActionable)
                {
                    return left.IsActionable ? -1 : 1;
                }
                return 0;
            });

            if (matches.Count > MaximumCandidatesAtPoint)
            {
                matches.RemoveRange(
                    MaximumCandidatesAtPoint,
                    matches.Count - MaximumCandidatesAtPoint);
            }
            return matches;
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                disposed = true;
                foreach (RootState state in roots.Values)
                {
                    if (state.DeadlineTimer != null)
                    {
                        state.DeadlineTimer.Dispose();
                    }
                }
                roots.Clear();
                pendingScans = 0;
            }
        }

        private void ScanAndPublish(RootState state)
        {
            List<ApplicationZone> discovered = new List<ApplicationZone>();
            try
            {
                Discover(state, discovered);
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
                // Accessibility providers belong to other processes. A broken or
                // disconnected provider must only disable semantic refinement.
            }

            lock (syncRoot)
            {
                if (disposed)
                {
                    return;
                }

                RootState current;
                if (roots.TryGetValue(state.Handle, out current) &&
                    ReferenceEquals(current, state) &&
                    !state.IsComplete)
                {
                    state.Zones = BuildSnapshot(discovered);
                    state.IsComplete = true;
                    if (state.DeadlineTimer != null)
                    {
                        state.DeadlineTimer.Dispose();
                        state.DeadlineTimer = null;
                    }
                    pendingScans = Math.Max(0, pendingScans - 1);
                }
            }
        }

        private void ExpireScan(RootState state)
        {
            lock (syncRoot)
            {
                if (disposed)
                {
                    return;
                }

                RootState current;
                if (roots.TryGetValue(state.Handle, out current) &&
                    ReferenceEquals(current, state) &&
                    !state.IsComplete)
                {
                    state.IsComplete = true;
                    state.DeadlineTimer = null;
                    pendingScans = Math.Max(0, pendingScans - 1);
                }
            }
        }

        private static void Discover(
            RootState state,
            List<ApplicationZone> result)
        {
            if (!RootStillMatches(state))
            {
                return;
            }

            Stopwatch timer = Stopwatch.StartNew();
            AutomationElement root = AutomationElement.FromHandle(state.Handle);
            if (root == null)
            {
                return;
            }

            AutomationElementCollection elements;
            CacheRequest cache = new CacheRequest();
            cache.AutomationElementMode = AutomationElementMode.None;
            cache.TreeFilter = Automation.ControlViewCondition;
            cache.TreeScope = TreeScope.Element;
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.IsInvokePatternAvailableProperty);
            cache.Add(AutomationElement.IsSelectionItemPatternAvailableProperty);
            cache.Add(AutomationElement.IsTogglePatternAvailableProperty);
            cache.Add(AutomationElement.IsExpandCollapsePatternAvailableProperty);
            cache.Add(AutomationElement.IsRangeValuePatternAvailableProperty);

            using (cache.Activate())
            {
                elements = root.FindAll(
                    TreeScope.Descendants,
                    Automation.ControlViewCondition);
            }

            HashSet<Rectangle> actionableBounds = new HashSet<Rectangle>();
            HashSet<Rectangle> structuralBounds = new HashSet<Rectangle>();
            int count = elements == null
                ? 0
                : Math.Min(elements.Count, MaximumElements);
            int index;
            for (index = 0; index < count; index++)
            {
                if (result.Count >= MaximumPublishedZones ||
                    timer.ElapsedMilliseconds > ScanBudgetMilliseconds)
                {
                    break;
                }

                ApplicationZone candidate;
                if (!TryCreateZone(elements[index], state.Bounds, out candidate))
                {
                    continue;
                }

                HashSet<Rectangle> target = candidate.IsActionable
                    ? actionableBounds
                    : structuralBounds;
                if (target.Add(candidate.Bounds))
                {
                    result.Add(candidate);
                }
            }
        }

        private static bool TryCreateZone(
            AutomationElement element,
            Rectangle rootBounds,
            out ApplicationZone zone)
        {
            zone = null;
            try
            {
                if (GetCachedBoolean(
                        element,
                        AutomationElement.IsOffscreenProperty,
                        true))
                {
                    return false;
                }

                object rawBounds = element.GetCachedPropertyValue(
                    AutomationElement.BoundingRectangleProperty,
                    true);
                if (!(rawBounds is System.Windows.Rect))
                {
                    return false;
                }

                System.Windows.Rect automationBounds =
                    (System.Windows.Rect)rawBounds;
                if (automationBounds.IsEmpty ||
                    double.IsNaN(automationBounds.X) ||
                    double.IsNaN(automationBounds.Y) ||
                    double.IsNaN(automationBounds.Width) ||
                    double.IsNaN(automationBounds.Height) ||
                    double.IsInfinity(automationBounds.X) ||
                    double.IsInfinity(automationBounds.Y) ||
                    double.IsInfinity(automationBounds.Width) ||
                    double.IsInfinity(automationBounds.Height))
                {
                    return false;
                }

                Rectangle bounds = Rectangle.Intersect(
                    Rectangle.Round(new RectangleF(
                        (float)automationBounds.X,
                        (float)automationBounds.Y,
                        (float)automationBounds.Width,
                        (float)automationBounds.Height)),
                    rootBounds);
                if (bounds.Width < MinimumDimension ||
                    bounds.Height < MinimumDimension ||
                    IsEffectivelyRoot(bounds, rootBounds))
                {
                    return false;
                }

                ControlType controlType = GetCachedControlType(element);
                bool actionable = IsActionable(element, controlType);
                if (!actionable && !IsStructural(controlType))
                {
                    return false;
                }

                zone = new ApplicationZone(bounds, actionable);
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

        private static bool IsActionable(
            AutomationElement element,
            ControlType controlType)
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
                controlType == ControlType.TreeItem ||
                GetCachedBoolean(
                    element,
                    AutomationElement.IsInvokePatternAvailableProperty,
                    false) ||
                GetCachedBoolean(
                    element,
                    AutomationElement.IsSelectionItemPatternAvailableProperty,
                    false) ||
                GetCachedBoolean(
                    element,
                    AutomationElement.IsTogglePatternAvailableProperty,
                    false) ||
                GetCachedBoolean(
                    element,
                    AutomationElement.IsExpandCollapsePatternAvailableProperty,
                    false) ||
                GetCachedBoolean(
                    element,
                    AutomationElement.IsRangeValuePatternAvailableProperty,
                    false);
        }

        private static bool IsStructural(ControlType controlType)
        {
            return controlType == ControlType.Calendar ||
                controlType == ControlType.Custom ||
                controlType == ControlType.DataGrid ||
                controlType == ControlType.Document ||
                controlType == ControlType.Group ||
                controlType == ControlType.Header ||
                controlType == ControlType.Image ||
                controlType == ControlType.List ||
                controlType == ControlType.Menu ||
                controlType == ControlType.Pane ||
                controlType == ControlType.Tab ||
                controlType == ControlType.Table ||
                controlType == ControlType.ToolBar ||
                controlType == ControlType.Tree ||
                controlType == ControlType.Window;
        }

        private static ControlType GetCachedControlType(AutomationElement element)
        {
            object value = element.GetCachedPropertyValue(
                AutomationElement.ControlTypeProperty,
                true);
            return value as ControlType;
        }

        private static bool GetCachedBoolean(
            AutomationElement element,
            AutomationProperty property,
            bool fallback)
        {
            object value = element.GetCachedPropertyValue(property, true);
            return value is bool ? (bool)value : fallback;
        }

        private static bool CanScan(IntPtr rootWindow, Rectangle rootBounds)
        {
            if (rootWindow == IntPtr.Zero || rootBounds.IsEmpty)
            {
                return false;
            }

            string className = NativeMethods.GetWindowClassName(rootWindow);
            return !string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal) &&
                !string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.Ordinal) &&
                !string.Equals(className, "Progman", StringComparison.Ordinal) &&
                !string.Equals(className, "WorkerW", StringComparison.Ordinal);
        }

        private static bool RootStillMatches(RootState state)
        {
            if (!NativeMethods.IsWindow(state.Handle) ||
                !NativeMethods.IsWindowVisible(state.Handle) ||
                NativeMethods.IsIconic(state.Handle) ||
                NativeMethods.IsWindowCloaked(state.Handle))
            {
                return false;
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(state.Handle, out processId);
            return state.ProcessId == 0 || processId == state.ProcessId;
        }

        private static bool IsEffectivelyRoot(
            Rectangle candidate,
            Rectangle root)
        {
            Rectangle intersection = Rectangle.Intersect(candidate, root);
            long rootArea = Area(root);
            long intersectionArea = Area(intersection);
            return rootArea > 0 && intersectionArea * 1000L >= rootArea * 975L;
        }

        private static ReadOnlyCollection<ApplicationZone> BuildSnapshot(
            IList<ApplicationZone> zones)
        {
            return new ReadOnlyCollection<ApplicationZone>(
                new List<ApplicationZone>(zones).ToArray());
        }

        private static long Area(Rectangle bounds)
        {
            return (long)Math.Max(0, bounds.Width) *
                Math.Max(0, bounds.Height);
        }

        private sealed class RootState
        {
            internal readonly IntPtr Handle;
            internal readonly Rectangle Bounds;
            internal readonly uint ProcessId;
            internal ReadOnlyCollection<ApplicationZone> Zones;
            internal bool IsComplete;
            internal Timer DeadlineTimer;

            internal RootState(IntPtr handle, Rectangle bounds)
            {
                Handle = handle;
                Bounds = bounds;
                uint processId;
                NativeMethods.GetWindowThreadProcessId(handle, out processId);
                ProcessId = processId;
                Zones = new ReadOnlyCollection<ApplicationZone>(
                    new ApplicationZone[0]);
            }
        }
    }
}
