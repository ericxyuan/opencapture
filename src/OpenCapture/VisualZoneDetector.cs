using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OpenCapture
{
    /// <summary>
    /// Finds visually coherent filled regions inside a semantic/window fallback.
    /// All public-facing coordinates are physical virtual-desktop pixels.
    ///
    /// The immutable captured bitmap is copied once with LockBits, then represented
    /// by a box-filtered grid capped at roughly one million cells. Region discovery
    /// is lazy and results are cached, so normal pointer movement does not repeatedly
    /// flood-fill the same card or panel. The local colour mode is used as the seed;
    /// this deliberately makes text and small icons holes in their surrounding fill.
    /// </summary>
    internal sealed class VisualZoneDetector : IDisposable
    {
        private const int MaximumGridCells = 1000000;
        private const int MaximumFloodCells = 600000;
        private const int MaximumCacheEntries = 24;
        private const int QuantizedColourCount = 4096;
        private const int MaximumPrototypeCount = 4;
        private const int MinimumZoneDimension = 12;
        private const int MinimumZoneArea = 320;
        private const int MinimumMatchedPixelArea = 256;
        private const int MinimumComponentFillPercent = 25;
        private const int RefinementMatchPercent = 55;
        private const int CompactGlyphMaximumDimension = 32;
        private const int EdgeRefinementSamples = 32;
        private const int CandidateBoundarySamples = 48;
        private const int CandidateBoundaryDistance = 6;
        private const int CandidateBoundaryCoveragePercent = 55;
        private const int CandidateContentDistance = 14;
        private const int CandidateMinimumContentPixelArea = 48;
        private const int MinimumOutlinedPanelDimension = 56;
        private const int GradientNeighbourTolerance = 7;
        private const int GradientDriftAllowance = 20;
        private const int AttachedGradientDriftAllowance = 15;

        private readonly object syncRoot;
        private readonly Rectangle desktopBounds;
        private readonly int sourceWidth;
        private readonly int sourceHeight;
        private readonly int sampleStep;
        private readonly int gridWidth;
        private readonly int gridHeight;
        private readonly List<CacheEntry> cache;

        private int[] sourcePixels;
        private int[] gridColours;
        private int[] visitStamps;
        private int[] floodQueue;
        private int[] componentColumnCounts;
        private int[] componentRowCounts;
        private int[] clusterWeights;
        private int[] clusterCounts;
        private int[] clusterRedSums;
        private int[] clusterGreenSums;
        private int[] clusterBlueSums;
        private int visitGeneration;
        private bool disposed;
        private bool hasLastQuery;
        private Point lastQueryPoint;
        private Rectangle lastQueryParent;
        private Rectangle lastQueryBounds;
        private bool lastQueryFoundZone;

        internal VisualZoneDetector(CapturedFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException("frame");
            }

            Bitmap bitmap = frame.Bitmap;
            if (bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                throw new ArgumentException("The captured frame must have a non-empty bitmap.", "frame");
            }
            if (frame.DesktopBounds.Width != bitmap.Width ||
                frame.DesktopBounds.Height != bitmap.Height)
            {
                throw new ArgumentException(
                    "The captured bitmap dimensions must match its desktop bounds.",
                    "frame");
            }

            syncRoot = new object();
            desktopBounds = frame.DesktopBounds;
            sourceWidth = bitmap.Width;
            sourceHeight = bitmap.Height;
            sampleStep = CalculateSampleStep(sourceWidth, sourceHeight);
            gridWidth = DivideRoundingUp(sourceWidth, sampleStep);
            gridHeight = DivideRoundingUp(sourceHeight, sampleStep);
            cache = new List<CacheEntry>(MaximumCacheEntries);

            sourcePixels = CopyPixels(bitmap);
            try
            {
                gridColours = BuildGrid(sourcePixels);
                visitStamps = new int[gridWidth * gridHeight];
                floodQueue = new int[gridWidth * gridHeight];
                componentColumnCounts = new int[gridWidth];
                componentRowCounts = new int[gridHeight];
                clusterWeights = new int[QuantizedColourCount];
                clusterCounts = new int[QuantizedColourCount];
                clusterRedSums = new int[QuantizedColourCount];
                clusterGreenSums = new int[QuantizedColourCount];
                clusterBlueSums = new int[QuantizedColourCount];
            }
            catch
            {
                sourcePixels = null;
                gridColours = null;
                visitStamps = null;
                floodQueue = null;
                componentColumnCounts = null;
                componentRowCounts = null;
                clusterWeights = null;
                clusterCounts = null;
                clusterRedSums = null;
                clusterGreenSums = null;
                clusterBlueSums = null;
                throw;
            }
        }

        /// <summary>
        /// The physical-pixel width represented by one analysis-grid cell. This is
        /// one on smaller captures and normally three on a 3840 by 2160 desktop.
        /// </summary>
        internal int SampleStep
        {
            get { return sampleStep; }
        }

        /// <summary>
        /// Returns a meaningful visual block at the point, or the supplied fallback
        /// clipped to the captured desktop when the pixels do not form such a block.
        /// </summary>
        internal Rectangle FindZoneAt(Point physicalScreenPoint, Rectangle enclosingFallback)
        {
            Rectangle result;
            TryFindZoneAt(physicalScreenPoint, enclosingFallback, out result);
            return result;
        }

        /// <summary>
        /// Returns true only when a visual sub-zone was found. On false, bounds is
        /// still set to the enclosing fallback clipped to the captured desktop.
        /// </summary>
        internal bool TryFindZoneAt(
            Point physicalScreenPoint,
            Rectangle enclosingFallback,
            out Rectangle bounds)
        {
            lock (syncRoot)
            {
                ThrowIfDisposed();

                Rectangle parent = Rectangle.Intersect(desktopBounds, enclosingFallback);
                bounds = parent;
                if (parent.Width <= 0 || parent.Height <= 0 ||
                    !parent.Contains(physicalScreenPoint))
                {
                    return false;
                }

                // Refinement polling can ask about the same frozen pixels before
                // a semantic scan finishes. Cache misses as well as hits so a
                // uniform surface does not repeatedly trigger full flood fills.
                if (hasLastQuery && lastQueryPoint == physicalScreenPoint &&
                    lastQueryParent == parent)
                {
                    bounds = lastQueryBounds;
                    return lastQueryFoundZone;
                }

                Prototype[] prototypes = FindPrototypeColours(physicalScreenPoint, parent);
                CacheEntry cached;
                if (TryFindCached(physicalScreenPoint, parent, prototypes, out cached))
                {
                    bounds = cached.Bounds;
                    CacheLastQuery(physicalScreenPoint, parent, bounds, true);
                    return true;
                }

                VisualCandidate bestCandidate = default(VisualCandidate);
                bool hasCandidate = false;
                int prototypeIndex;
                for (prototypeIndex = 0;
                     prototypeIndex < prototypes.Length;
                     prototypeIndex++)
                {
                    Region region = GrowRegion(
                        physicalScreenPoint,
                        parent,
                        prototypes[prototypeIndex]);
                    Rectangle visualBounds;
                    if (!TryCreateVisualBounds(
                        physicalScreenPoint,
                        parent,
                        prototypes[prototypeIndex],
                        region,
                        out visualBounds))
                    {
                        continue;
                    }

                    VisualCandidate candidate = new VisualCandidate(
                        visualBounds,
                        prototypes[prototypeIndex],
                        prototypeIndex,
                        parent);
                    if (!hasCandidate || candidate.IsBetterThan(bestCandidate))
                    {
                        bestCandidate = candidate;
                        hasCandidate = true;
                    }
                }

                // A rounded card can share its fill with an adjoining panel. Its
                // flood component then includes both surfaces even though the card
                // still has clear top, bottom, and free-side edges. Also run this
                // pass when a normal result exists: it can expand a partial colour
                // band to the complete pill/card without replacing a genuinely
                // smaller nested control.
                VisualCandidate bestAttachedCandidate =
                    default(VisualCandidate);
                bool hasAttachedCandidate = false;
                for (prototypeIndex = 0;
                     prototypeIndex < prototypes.Length;
                     prototypeIndex++)
                {
                    Rectangle attachedBounds;
                    if (!TryFindAttachedRoundedBounds(
                        physicalScreenPoint,
                        parent,
                        prototypes[prototypeIndex],
                        out attachedBounds))
                    {
                        continue;
                    }

                    VisualCandidate attachedCandidate = new VisualCandidate(
                        attachedBounds,
                        prototypes[prototypeIndex],
                        prototypeIndex,
                        parent);
                    bool attachedExpandsCurrent = hasAttachedCandidate &&
                        ShouldPreferRoundedOuterBounds(
                            bestAttachedCandidate.Bounds,
                            attachedCandidate.Bounds);
                    bool currentExpandsAttached = hasAttachedCandidate &&
                        ShouldPreferRoundedOuterBounds(
                            attachedCandidate.Bounds,
                            bestAttachedCandidate.Bounds);
                    if (!hasAttachedCandidate ||
                        attachedExpandsCurrent ||
                        (!currentExpandsAttached &&
                            attachedCandidate.IsBetterThan(
                                bestAttachedCandidate)))
                    {
                        bestAttachedCandidate = attachedCandidate;
                        hasAttachedCandidate = true;
                    }
                }

                if (hasAttachedCandidate &&
                    (!hasCandidate ||
                        ShouldPreferRoundedOuterBounds(
                            bestCandidate.Bounds,
                            bestAttachedCandidate.Bounds) ||
                        ShouldPreferAttachedSubzone(
                            bestCandidate.Bounds,
                            bestAttachedCandidate.Bounds)))
                {
                    bestCandidate = bestAttachedCandidate;
                    hasCandidate = true;
                }

                // Some custom-drawn side panels deliberately reuse almost the
                // same fill as their parent and are distinguished only by a faint
                // one-pixel outline or shadow. The colour flood then merges the two
                // surfaces. Look for a complete enclosing edge rectangle around the
                // pointer, and validate every edge across its final full span so
                // text strokes and isolated separators cannot become zones.
                Rectangle outlinedBounds;
                if (TryFindEnclosingOutlinedPanel(
                    physicalScreenPoint,
                    parent,
                    out outlinedBounds))
                {
                    VisualCandidate outlinedCandidate = new VisualCandidate(
                        outlinedBounds,
                        prototypes[0],
                        prototypes.Length,
                        parent);
                    if (!hasCandidate ||
                        ShouldPreferRoundedOuterBounds(
                            bestCandidate.Bounds,
                            outlinedCandidate.Bounds) ||
                        outlinedCandidate.IsBetterThan(bestCandidate))
                    {
                        bestCandidate = outlinedCandidate;
                        hasCandidate = true;
                    }
                }

                if (!hasCandidate)
                {
                    CacheLastQuery(physicalScreenPoint, parent, bounds, false);
                    return false;
                }

                CacheResult(parent, bestCandidate.Bounds, bestCandidate.Prototype);
                bounds = bestCandidate.Bounds;
                CacheLastQuery(physicalScreenPoint, parent, bounds, true);
                return true;
            }
        }

        /// <summary>
        /// Validates an arbitrary semantic/UIA rectangle against the frozen pixels.
        /// A candidate is accepted when opposing visible edges isolate it from the
        /// enclosing surface, or when a transparent candidate contains a compact,
        /// isolated icon-like content cluster. A purely geometric subdivision of a
        /// continuous taskbar/flyout surface is rejected.
        /// </summary>
        internal bool TryValidateCandidate(
            Point physicalScreenPoint,
            Rectangle candidateBounds,
            Rectangle enclosingBounds,
            out Rectangle refinedBounds)
        {
            lock (syncRoot)
            {
                ThrowIfDisposed();
                refinedBounds = Rectangle.Empty;

                Rectangle enclosing = Rectangle.Intersect(desktopBounds, enclosingBounds);
                Rectangle candidate = Rectangle.Intersect(enclosing, candidateBounds);
                if (!IsMeaningful(candidate) ||
                    candidate == enclosing ||
                    !candidate.Contains(physicalScreenPoint))
                {
                    return false;
                }

                Rectangle edgeBounds;
                if (TryRefineCandidateEdges(candidate, enclosing, out edgeBounds) &&
                    IsMeaningful(edgeBounds) &&
                    edgeBounds.Contains(physicalScreenPoint))
                {
                    refinedBounds = edgeBounds;
                    return true;
                }

                if (HasIsolatedCandidateContent(candidate, enclosing))
                {
                    // Transparent taskbar buttons have no defensible pixel border;
                    // their semantic cell is still the correct capture target. Keep
                    // those bounds instead of shrinking the target to its icon.
                    refinedBounds = candidate;
                    return true;
                }

                return false;
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                cache.Clear();
                hasLastQuery = false;
                sourcePixels = null;
                gridColours = null;
                visitStamps = null;
                floodQueue = null;
                componentColumnCounts = null;
                componentRowCounts = null;
                clusterWeights = null;
                clusterCounts = null;
                clusterRedSums = null;
                clusterGreenSums = null;
                clusterBlueSums = null;
            }
        }

        private int[] CopyPixels(Bitmap bitmap)
        {
            Rectangle bitmapBounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = null;
            try
            {
                PixelFormat format = bitmap.PixelFormat;
                if (format != PixelFormat.Format32bppRgb &&
                    format != PixelFormat.Format32bppArgb &&
                    format != PixelFormat.Format32bppPArgb)
                {
                    format = PixelFormat.Format32bppArgb;
                }

                data = bitmap.LockBits(bitmapBounds, ImageLockMode.ReadOnly, format);
                if (Math.Abs(data.Stride) < bitmap.Width * 4)
                {
                    throw new InvalidOperationException("The captured bitmap has an unsupported stride.");
                }

                int[] pixels = new int[bitmap.Width * bitmap.Height];
                int row;
                for (row = 0; row < bitmap.Height; row++)
                {
                    IntPtr rowPointer = IntPtr.Add(data.Scan0, row * data.Stride);
                    Marshal.Copy(rowPointer, pixels, row * bitmap.Width, bitmap.Width);
                }

                return pixels;
            }
            finally
            {
                if (data != null)
                {
                    bitmap.UnlockBits(data);
                }
            }
        }

        private int[] BuildGrid(int[] pixels)
        {
            int[] result = new int[gridWidth * gridHeight];
            int gridY;
            for (gridY = 0; gridY < gridHeight; gridY++)
            {
                int sourceTop = gridY * sampleStep;
                int sourceBottom = Math.Min(sourceHeight, sourceTop + sampleStep);
                int gridX;
                for (gridX = 0; gridX < gridWidth; gridX++)
                {
                    int sourceLeft = gridX * sampleStep;
                    int sourceRight = Math.Min(sourceWidth, sourceLeft + sampleStep);
                    int red = 0;
                    int green = 0;
                    int blue = 0;
                    int count = 0;
                    int y;
                    for (y = sourceTop; y < sourceBottom; y++)
                    {
                        int index = y * sourceWidth + sourceLeft;
                        int x;
                        for (x = sourceLeft; x < sourceRight; x++)
                        {
                            int colour = pixels[index++];
                            blue += colour & 0xff;
                            green += (colour >> 8) & 0xff;
                            red += (colour >> 16) & 0xff;
                            count++;
                        }
                    }

                    result[gridY * gridWidth + gridX] = PackColour(
                        red / count,
                        green / count,
                        blue / count);
                }
            }

            return result;
        }

        private Prototype[] FindPrototypeColours(Point screenPoint, Rectangle parent)
        {
            Array.Clear(clusterWeights, 0, clusterWeights.Length);
            Array.Clear(clusterCounts, 0, clusterCounts.Length);
            Array.Clear(clusterRedSums, 0, clusterRedSums.Length);
            Array.Clear(clusterGreenSums, 0, clusterGreenSums.Length);
            Array.Clear(clusterBlueSums, 0, clusterBlueSums.Length);

            GridBounds parentGrid = GetGridBounds(parent);
            Point local = ScreenToLocal(screenPoint);
            int centreX = Math.Min(gridWidth - 1, local.X / sampleStep);
            int centreY = Math.Min(gridHeight - 1, local.Y / sampleStep);
            // Keep the physical neighbourhood consistent across DPI/resolution.
            // The previous ten-cell cap reduced this to ten physical pixels at
            // 1080p, so a 24px app icon completely hid its 66px hover button.
            int radius = Math.Max(5, Math.Min(24, DivideRoundingUp(24, sampleStep)));

            int top = Math.Max(parentGrid.Top, centreY - radius);
            int bottom = Math.Min(parentGrid.Bottom, centreY + radius);
            int left = Math.Max(parentGrid.Left, centreX - radius);
            int right = Math.Min(parentGrid.Right, centreX + radius);
            int y;
            for (y = top; y <= bottom; y++)
            {
                int x;
                for (x = left; x <= right; x++)
                {
                    int distance = Math.Max(Math.Abs(x - centreX), Math.Abs(y - centreY));
                    int weight = radius + 2 - distance;
                    int colour = gridColours[y * gridWidth + x];
                    int bin = Quantize(colour);
                    clusterWeights[bin] += weight;
                    clusterCounts[bin]++;
                    clusterRedSums[bin] += Red(colour);
                    clusterGreenSums[bin] += Green(colour);
                    clusterBlueSums[bin] += Blue(colour);
                }
            }

            int[] bestBins = new int[MaximumPrototypeCount];
            int[] bestScores = new int[MaximumPrototypeCount];
            int bestCount = 0;
            int binIndex;
            for (binIndex = 0; binIndex < QuantizedColourCount; binIndex++)
            {
                int score = clusterWeights[binIndex];
                if (score <= 0)
                {
                    continue;
                }

                int insertAt = bestCount;
                int existing;
                for (existing = 0; existing < bestCount; existing++)
                {
                    if (score > bestScores[existing])
                    {
                        insertAt = existing;
                        break;
                    }
                }

                if (insertAt >= MaximumPrototypeCount)
                {
                    continue;
                }
                if (bestCount < MaximumPrototypeCount)
                {
                    bestCount++;
                }

                int move;
                for (move = bestCount - 1; move > insertAt; move--)
                {
                    bestBins[move] = bestBins[move - 1];
                    bestScores[move] = bestScores[move - 1];
                }
                bestBins[insertAt] = binIndex;
                bestScores[insertAt] = score;
            }

            List<Prototype> result = new List<Prototype>(MaximumPrototypeCount);
            int bestIndex;
            for (bestIndex = 0; bestIndex < bestCount; bestIndex++)
            {
                int bin = bestBins[bestIndex];
                int count = clusterCounts[bin];
                if (count <= 0)
                {
                    continue;
                }

                int colour = PackColour(
                    clusterRedSums[bin] / count,
                    clusterGreenSums[bin] / count,
                    clusterBlueSums[bin] / count);
                bool duplicate = false;
                int resultIndex;
                for (resultIndex = 0; resultIndex < result.Count; resultIndex++)
                {
                    if (ColourDistance(colour, result[resultIndex].Colour) <= 6)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate)
                {
                    continue;
                }

                int totalDeviation = 0;
                int deviationCount = 0;
                for (y = top; y <= bottom; y++)
                {
                    int x;
                    for (x = left; x <= right; x++)
                    {
                        int sample = gridColours[y * gridWidth + x];
                        if (Quantize(sample) == bin)
                        {
                            totalDeviation += ColourDistance(sample, colour);
                            deviationCount++;
                        }
                    }
                }

                int averageDeviation = deviationCount == 0
                    ? 0
                    : totalDeviation / deviationCount;
                // Treat gentle shading and antialiasing as one surface. A visual
                // zone should start at a defensible edge, not at every small step
                // in a background gradient.
                int tolerance = Math.Max(14, Math.Min(34, 14 + averageDeviation * 2));
                result.Add(new Prototype(colour, tolerance));
                if (result.Count == MaximumPrototypeCount)
                {
                    break;
                }
            }

            if (result.Count == 0)
            {
                int centreColour = gridColours[centreY * gridWidth + centreX];
                result.Add(new Prototype(centreColour, 16));
            }

            // A small, pointer-centred mode is intentionally considered before the
            // wider neighbourhood. This lets a hovered taskbar button or flyout tile
            // replace a previously cached group background. Using a mode rather than
            // the literal centre pixel still treats text and icon pixels as holes.
            Prototype immediate;
            if (TryFindImmediatePrototype(screenPoint, parent, out immediate))
            {
                int duplicateIndex = -1;
                int index;
                for (index = 0; index < result.Count; index++)
                {
                    if (ColourDistance(immediate.Colour, result[index].Colour) <= 5)
                    {
                        duplicateIndex = index;
                        break;
                    }
                }

                if (duplicateIndex >= 0)
                {
                    immediate = result[duplicateIndex];
                    result.RemoveAt(duplicateIndex);
                }
                result.Insert(0, immediate);
                while (result.Count > MaximumPrototypeCount)
                {
                    result.RemoveAt(result.Count - 1);
                }
            }

            return result.ToArray();
        }

        private bool TryFindImmediatePrototype(
            Point screenPoint,
            Rectangle parent,
            out Prototype prototype)
        {
            Array.Clear(clusterWeights, 0, clusterWeights.Length);
            Array.Clear(clusterCounts, 0, clusterCounts.Length);
            Array.Clear(clusterRedSums, 0, clusterRedSums.Length);
            Array.Clear(clusterGreenSums, 0, clusterGreenSums.Length);
            Array.Clear(clusterBlueSums, 0, clusterBlueSums.Length);

            GridBounds parentGrid = GetGridBounds(parent);
            Point local = ScreenToLocal(screenPoint);
            int centreX = Math.Min(gridWidth - 1, local.X / sampleStep);
            int centreY = Math.Min(gridHeight - 1, local.Y / sampleStep);
            int radius = Math.Max(2, Math.Min(8, DivideRoundingUp(9, sampleStep)));
            int top = Math.Max(parentGrid.Top, centreY - radius);
            int bottom = Math.Min(parentGrid.Bottom, centreY + radius);
            int left = Math.Max(parentGrid.Left, centreX - radius);
            int right = Math.Min(parentGrid.Right, centreX + radius);

            int totalCount = 0;
            int y;
            for (y = top; y <= bottom; y++)
            {
                int x;
                for (x = left; x <= right; x++)
                {
                    int distance = Math.Max(Math.Abs(x - centreX), Math.Abs(y - centreY));
                    int weight = radius + 3 - distance;
                    int colour = gridColours[y * gridWidth + x];
                    int bin = Quantize(colour);
                    clusterWeights[bin] += weight;
                    clusterCounts[bin]++;
                    clusterRedSums[bin] += Red(colour);
                    clusterGreenSums[bin] += Green(colour);
                    clusterBlueSums[bin] += Blue(colour);
                    totalCount++;
                }
            }

            int bestBin = -1;
            int bestWeight = 0;
            int binIndex;
            for (binIndex = 0; binIndex < QuantizedColourCount; binIndex++)
            {
                if (clusterWeights[binIndex] > bestWeight)
                {
                    bestWeight = clusterWeights[binIndex];
                    bestBin = binIndex;
                }
            }

            if (bestBin < 0 ||
                clusterCounts[bestBin] < 3 ||
                clusterCounts[bestBin] * 6 < totalCount)
            {
                prototype = default(Prototype);
                return false;
            }

            int count = clusterCounts[bestBin];
            int colourMean = PackColour(
                clusterRedSums[bestBin] / count,
                clusterGreenSums[bestBin] / count,
                clusterBlueSums[bestBin] / count);
            int totalDeviation = 0;
            int deviationCount = 0;
            for (y = top; y <= bottom; y++)
            {
                int x;
                for (x = left; x <= right; x++)
                {
                    int sample = gridColours[y * gridWidth + x];
                    if (Quantize(sample) == bestBin)
                    {
                        totalDeviation += ColourDistance(sample, colourMean);
                        deviationCount++;
                    }
                }
            }

            int averageDeviation = deviationCount == 0
                ? 0
                : totalDeviation / deviationCount;
            int tolerance = Math.Max(13, Math.Min(30, 13 + averageDeviation * 2));
            prototype = new Prototype(colourMean, tolerance);
            return true;
        }

        private Region GrowRegion(Point screenPoint, Rectangle parent, Prototype prototype)
        {
            GridBounds limits = GetGridBounds(parent);
            Array.Clear(componentColumnCounts, limits.Left, limits.Width);
            Array.Clear(componentRowCounts, limits.Top, limits.Height);
            int seedIndex;
            if (!TryFindSeed(screenPoint, limits, prototype, out seedIndex))
            {
                return Region.Empty;
            }

            visitGeneration++;
            if (visitGeneration == int.MaxValue)
            {
                Array.Clear(visitStamps, 0, visitStamps.Length);
                visitGeneration = 1;
            }

            int generation = visitGeneration;
            int head = 0;
            int tail = 0;
            floodQueue[tail++] = seedIndex;
            visitStamps[seedIndex] = generation;

            int seedY = seedIndex / gridWidth;
            int seedX = seedIndex - seedY * gridWidth;
            int minimumX = seedX;
            int maximumX = seedX;
            int minimumY = seedY;
            int maximumY = seedY;
            int count = 0;
            int parentCellCount = limits.Width * limits.Height;
            int floodLimit = Math.Min(parentCellCount, MaximumFloodCells);

            while (head < tail)
            {
                int index = floodQueue[head++];
                int y = index / gridWidth;
                int x = index - y * gridWidth;
                count++;
                componentColumnCounts[x]++;
                componentRowCounts[y]++;

                if (x < minimumX) minimumX = x;
                if (x > maximumX) maximumX = x;
                if (y < minimumY) minimumY = y;
                if (y > maximumY) maximumY = y;

                if (count >= floodLimit && count < parentCellCount)
                {
                    return Region.AbortedRegion;
                }

                int neighbourColour = gridColours[index];
                TryEnqueue(x - 1, y, limits, prototype, neighbourColour, generation, ref tail);
                TryEnqueue(x + 1, y, limits, prototype, neighbourColour, generation, ref tail);
                TryEnqueue(x, y - 1, limits, prototype, neighbourColour, generation, ref tail);
                TryEnqueue(x, y + 1, limits, prototype, neighbourColour, generation, ref tail);
            }

            return TrimSparseRegionEdges(new Region(
                minimumX,
                minimumY,
                maximumX,
                maximumY,
                count,
                false), generation);
        }

        private Region TrimSparseRegionEdges(Region region, int generation)
        {
            if (region.IsEmpty || region.Aborted)
            {
                return region;
            }

            int maximumColumnSupport = 0;
            int x;
            for (x = region.MinimumX; x <= region.MaximumX; x++)
            {
                maximumColumnSupport = Math.Max(
                    maximumColumnSupport,
                    componentColumnCounts[x]);
            }

            int maximumRowSupport = 0;
            int y;
            for (y = region.MinimumY; y <= region.MaximumY; y++)
            {
                maximumRowSupport = Math.Max(
                    maximumRowSupport,
                    componentRowCounts[y]);
            }

            // Ignore one-cell text strokes, shadows, and narrow same-colour paths
            // that otherwise make an app-sized component inherit its group bounds.
            // Rounded corners keep substantially more than one eighth of the peak
            // support and therefore survive this robust-extrema pass.
            int columnThreshold = maximumColumnSupport >= 8
                ? Math.Max(2, maximumColumnSupport / 8)
                : 1;
            int rowThreshold = maximumRowSupport >= 8
                ? Math.Max(2, maximumRowSupport / 8)
                : 1;
            int minimumX = region.MinimumX;
            int maximumX = region.MaximumX;
            int minimumY = region.MinimumY;
            int maximumY = region.MaximumY;
            while (minimumX <= maximumX &&
                   componentColumnCounts[minimumX] < columnThreshold)
            {
                minimumX++;
            }
            while (maximumX >= minimumX &&
                   componentColumnCounts[maximumX] < columnThreshold)
            {
                maximumX--;
            }
            while (minimumY <= maximumY &&
                   componentRowCounts[minimumY] < rowThreshold)
            {
                minimumY++;
            }
            while (maximumY >= minimumY &&
                   componentRowCounts[maximumY] < rowThreshold)
            {
                maximumY--;
            }

            if (minimumX > maximumX || minimumY > maximumY)
            {
                return Region.Empty;
            }

            int trimmedCount = 0;
            for (y = minimumY; y <= maximumY; y++)
            {
                int index = y * gridWidth + minimumX;
                for (x = minimumX; x <= maximumX; x++)
                {
                    if (visitStamps[index++] == generation)
                    {
                        trimmedCount++;
                    }
                }
            }

            return new Region(
                minimumX,
                minimumY,
                maximumX,
                maximumY,
                trimmedCount,
                false);
        }

        private void TryEnqueue(
            int x,
            int y,
            GridBounds limits,
            Prototype prototype,
            int neighbourColour,
            int generation,
            ref int tail)
        {
            if (x < limits.Left || x > limits.Right ||
                y < limits.Top || y > limits.Bottom)
            {
                return;
            }

            int index = y * gridWidth + x;
            if (visitStamps[index] == generation ||
                visitStamps[index] == -generation)
            {
                return;
            }

            int candidateColour = gridColours[index];
            bool prototypeMatch = ColoursSimilar(
                candidateColour,
                prototype.Colour,
                prototype.Tolerance);
            bool gentleGradient = ColoursSimilar(
                    candidateColour,
                    neighbourColour,
                    GradientNeighbourTolerance) &&
                ColoursSimilar(
                    candidateColour,
                    prototype.Colour,
                    prototype.Tolerance + GradientDriftAllowance);
            if (prototypeMatch || gentleGradient)
            {
                visitStamps[index] = generation;
                floodQueue[tail++] = index;
            }
            else
            {
                visitStamps[index] = -generation;
            }
        }

        private bool TryFindSeed(
            Point screenPoint,
            GridBounds limits,
            Prototype prototype,
            out int seedIndex)
        {
            Point local = ScreenToLocal(screenPoint);
            int centreX = Math.Min(gridWidth - 1, local.X / sampleStep);
            int centreY = Math.Min(gridHeight - 1, local.Y / sampleStep);
            int maximumRadius = Math.Max(3, DivideRoundingUp(28, sampleStep));
            int radius;
            for (radius = 0; radius <= maximumRadius; radius++)
            {
                int top = Math.Max(limits.Top, centreY - radius);
                int bottom = Math.Min(limits.Bottom, centreY + radius);
                int left = Math.Max(limits.Left, centreX - radius);
                int right = Math.Min(limits.Right, centreX + radius);
                int x;
                for (x = left; x <= right; x++)
                {
                    if (TryUseSeedCell(x, top, prototype, out seedIndex)) return true;
                    if (bottom != top && TryUseSeedCell(x, bottom, prototype, out seedIndex)) return true;
                }

                int y;
                for (y = top + 1; y < bottom; y++)
                {
                    if (TryUseSeedCell(left, y, prototype, out seedIndex)) return true;
                    if (right != left && TryUseSeedCell(right, y, prototype, out seedIndex)) return true;
                }
            }

            seedIndex = -1;
            return false;
        }

        private bool TryUseSeedCell(int x, int y, Prototype prototype, out int seedIndex)
        {
            int index = y * gridWidth + x;
            if (ColoursSimilar(gridColours[index], prototype.Colour, prototype.Tolerance))
            {
                seedIndex = index;
                return true;
            }

            seedIndex = -1;
            return false;
        }

        private bool TryCreateVisualBounds(
            Point screenPoint,
            Rectangle parent,
            Prototype prototype,
            Region region,
            out Rectangle visualBounds)
        {
            visualBounds = Rectangle.Empty;
            if (region.IsEmpty || region.Aborted)
            {
                return false;
            }

            int boxCellWidth = region.MaximumX - region.MinimumX + 1;
            int boxCellHeight = region.MaximumY - region.MinimumY + 1;
            long boxCellArea = (long)boxCellWidth * boxCellHeight;
            long matchedPixelArea = (long)region.CellCount * sampleStep * sampleStep;
            if (matchedPixelArea < MinimumMatchedPixelArea ||
                region.CellCount * 100L < boxCellArea * MinimumComponentFillPercent)
            {
                return false;
            }

            Rectangle coarse = Rectangle.FromLTRB(
                desktopBounds.Left + region.MinimumX * sampleStep,
                desktopBounds.Top + region.MinimumY * sampleStep,
                desktopBounds.Left + Math.Min(sourceWidth, (region.MaximumX + 1) * sampleStep),
                desktopBounds.Top + Math.Min(sourceHeight, (region.MaximumY + 1) * sampleStep));
            coarse = Rectangle.Intersect(parent, coarse);
            if (!IsMeaningful(coarse) || !coarse.Contains(screenPoint))
            {
                return false;
            }

            Rectangle refined = RefineBounds(coarse, parent, prototype);
            if (!IsMeaningful(refined) || !refined.Contains(screenPoint))
            {
                return false;
            }

            long parentArea = (long)parent.Width * parent.Height;
            long refinedArea = (long)refined.Width * refined.Height;
            bool effectivelyParent =
                refined.Left <= parent.Left + sampleStep &&
                refined.Top <= parent.Top + sampleStep &&
                refined.Right >= parent.Right - sampleStep &&
                refined.Bottom >= parent.Bottom - sampleStep;
            if (refined == parent ||
                (effectivelyParent && refinedArea * 100L >= parentArea * 92L))
            {
                return false;
            }

            visualBounds = refined;
            return true;
        }

        private bool TryFindAttachedRoundedBounds(
            Point screenPoint,
            Rectangle parent,
            Prototype prototype,
            out Rectangle visualBounds)
        {
            visualBounds = Rectangle.Empty;
            GridBounds limits = GetGridBounds(parent);
            int seedIndex;
            if (!TryFindSeed(screenPoint, limits, prototype, out seedIndex))
            {
                return false;
            }

            int seedY = seedIndex / gridWidth;
            int seedX = seedIndex - seedY * gridWidth;
            // Text, icons, and antialiasing can interrupt the local fill for a
            // few pixels even though the surrounding rounded card is continuous.
            int allowedGap = Math.Max(2, DivideRoundingUp(10, sampleStep));
            int localRadius = Math.Max(2, DivideRoundingUp(10, sampleStep));
            int minimumY = FindLocalPrototypeExtent(
                seedX,
                seedY,
                limits.Top,
                -1,
                localRadius,
                allowedGap,
                prototype);
            int maximumY = FindLocalPrototypeExtent(
                seedX,
                seedY,
                limits.Bottom,
                1,
                localRadius,
                allowedGap,
                prototype);
            int minimumHeightCells = Math.Max(
                2,
                DivideRoundingUp(MinimumZoneDimension, sampleStep));
            if (maximumY - minimumY + 1 < minimumHeightCells)
            {
                return false;
            }

            int maximumAttachmentOverlap = Math.Max(
                3,
                DivideRoundingUp(20, sampleStep));
            int minimumX = FindHorizontalPrototypeExtent(
                seedX,
                limits.Left,
                -1,
                minimumY,
                maximumY,
                maximumAttachmentOverlap,
                prototype);
            int maximumX = FindHorizontalPrototypeExtent(
                seedX,
                limits.Right,
                1,
                minimumY,
                maximumY,
                maximumAttachmentOverlap,
                prototype);
            int minimumWidthCells = Math.Max(
                2,
                DivideRoundingUp(MinimumZoneDimension, sampleStep));
            if (maximumX - minimumX + 1 < minimumWidthCells)
            {
                return false;
            }

            Rectangle coarse = Rectangle.FromLTRB(
                desktopBounds.Left + minimumX * sampleStep,
                desktopBounds.Top + minimumY * sampleStep,
                desktopBounds.Left + Math.Min(
                    sourceWidth,
                    (maximumX + 1) * sampleStep),
                desktopBounds.Top + Math.Min(
                    sourceHeight,
                    (maximumY + 1) * sampleStep));
            coarse = Rectangle.Intersect(parent, coarse);
            if (!IsMeaningful(coarse) ||
                !coarse.Contains(screenPoint) ||
                IsEffectivelyParent(coarse, parent))
            {
                return false;
            }

            Rectangle edgeBounds;
            if (!TryRefineCandidateEdges(
                    coarse,
                    parent,
                    out edgeBounds) ||
                !IsMeaningful(edgeBounds) ||
                !edgeBounds.Contains(screenPoint) ||
                IsEffectivelyParent(edgeBounds, parent))
            {
                return false;
            }

            visualBounds = edgeBounds;
            return true;
        }

        private bool TryFindEnclosingOutlinedPanel(
            Point screenPoint,
            Rectangle parent,
            out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (parent.Width < MinimumZoneDimension * 2 ||
                parent.Height < MinimumZoneDimension * 2)
            {
                return false;
            }

            int localHalfHeight = Math.Max(28, Math.Min(120, parent.Height / 5));
            int localTop = Math.Max(parent.Top, screenPoint.Y - localHalfHeight);
            int localBottom = Math.Min(parent.Bottom, screenPoint.Y + localHalfHeight + 1);
            BoundaryEvidence left = FindNearestVerticalPanelBoundary(
                screenPoint.X - 4,
                parent.Left + 1,
                -1,
                localTop,
                localBottom,
                parent);
            BoundaryEvidence right = FindNearestVerticalPanelBoundary(
                screenPoint.X + 4,
                parent.Right - 1,
                1,
                localTop,
                localBottom,
                parent);
            if (!left.IsValid || !right.IsValid ||
                right.Position - left.Position < MinimumZoneDimension)
            {
                return false;
            }

            int localHalfWidth = Math.Max(
                28,
                Math.Min(160, (right.Position - left.Position) / 2));
            int horizontalLeft = Math.Max(
                left.Position,
                screenPoint.X - localHalfWidth);
            int horizontalRight = Math.Min(
                right.Position,
                screenPoint.X + localHalfWidth + 1);
            BoundaryEvidence top = FindNearestHorizontalPanelBoundary(
                parent.Top + 2,
                screenPoint.Y - 4,
                1,
                horizontalLeft,
                horizontalRight,
                parent);
            BoundaryEvidence bottom = FindNearestHorizontalPanelBoundary(
                parent.Bottom - 2,
                screenPoint.Y + 4,
                -1,
                horizontalLeft,
                horizontalRight,
                parent);
            if (!top.IsValid || !bottom.IsValid ||
                bottom.Position - top.Position < MinimumZoneDimension)
            {
                return false;
            }

            Rectangle candidate = Rectangle.FromLTRB(
                left.Position,
                top.Position,
                right.Position,
                bottom.Position);
            if (!IsMeaningful(candidate) ||
                candidate.Width < MinimumOutlinedPanelDimension ||
                candidate.Height < MinimumOutlinedPanelDimension ||
                !candidate.Contains(screenPoint) ||
                IsEffectivelyParent(candidate, parent))
            {
                return false;
            }

            BoundaryEvidence fullLeft = MeasureVerticalPanelBoundary(
                candidate.Left,
                candidate.Top,
                candidate.Bottom,
                parent);
            BoundaryEvidence fullRight = MeasureVerticalPanelBoundary(
                candidate.Right,
                candidate.Top,
                candidate.Bottom,
                parent);
            BoundaryEvidence fullTop = MeasureHorizontalPanelBoundary(
                candidate.Top,
                candidate.Left,
                candidate.Right,
                parent);
            BoundaryEvidence fullBottom = MeasureHorizontalPanelBoundary(
                candidate.Bottom,
                candidate.Left,
                candidate.Right,
                parent);
            if (!fullLeft.IsValid || !fullRight.IsValid ||
                !fullTop.IsValid || !fullBottom.IsValid)
            {
                return false;
            }

            long parentArea = (long)parent.Width * parent.Height;
            long candidateArea = (long)candidate.Width * candidate.Height;
            if (candidateArea * 100L >= parentArea * 90L)
            {
                return false;
            }

            bounds = candidate;
            return true;
        }

        private BoundaryEvidence FindNearestVerticalPanelBoundary(
            int start,
            int limit,
            int direction,
            int top,
            int bottom,
            Rectangle parent)
        {
            int x;
            for (x = start;
                 direction < 0 ? x >= limit : x <= limit;
                 x += direction)
            {
                BoundaryEvidence evidence = MeasureVerticalPanelBoundary(
                    x,
                    top,
                    bottom,
                    parent);
                if (evidence.IsValid)
                {
                    return evidence;
                }
            }
            return BoundaryEvidence.Invalid;
        }

        private BoundaryEvidence FindNearestHorizontalPanelBoundary(
            int start,
            int limit,
            int direction,
            int left,
            int right,
            Rectangle parent)
        {
            int y;
            for (y = start;
                 direction < 0 ? y >= limit : y <= limit;
                 y += direction)
            {
                BoundaryEvidence evidence = MeasureHorizontalPanelBoundary(
                    y,
                    left,
                    right,
                    parent);
                if (evidence.IsValid)
                {
                    return evidence;
                }
            }
            return BoundaryEvidence.Invalid;
        }

        private BoundaryEvidence MeasureVerticalPanelBoundary(
            int screenX,
            int top,
            int bottom,
            Rectangle enclosing)
        {
            const int depth = 2;
            if (screenX - depth < enclosing.Left ||
                screenX + depth > enclosing.Right)
            {
                return BoundaryEvidence.Invalid;
            }

            int inset = Math.Min(8, Math.Max(1, (bottom - top) / 10));
            int sampleTop = top + inset;
            int sampleBottom = bottom - inset;
            int length = sampleBottom - sampleTop;
            if (length <= 0)
            {
                return BoundaryEvidence.Invalid;
            }

            int samples = Math.Min(40, length);
            int strong = 0;
            int distanceSum = 0;
            int sample;
            for (sample = 0; sample < samples; sample++)
            {
                int y = sampleTop +
                    (int)(((long)sample * length + length / 2) / samples);
                int distance = Math.Max(
                    ColourDistance(
                        GetPackedPixel(screenX - depth, y),
                        GetPackedPixel(screenX + depth - 1, y)),
                    ColourDistance(
                        GetPackedPixel(screenX - 1, y),
                        GetPackedPixel(screenX, y)));
                distanceSum += distance;
                if (distance >= 3)
                {
                    strong++;
                }
            }
            return CreatePanelBoundaryEvidence(screenX, strong, distanceSum, samples);
        }

        private BoundaryEvidence MeasureHorizontalPanelBoundary(
            int screenY,
            int left,
            int right,
            Rectangle enclosing)
        {
            const int depth = 2;
            if (screenY - depth < enclosing.Top ||
                screenY + depth > enclosing.Bottom)
            {
                return BoundaryEvidence.Invalid;
            }

            int inset = Math.Min(8, Math.Max(1, (right - left) / 10));
            int sampleLeft = left + inset;
            int sampleRight = right - inset;
            int length = sampleRight - sampleLeft;
            if (length <= 0)
            {
                return BoundaryEvidence.Invalid;
            }

            int samples = Math.Min(40, length);
            int strong = 0;
            int distanceSum = 0;
            int sample;
            for (sample = 0; sample < samples; sample++)
            {
                int x = sampleLeft +
                    (int)(((long)sample * length + length / 2) / samples);
                int distance = Math.Max(
                    ColourDistance(
                        GetPackedPixel(x, screenY - depth),
                        GetPackedPixel(x, screenY + depth - 1)),
                    ColourDistance(
                        GetPackedPixel(x, screenY - 1),
                        GetPackedPixel(x, screenY)));
                distanceSum += distance;
                if (distance >= 3)
                {
                    strong++;
                }
            }
            return CreatePanelBoundaryEvidence(screenY, strong, distanceSum, samples);
        }

        private static BoundaryEvidence CreatePanelBoundaryEvidence(
            int position,
            int strongSamples,
            int distanceSum,
            int sampleCount)
        {
            if (sampleCount <= 0)
            {
                return BoundaryEvidence.Invalid;
            }
            int coverage = strongSamples * 100 / sampleCount;
            int meanDistance = distanceSum / sampleCount;
            bool valid = (coverage >= 58 && meanDistance >= 3) ||
                (coverage >= 55 && meanDistance >= 7);
            return valid
                ? new BoundaryEvidence(
                    position,
                    coverage * 4 + Math.Min(32, meanDistance) * 3,
                    true)
                : BoundaryEvidence.Invalid;
        }

        private int FindLocalPrototypeExtent(
            int centreX,
            int startY,
            int limitY,
            int direction,
            int radius,
            int allowedGap,
            Prototype prototype)
        {
            int lastMatch = startY;
            int misses = 0;
            int y;
            for (y = startY; ; y += direction)
            {
                if ((direction < 0 && y < limitY) ||
                    (direction > 0 && y > limitY))
                {
                    break;
                }

                if (LocalRowMatchesPrototype(centreX, y, radius, prototype))
                {
                    lastMatch = y;
                    misses = 0;
                }
                else
                {
                    misses++;
                    if (misses > allowedGap)
                    {
                        break;
                    }
                }
            }
            return lastMatch;
        }

        private bool LocalRowMatchesPrototype(
            int centreX,
            int y,
            int radius,
            Prototype prototype)
        {
            int left = Math.Max(0, centreX - radius);
            int right = Math.Min(gridWidth - 1, centreX + radius);
            int matches = 0;
            int total = right - left + 1;
            int x;
            for (x = left; x <= right; x++)
            {
                if (ColoursSimilar(
                    gridColours[y * gridWidth + x],
                    prototype.Colour,
                    prototype.Tolerance + AttachedGradientDriftAllowance))
                {
                    matches++;
                }
            }
            return total > 0 && matches * 100 >= total * 32;
        }

        private int FindHorizontalPrototypeExtent(
            int startX,
            int limitX,
            int direction,
            int minimumY,
            int maximumY,
            int attachmentOverlap,
            Prototype prototype)
        {
            int lastMatch = startX;
            int misses = 0;
            int attachedRun = 0;
            int attachedStart = startX;
            int x;
            for (x = startX; ; x += direction)
            {
                if ((direction < 0 && x < limitX) ||
                    (direction > 0 && x > limitX))
                {
                    break;
                }

                if (!ColumnSpanMatchesPrototype(
                    x,
                    minimumY,
                    maximumY,
                    prototype))
                {
                    misses++;
                    if (misses > 1)
                    {
                        break;
                    }
                    continue;
                }

                lastMatch = x;
                misses = 0;
                if (ColumnOutsideSpanMatchesPrototype(
                    x,
                    minimumY,
                    maximumY,
                    prototype))
                {
                    if (attachedRun == 0)
                    {
                        attachedStart = x;
                    }
                    attachedRun++;
                    if (attachedRun >= 3)
                    {
                        int inferred = attachedStart +
                            direction * attachmentOverlap;
                        int lower = Math.Min(startX, limitX);
                        int upper = Math.Max(startX, limitX);
                        return Math.Max(lower, Math.Min(upper, inferred));
                    }
                }
                else
                {
                    attachedRun = 0;
                }
            }
            return lastMatch;
        }

        private bool ColumnSpanMatchesPrototype(
            int x,
            int minimumY,
            int maximumY,
            Prototype prototype)
        {
            int length = maximumY - minimumY + 1;
            int samples = Math.Min(40, length);
            int matches = 0;
            int sample;
            for (sample = 0; sample < samples; sample++)
            {
                int y = minimumY + (int)(((long)sample * length +
                    length / 2) / samples);
                if (ColoursSimilar(
                    gridColours[y * gridWidth + x],
                    prototype.Colour,
                    prototype.Tolerance + AttachedGradientDriftAllowance))
                {
                    matches++;
                }
            }
            return samples > 0 && matches * 100 >= samples * 30;
        }

        private bool ColumnOutsideSpanMatchesPrototype(
            int x,
            int minimumY,
            int maximumY,
            Prototype prototype)
        {
            int distance = Math.Max(2, DivideRoundingUp(6, sampleStep));
            int topMatches = 0;
            int bottomMatches = 0;
            int samples = 0;
            int offset;
            for (offset = 1; offset <= distance; offset++)
            {
                int top = minimumY - offset;
                int bottom = maximumY + offset;
                if (top >= 0)
                {
                    samples++;
                    if (ColoursSimilar(
                        gridColours[top * gridWidth + x],
                        prototype.Colour,
                        prototype.Tolerance + AttachedGradientDriftAllowance))
                    {
                        topMatches++;
                    }
                }
                if (bottom < gridHeight)
                {
                    samples++;
                    if (ColoursSimilar(
                        gridColours[bottom * gridWidth + x],
                        prototype.Colour,
                        prototype.Tolerance + AttachedGradientDriftAllowance))
                    {
                        bottomMatches++;
                    }
                }
            }
            return samples > 0 &&
                (topMatches + bottomMatches) * 100 >= samples * 60;
        }

        private bool IsEffectivelyParent(Rectangle candidate, Rectangle parent)
        {
            long parentArea = (long)parent.Width * parent.Height;
            long candidateArea = (long)candidate.Width * candidate.Height;
            bool reachesEdges =
                candidate.Left <= parent.Left + sampleStep &&
                candidate.Top <= parent.Top + sampleStep &&
                candidate.Right >= parent.Right - sampleStep &&
                candidate.Bottom >= parent.Bottom - sampleStep;
            return candidate == parent ||
                (reachesEdges && candidateArea * 100L >= parentArea * 92L);
        }

        private static bool ShouldPreferRoundedOuterBounds(
            Rectangle inner,
            Rectangle outer)
        {
            long innerArea = (long)inner.Width * inner.Height;
            long outerArea = (long)outer.Width * outer.Height;
            if (innerArea <= 0 ||
                outerArea <= innerArea ||
                outerArea * 100L > innerArea * 175L)
            {
                return false;
            }

            Rectangle tolerantOuter = outer;
            tolerantOuter.Inflate(3, 3);
            bool oneAxisAlreadyComplete =
                outer.Width * 100L <= inner.Width * 115L ||
                outer.Height * 100L <= inner.Height * 115L;
            return oneAxisAlreadyComplete && tolerantOuter.Contains(inner);
        }

        private static bool ShouldPreferAttachedSubzone(
            Rectangle broad,
            Rectangle attached)
        {
            long broadArea = (long)broad.Width * broad.Height;
            long attachedArea = (long)attached.Width * attached.Height;
            if (attachedArea <= 0 || broadArea * 100L < attachedArea * 180L)
            {
                return false;
            }

            Rectangle tolerantBroad = broad;
            tolerantBroad.Inflate(3, 3);
            return tolerantBroad.Contains(attached);
        }

        private Rectangle RefineBounds(Rectangle coarse, Rectangle parent, Prototype prototype)
        {
            int search = Math.Max(2, sampleStep * 2);
            int left = RefineLeft(coarse, parent, prototype, search);
            int right = RefineRight(coarse, parent, prototype, search);
            Rectangle horizontal = Rectangle.FromLTRB(left, coarse.Top, right, coarse.Bottom);
            int top = RefineTop(horizontal, parent, prototype, search);
            int bottom = RefineBottom(horizontal, parent, prototype, search);

            // left/top are the first matching fill pixels and right/bottom are
            // already exclusive. Do not inflate unconditionally: doing so made a
            // sequence of adjacent taskbar buttons appear to overlap by two pixels.
            Rectangle result = Rectangle.FromLTRB(left, top, right, bottom);
            return Rectangle.Intersect(parent, result);
        }

        private int RefineLeft(
            Rectangle coarse,
            Rectangle parent,
            Prototype prototype,
            int search)
        {
            int minimum = Math.Max(parent.Left, coarse.Left - search);
            int maximum = Math.Min(coarse.Right - 1, coarse.Left + search);
            int inside = FindMatchingColumn(
                Math.Min(maximum, coarse.Left + sampleStep),
                minimum,
                maximum,
                coarse,
                prototype);
            if (inside < 0)
            {
                return coarse.Left;
            }

            int x = inside;
            while (x > minimum && ColumnMatches(x - 1, coarse, prototype))
            {
                x--;
            }
            return x;
        }

        private int RefineRight(
            Rectangle coarse,
            Rectangle parent,
            Prototype prototype,
            int search)
        {
            int minimum = Math.Max(coarse.Left, coarse.Right - 1 - search);
            int maximum = Math.Min(parent.Right - 1, coarse.Right - 1 + search);
            int inside = FindMatchingColumn(
                Math.Max(minimum, coarse.Right - 1 - sampleStep),
                minimum,
                maximum,
                coarse,
                prototype);
            if (inside < 0)
            {
                return coarse.Right;
            }

            int x = inside;
            while (x < maximum && ColumnMatches(x + 1, coarse, prototype))
            {
                x++;
            }
            return x + 1;
        }

        private int RefineTop(
            Rectangle coarse,
            Rectangle parent,
            Prototype prototype,
            int search)
        {
            int minimum = Math.Max(parent.Top, coarse.Top - search);
            int maximum = Math.Min(coarse.Bottom - 1, coarse.Top + search);
            int inside = FindMatchingRow(
                Math.Min(maximum, coarse.Top + sampleStep),
                minimum,
                maximum,
                coarse,
                prototype);
            if (inside < 0)
            {
                return coarse.Top;
            }

            int y = inside;
            while (y > minimum && RowMatches(y - 1, coarse, prototype))
            {
                y--;
            }
            return y;
        }

        private int RefineBottom(
            Rectangle coarse,
            Rectangle parent,
            Prototype prototype,
            int search)
        {
            int minimum = Math.Max(coarse.Top, coarse.Bottom - 1 - search);
            int maximum = Math.Min(parent.Bottom - 1, coarse.Bottom - 1 + search);
            int inside = FindMatchingRow(
                Math.Max(minimum, coarse.Bottom - 1 - sampleStep),
                minimum,
                maximum,
                coarse,
                prototype);
            if (inside < 0)
            {
                return coarse.Bottom;
            }

            int y = inside;
            while (y < maximum && RowMatches(y + 1, coarse, prototype))
            {
                y++;
            }
            return y + 1;
        }

        private int FindMatchingColumn(
            int preferred,
            int minimum,
            int maximum,
            Rectangle verticalRange,
            Prototype prototype)
        {
            int offset;
            int maximumOffset = Math.Max(preferred - minimum, maximum - preferred);
            for (offset = 0; offset <= maximumOffset; offset++)
            {
                int right = preferred + offset;
                if (right <= maximum && ColumnMatches(right, verticalRange, prototype))
                {
                    return right;
                }
                int left = preferred - offset;
                if (offset != 0 && left >= minimum && ColumnMatches(left, verticalRange, prototype))
                {
                    return left;
                }
            }
            return -1;
        }

        private int FindMatchingRow(
            int preferred,
            int minimum,
            int maximum,
            Rectangle horizontalRange,
            Prototype prototype)
        {
            int offset;
            int maximumOffset = Math.Max(preferred - minimum, maximum - preferred);
            for (offset = 0; offset <= maximumOffset; offset++)
            {
                int bottom = preferred + offset;
                if (bottom <= maximum && RowMatches(bottom, horizontalRange, prototype))
                {
                    return bottom;
                }
                int top = preferred - offset;
                if (offset != 0 && top >= minimum && RowMatches(top, horizontalRange, prototype))
                {
                    return top;
                }
            }
            return -1;
        }

        private bool ColumnMatches(int screenX, Rectangle verticalRange, Prototype prototype)
        {
            int top = verticalRange.Top + verticalRange.Height / 8;
            int bottom = verticalRange.Bottom - verticalRange.Height / 8;
            if (bottom <= top)
            {
                top = verticalRange.Top;
                bottom = verticalRange.Bottom;
            }

            int length = Math.Max(1, bottom - top);
            int samples = Math.Min(EdgeRefinementSamples, length);
            int matches = 0;
            int sample;
            for (sample = 0; sample < samples; sample++)
            {
                int y = top + (int)(((long)sample * length + length / 2) / samples);
                if (PixelMatches(screenX, y, prototype))
                {
                    matches++;
                }
            }

            return matches * 100 >= samples * RefinementMatchPercent;
        }

        private bool RowMatches(int screenY, Rectangle horizontalRange, Prototype prototype)
        {
            int left = horizontalRange.Left + horizontalRange.Width / 8;
            int right = horizontalRange.Right - horizontalRange.Width / 8;
            if (right <= left)
            {
                left = horizontalRange.Left;
                right = horizontalRange.Right;
            }

            int length = Math.Max(1, right - left);
            int samples = Math.Min(EdgeRefinementSamples, length);
            int matches = 0;
            int sample;
            for (sample = 0; sample < samples; sample++)
            {
                int x = left + (int)(((long)sample * length + length / 2) / samples);
                if (PixelMatches(x, screenY, prototype))
                {
                    matches++;
                }
            }

            return matches * 100 >= samples * RefinementMatchPercent;
        }

        private bool PixelMatches(int screenX, int screenY, Prototype prototype)
        {
            int localX = screenX - desktopBounds.Left;
            int localY = screenY - desktopBounds.Top;
            if (localX < 0 || localX >= sourceWidth ||
                localY < 0 || localY >= sourceHeight)
            {
                return false;
            }

            int colour = sourcePixels[localY * sourceWidth + localX];
            return ColoursSimilar(colour, prototype.Colour, prototype.Tolerance + 2);
        }

        private bool TryRefineCandidateEdges(
            Rectangle candidate,
            Rectangle enclosing,
            out Rectangle refined)
        {
            int search = Math.Max(6, sampleStep * 2);
            BoundaryEvidence left = FindBestVerticalCandidateBoundary(
                candidate.Left,
                candidate,
                enclosing,
                search,
                true);
            BoundaryEvidence right = FindBestVerticalCandidateBoundary(
                candidate.Right,
                candidate,
                enclosing,
                search,
                false);
            BoundaryEvidence top = FindBestHorizontalCandidateBoundary(
                candidate.Top,
                candidate,
                enclosing,
                search,
                true);
            BoundaryEvidence bottom = FindBestHorizontalCandidateBoundary(
                candidate.Bottom,
                candidate,
                enclosing,
                search,
                false);

            bool verticalPair = left.IsValid && right.IsValid;
            bool horizontalPair = top.IsValid && bottom.IsValid;
            if (!verticalPair && !horizontalPair)
            {
                refined = Rectangle.Empty;
                return false;
            }

            int refinedLeft = verticalPair ? left.Position : candidate.Left;
            int refinedRight = verticalPair ? right.Position : candidate.Right;
            int refinedTop = horizontalPair ? top.Position : candidate.Top;
            int refinedBottom = horizontalPair ? bottom.Position : candidate.Bottom;
            if (refinedRight - refinedLeft < MinimumZoneDimension ||
                refinedBottom - refinedTop < MinimumZoneDimension)
            {
                refined = Rectangle.Empty;
                return false;
            }

            refined = Rectangle.Intersect(
                enclosing,
                Rectangle.FromLTRB(
                    refinedLeft,
                    refinedTop,
                    refinedRight,
                    refinedBottom));
            return true;
        }

        private BoundaryEvidence FindBestVerticalCandidateBoundary(
            int expectedX,
            Rectangle candidate,
            Rectangle enclosing,
            int search,
            bool isLeft)
        {
            int depth = Math.Max(1, Math.Min(3, sampleStep));
            int minimum = Math.Max(enclosing.Left + depth, expectedX - search);
            int maximum = Math.Min(enclosing.Right - depth, expectedX + search);
            if (isLeft)
            {
                maximum = Math.Min(maximum, candidate.Right - MinimumZoneDimension);
            }
            else
            {
                minimum = Math.Max(minimum, candidate.Left + MinimumZoneDimension);
            }

            BoundaryEvidence best = BoundaryEvidence.Invalid;
            int bestAdjustedScore = int.MinValue;
            int x;
            for (x = minimum; x <= maximum; x++)
            {
                BoundaryEvidence evidence = MeasureVerticalCandidateBoundary(
                    x,
                    candidate.Top,
                    candidate.Bottom,
                    enclosing,
                    depth);
                if (!evidence.IsValid)
                {
                    continue;
                }

                int adjustedScore = evidence.Score - Math.Abs(x - expectedX) * 3;
                if (adjustedScore > bestAdjustedScore)
                {
                    best = evidence;
                    bestAdjustedScore = adjustedScore;
                }
            }

            return best;
        }

        private BoundaryEvidence FindBestHorizontalCandidateBoundary(
            int expectedY,
            Rectangle candidate,
            Rectangle enclosing,
            int search,
            bool isTop)
        {
            int depth = Math.Max(1, Math.Min(3, sampleStep));
            int minimum = Math.Max(enclosing.Top + depth, expectedY - search);
            int maximum = Math.Min(enclosing.Bottom - depth, expectedY + search);
            if (isTop)
            {
                maximum = Math.Min(maximum, candidate.Bottom - MinimumZoneDimension);
            }
            else
            {
                minimum = Math.Max(minimum, candidate.Top + MinimumZoneDimension);
            }

            BoundaryEvidence best = BoundaryEvidence.Invalid;
            int bestAdjustedScore = int.MinValue;
            int y;
            for (y = minimum; y <= maximum; y++)
            {
                BoundaryEvidence evidence = MeasureHorizontalCandidateBoundary(
                    y,
                    candidate.Left,
                    candidate.Right,
                    enclosing,
                    depth);
                if (!evidence.IsValid)
                {
                    continue;
                }

                int adjustedScore = evidence.Score - Math.Abs(y - expectedY) * 3;
                if (adjustedScore > bestAdjustedScore)
                {
                    best = evidence;
                    bestAdjustedScore = adjustedScore;
                }
            }

            return best;
        }

        private BoundaryEvidence MeasureVerticalCandidateBoundary(
            int screenX,
            int top,
            int bottom,
            Rectangle enclosing,
            int depth)
        {
            if (screenX - depth < enclosing.Left ||
                screenX + depth > enclosing.Right)
            {
                return BoundaryEvidence.Invalid;
            }

            int inset = Math.Min(Math.Max(2, sampleStep), Math.Max(0, (bottom - top) / 8));
            int sampleTop = top + inset;
            int sampleBottom = bottom - inset;
            int length = sampleBottom - sampleTop;
            if (length <= 0)
            {
                return BoundaryEvidence.Invalid;
            }

            int samples = Math.Min(CandidateBoundarySamples, length);
            int strong = 0;
            int distanceSum = 0;
            int sample;
            for (sample = 0; sample < samples; sample++)
            {
                int y = sampleTop +
                    (int)(((long)sample * length + length / 2) / samples);
                int farLeft = GetPackedPixel(screenX - depth, y);
                int nearLeft = GetPackedPixel(screenX - 1, y);
                int nearRight = GetPackedPixel(screenX, y);
                int farRight = GetPackedPixel(screenX + depth - 1, y);
                int distance = Math.Max(
                    ColourDistance(farLeft, farRight),
                    Math.Max(
                        ColourDistance(nearLeft, nearRight),
                        Math.Max(
                            ColourDistance(farLeft, nearLeft),
                            ColourDistance(nearRight, farRight))));
                distanceSum += distance;
                if (distance >= CandidateBoundaryDistance)
                {
                    strong++;
                }
            }

            return CreateBoundaryEvidence(screenX, strong, distanceSum, samples);
        }

        private BoundaryEvidence MeasureHorizontalCandidateBoundary(
            int screenY,
            int left,
            int right,
            Rectangle enclosing,
            int depth)
        {
            if (screenY - depth < enclosing.Top ||
                screenY + depth > enclosing.Bottom)
            {
                return BoundaryEvidence.Invalid;
            }

            int inset = Math.Min(Math.Max(2, sampleStep), Math.Max(0, (right - left) / 8));
            int sampleLeft = left + inset;
            int sampleRight = right - inset;
            int length = sampleRight - sampleLeft;
            if (length <= 0)
            {
                return BoundaryEvidence.Invalid;
            }

            int samples = Math.Min(CandidateBoundarySamples, length);
            int strong = 0;
            int distanceSum = 0;
            int sample;
            for (sample = 0; sample < samples; sample++)
            {
                int x = sampleLeft +
                    (int)(((long)sample * length + length / 2) / samples);
                int farTop = GetPackedPixel(x, screenY - depth);
                int nearTop = GetPackedPixel(x, screenY - 1);
                int nearBottom = GetPackedPixel(x, screenY);
                int farBottom = GetPackedPixel(x, screenY + depth - 1);
                int distance = Math.Max(
                    ColourDistance(farTop, farBottom),
                    Math.Max(
                        ColourDistance(nearTop, nearBottom),
                        Math.Max(
                            ColourDistance(farTop, nearTop),
                            ColourDistance(nearBottom, farBottom))));
                distanceSum += distance;
                if (distance >= CandidateBoundaryDistance)
                {
                    strong++;
                }
            }

            return CreateBoundaryEvidence(screenY, strong, distanceSum, samples);
        }

        private static BoundaryEvidence CreateBoundaryEvidence(
            int position,
            int strongSamples,
            int distanceSum,
            int sampleCount)
        {
            if (sampleCount <= 0)
            {
                return BoundaryEvidence.Invalid;
            }

            int coverage = strongSamples * 100 / sampleCount;
            int meanDistance = distanceSum / sampleCount;
            bool valid =
                (coverage >= CandidateBoundaryCoveragePercent && meanDistance >= 5) ||
                (coverage >= 45 && meanDistance >= 12);
            if (!valid)
            {
                return BoundaryEvidence.Invalid;
            }

            return new BoundaryEvidence(
                position,
                coverage * 4 + Math.Min(40, meanDistance) * 3,
                true);
        }

        private bool HasIsolatedCandidateContent(Rectangle candidate, Rectangle enclosing)
        {
            int surfaceColour;
            int outerForeground;
            int outerSamples;
            if (!TryFindCandidateSurfaceColour(
                candidate,
                enclosing,
                out surfaceColour,
                out outerForeground,
                out outerSamples))
            {
                return false;
            }

            int step = Math.Max(1, sampleStep);
            // Ignore the shared surface perimeter. Windows taskbar cells span the
            // full taskbar height, so the taskbar's parent-wide top separator and
            // bottom screen edge otherwise make every transparent app cell appear
            // to contain a full-width/full-height foreground cluster.
            int contentInset = Math.Max(2, step);
            int firstX = candidate.Left + contentInset + step / 2;
            int firstY = candidate.Top + contentInset + step / 2;
            int contentRight = candidate.Right - contentInset;
            int contentBottom = candidate.Bottom - contentInset;
            int foreground = 0;
            int total = 0;
            int minimumX = int.MaxValue;
            int minimumY = int.MaxValue;
            int maximumX = int.MinValue;
            int maximumY = int.MinValue;
            int y;
            for (y = firstY; y < contentBottom; y += step)
            {
                int x;
                for (x = firstX; x < contentRight; x += step)
                {
                    total++;
                    if (ColourDistance(GetPackedPixel(x, y), surfaceColour) <
                        CandidateContentDistance)
                    {
                        continue;
                    }

                    foreground++;
                    if (x < minimumX) minimumX = x;
                    if (x > maximumX) maximumX = x;
                    if (y < minimumY) minimumY = y;
                    if (y > maximumY) maximumY = y;
                }
            }

            if (total <= 0 || foreground <= 0 ||
                (long)foreground * step * step < CandidateMinimumContentPixelArea)
            {
                return false;
            }

            int insidePercent = foreground * 100 / total;
            int outsidePercent = outerSamples == 0
                ? 100
                : outerForeground * 100 / outerSamples;
            if (insidePercent < 2 ||
                insidePercent > 55 ||
                insidePercent < outsidePercent + 2)
            {
                return false;
            }

            Rectangle content = Rectangle.FromLTRB(
                Math.Max(candidate.Left, minimumX - step / 2),
                Math.Max(candidate.Top, minimumY - step / 2),
                Math.Min(candidate.Right, maximumX + step / 2 + 1),
                Math.Min(candidate.Bottom, maximumY + step / 2 + 1));
            if (content.Width < 6 || content.Height < 6 ||
                content.Width * 100L > candidate.Width * 78L ||
                content.Height * 100L > candidate.Height * 82L)
            {
                return false;
            }

            int minimumMargin = Math.Max(2, step);
            if (content.Left - candidate.Left < minimumMargin ||
                candidate.Right - content.Right < minimumMargin ||
                content.Top - candidate.Top < minimumMargin)
            {
                return false;
            }

            int candidateCentreX = candidate.Left + candidate.Width / 2;
            int candidateCentreY = candidate.Top + candidate.Height / 2;
            int contentCentreX = content.Left + content.Width / 2;
            int contentCentreY = content.Top + content.Height / 2;
            if (Math.Abs(contentCentreX - candidateCentreX) * 100L >
                    candidate.Width * 28L ||
                Math.Abs(contentCentreY - candidateCentreY) * 100L >
                    candidate.Height * 30L)
            {
                return false;
            }

            int contentCellWidth = Math.Max(1, DivideRoundingUp(content.Width, step));
            int contentCellHeight = Math.Max(1, DivideRoundingUp(content.Height, step));
            int contentFillPercent = foreground * 100 /
                (contentCellWidth * contentCellHeight);
            if (contentFillPercent < 5)
            {
                return false;
            }

            // A narrow clock/date label often occupies almost the full width of its
            // synthetic UIA strip. It is content, but it does not visually define a
            // button. App/tray icons retain generous horizontal cell padding.
            if (candidate.Width <= 48 &&
                content.Width * 100L > candidate.Width * 68L &&
                contentFillPercent < 32)
            {
                return false;
            }

            return true;
        }

        private bool TryFindCandidateSurfaceColour(
            Rectangle candidate,
            Rectangle enclosing,
            out int surfaceColour,
            out int outerForeground,
            out int outerSamples)
        {
            Array.Clear(clusterCounts, 0, clusterCounts.Length);
            Array.Clear(clusterRedSums, 0, clusterRedSums.Length);
            Array.Clear(clusterGreenSums, 0, clusterGreenSums.Length);
            Array.Clear(clusterBlueSums, 0, clusterBlueSums.Length);

            int ring = Math.Max(3, sampleStep * 2);
            int step = Math.Max(1, sampleStep);
            Rectangle left = Rectangle.Intersect(enclosing, Rectangle.FromLTRB(
                candidate.Left - ring,
                candidate.Top,
                candidate.Left,
                candidate.Bottom));
            Rectangle right = Rectangle.Intersect(enclosing, Rectangle.FromLTRB(
                candidate.Right,
                candidate.Top,
                candidate.Right + ring,
                candidate.Bottom));
            Rectangle top = Rectangle.Intersect(enclosing, Rectangle.FromLTRB(
                candidate.Left,
                candidate.Top - ring,
                candidate.Right,
                candidate.Top));
            Rectangle bottom = Rectangle.Intersect(enclosing, Rectangle.FromLTRB(
                candidate.Left,
                candidate.Bottom,
                candidate.Right,
                candidate.Bottom + ring));

            int total = 0;
            AccumulateCandidateSurfaceSamples(left, step, ref total);
            AccumulateCandidateSurfaceSamples(right, step, ref total);
            AccumulateCandidateSurfaceSamples(top, step, ref total);
            AccumulateCandidateSurfaceSamples(bottom, step, ref total);
            if (total < 8)
            {
                surfaceColour = 0;
                outerForeground = 0;
                outerSamples = total;
                return false;
            }

            int bestBin = -1;
            int bestCount = 0;
            int bin;
            for (bin = 0; bin < QuantizedColourCount; bin++)
            {
                if (clusterCounts[bin] > bestCount)
                {
                    bestBin = bin;
                    bestCount = clusterCounts[bin];
                }
            }
            if (bestBin < 0 || bestCount * 5 < total)
            {
                surfaceColour = 0;
                outerForeground = 0;
                outerSamples = total;
                return false;
            }

            surfaceColour = PackColour(
                clusterRedSums[bestBin] / bestCount,
                clusterGreenSums[bestBin] / bestCount,
                clusterBlueSums[bestBin] / bestCount);
            outerForeground = 0;
            outerSamples = 0;
            CountCandidateSurfaceDifferences(
                left,
                step,
                surfaceColour,
                ref outerForeground,
                ref outerSamples);
            CountCandidateSurfaceDifferences(
                right,
                step,
                surfaceColour,
                ref outerForeground,
                ref outerSamples);
            CountCandidateSurfaceDifferences(
                top,
                step,
                surfaceColour,
                ref outerForeground,
                ref outerSamples);
            CountCandidateSurfaceDifferences(
                bottom,
                step,
                surfaceColour,
                ref outerForeground,
                ref outerSamples);
            return true;
        }

        private void AccumulateCandidateSurfaceSamples(
            Rectangle bounds,
            int step,
            ref int total)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            int y;
            for (y = bounds.Top + step / 2; y < bounds.Bottom; y += step)
            {
                int x;
                for (x = bounds.Left + step / 2; x < bounds.Right; x += step)
                {
                    int colour = GetPackedPixel(x, y);
                    int bin = Quantize(colour);
                    clusterCounts[bin]++;
                    clusterRedSums[bin] += Red(colour);
                    clusterGreenSums[bin] += Green(colour);
                    clusterBlueSums[bin] += Blue(colour);
                    total++;
                }
            }
        }

        private void CountCandidateSurfaceDifferences(
            Rectangle bounds,
            int step,
            int surfaceColour,
            ref int foreground,
            ref int total)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            int y;
            for (y = bounds.Top + step / 2; y < bounds.Bottom; y += step)
            {
                int x;
                for (x = bounds.Left + step / 2; x < bounds.Right; x += step)
                {
                    total++;
                    if (ColourDistance(GetPackedPixel(x, y), surfaceColour) >=
                        CandidateContentDistance)
                    {
                        foreground++;
                    }
                }
            }
        }

        private int GetPackedPixel(int screenX, int screenY)
        {
            int localX = screenX - desktopBounds.Left;
            int localY = screenY - desktopBounds.Top;
            return sourcePixels[localY * sourceWidth + localX];
        }

        private bool TryFindCached(
            Point screenPoint,
            Rectangle parent,
            Prototype[] prototypes,
            out CacheEntry entry)
        {
            if (prototypes.Length == 0)
            {
                entry = default(CacheEntry);
                return false;
            }

            // Only the primary pointer-centred colour may validate a cache entry.
            // Matching any secondary mode allowed a broad taskbar/group background
            // to remain selected after the pointer moved onto a distinct app tile.
            Prototype primary = prototypes[0];
            int cacheIndex;
            for (cacheIndex = 0; cacheIndex < cache.Count; cacheIndex++)
            {
                CacheEntry candidate = cache[cacheIndex];
                if (candidate.Parent != parent || !candidate.Bounds.Contains(screenPoint))
                {
                    continue;
                }

                int tolerance = Math.Min(
                    candidate.Prototype.Tolerance,
                    primary.Tolerance);
                if (!ColoursSimilar(
                    candidate.Prototype.Colour,
                    primary.Colour,
                    tolerance))
                {
                    continue;
                }

                if (cacheIndex != 0)
                {
                    cache.RemoveAt(cacheIndex);
                    cache.Insert(0, candidate);
                }
                entry = candidate;
                return true;
            }

            entry = default(CacheEntry);
            return false;
        }

        private void CacheResult(Rectangle parent, Rectangle bounds, Prototype prototype)
        {
            int index;
            for (index = cache.Count - 1; index >= 0; index--)
            {
                CacheEntry existing = cache[index];
                if (existing.Parent == parent &&
                    existing.Bounds == bounds &&
                    ColoursSimilar(existing.Prototype.Colour, prototype.Colour, 6))
                {
                    cache.RemoveAt(index);
                }
            }

            cache.Insert(0, new CacheEntry(parent, bounds, prototype));
            if (cache.Count > MaximumCacheEntries)
            {
                cache.RemoveAt(cache.Count - 1);
            }
        }

        private void CacheLastQuery(
            Point screenPoint,
            Rectangle parent,
            Rectangle bounds,
            bool foundZone)
        {
            lastQueryPoint = screenPoint;
            lastQueryParent = parent;
            lastQueryBounds = bounds;
            lastQueryFoundZone = foundZone;
            hasLastQuery = true;
        }

        private GridBounds GetGridBounds(Rectangle screenBounds)
        {
            Rectangle clipped = Rectangle.Intersect(desktopBounds, screenBounds);
            int localLeft = clipped.Left - desktopBounds.Left;
            int localTop = clipped.Top - desktopBounds.Top;
            int localRight = clipped.Right - desktopBounds.Left;
            int localBottom = clipped.Bottom - desktopBounds.Top;
            return new GridBounds(
                Math.Max(0, localLeft / sampleStep),
                Math.Max(0, localTop / sampleStep),
                Math.Min(gridWidth - 1, (localRight - 1) / sampleStep),
                Math.Min(gridHeight - 1, (localBottom - 1) / sampleStep));
        }

        private Point ScreenToLocal(Point screenPoint)
        {
            return new Point(
                screenPoint.X - desktopBounds.Left,
                screenPoint.Y - desktopBounds.Top);
        }

        private static int CalculateSampleStep(int width, int height)
        {
            int step = 1;
            while ((long)DivideRoundingUp(width, step) *
                   DivideRoundingUp(height, step) > MaximumGridCells)
            {
                step++;
            }
            return step;
        }

        private static int DivideRoundingUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        private static bool IsMeaningful(Rectangle bounds)
        {
            return bounds.Width >= MinimumZoneDimension &&
                bounds.Height >= MinimumZoneDimension &&
                (long)bounds.Width * bounds.Height >= MinimumZoneArea;
        }

        private static int Quantize(int colour)
        {
            return ((Red(colour) >> 4) << 8) |
                ((Green(colour) >> 4) << 4) |
                (Blue(colour) >> 4);
        }

        private static int PackColour(int red, int green, int blue)
        {
            return (red << 16) | (green << 8) | blue;
        }

        private static int Red(int colour)
        {
            return (colour >> 16) & 0xff;
        }

        private static int Green(int colour)
        {
            return (colour >> 8) & 0xff;
        }

        private static int Blue(int colour)
        {
            return colour & 0xff;
        }

        private static bool ColoursSimilar(int first, int second, int tolerance)
        {
            int red = Math.Abs(Red(first) - Red(second));
            int green = Math.Abs(Green(first) - Green(second));
            int blue = Math.Abs(Blue(first) - Blue(second));
            int maximum = Math.Max(red, Math.Max(green, blue));
            if (maximum > tolerance + 5)
            {
                return false;
            }

            int weightedSquare = 2 * red * red + 4 * green * green + blue * blue;
            return weightedSquare <= 7 * tolerance * tolerance;
        }

        private static int ColourDistance(int first, int second)
        {
            int red = Math.Abs(Red(first) - Red(second));
            int green = Math.Abs(Green(first) - Green(second));
            int blue = Math.Abs(Blue(first) - Blue(second));
            int weightedSquare = 2 * red * red + 4 * green * green + blue * blue;
            return (int)Math.Sqrt(weightedSquare / 7.0);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(typeof(VisualZoneDetector).FullName);
            }
        }

        private struct Prototype
        {
            internal readonly int Colour;
            internal readonly int Tolerance;

            internal Prototype(int colour, int tolerance)
            {
                Colour = colour;
                Tolerance = tolerance;
            }
        }

        private struct BoundaryEvidence
        {
            internal static readonly BoundaryEvidence Invalid =
                new BoundaryEvidence(0, 0, false);

            internal readonly int Position;
            internal readonly int Score;
            internal readonly bool IsValid;

            internal BoundaryEvidence(int position, int score, bool isValid)
            {
                Position = position;
                Score = score;
                IsValid = isValid;
            }
        }

        private struct Region
        {
            internal static readonly Region Empty = new Region(0, 0, -1, -1, 0, false);
            internal static readonly Region AbortedRegion = new Region(0, 0, -1, -1, 0, true);

            internal readonly int MinimumX;
            internal readonly int MinimumY;
            internal readonly int MaximumX;
            internal readonly int MaximumY;
            internal readonly int CellCount;
            internal readonly bool Aborted;

            internal Region(
                int minimumX,
                int minimumY,
                int maximumX,
                int maximumY,
                int cellCount,
                bool aborted)
            {
                MinimumX = minimumX;
                MinimumY = minimumY;
                MaximumX = maximumX;
                MaximumY = maximumY;
                CellCount = cellCount;
                Aborted = aborted;
            }

            internal bool IsEmpty
            {
                get { return CellCount <= 0; }
            }
        }

        private struct GridBounds
        {
            internal readonly int Left;
            internal readonly int Top;
            internal readonly int Right;
            internal readonly int Bottom;

            internal GridBounds(int left, int top, int right, int bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }

            internal int Width
            {
                get { return Right - Left + 1; }
            }

            internal int Height
            {
                get { return Bottom - Top + 1; }
            }
        }

        private struct CacheEntry
        {
            internal readonly Rectangle Parent;
            internal readonly Rectangle Bounds;
            internal readonly Prototype Prototype;

            internal CacheEntry(Rectangle parent, Rectangle bounds, Prototype prototype)
            {
                Parent = parent;
                Bounds = bounds;
                Prototype = prototype;
            }
        }

        private struct VisualCandidate
        {
            internal readonly Rectangle Bounds;
            internal readonly Prototype Prototype;
            private readonly int prototypeIndex;
            private readonly bool broadBackground;
            private readonly bool compactGlyph;
            private readonly long area;

            internal VisualCandidate(
                Rectangle bounds,
                Prototype prototype,
                int prototypeIndex,
                Rectangle parent)
            {
                Bounds = bounds;
                Prototype = prototype;
                this.prototypeIndex = prototypeIndex;
                area = (long)bounds.Width * bounds.Height;

                long parentArea = (long)parent.Width * parent.Height;
                bool nearlyFullWidth = bounds.Width * 100L >= parent.Width * 90L;
                bool nearlyFullHeight = bounds.Height * 100L >= parent.Height * 90L;
                int touchedEdges = 0;
                if (bounds.Left <= parent.Left + 1) touchedEdges++;
                if (bounds.Top <= parent.Top + 1) touchedEdges++;
                if (bounds.Right >= parent.Right - 1) touchedEdges++;
                if (bounds.Bottom >= parent.Bottom - 1) touchedEdges++;
                broadBackground =
                    area * 100L >= parentArea * 70L ||
                    (nearlyFullWidth && bounds.Height * 100L >= parent.Height * 55L) ||
                    (nearlyFullHeight && bounds.Width * 100L >= parent.Width * 55L) ||
                    (touchedEdges >= 3 && area * 100L >= parentArea * 45L);

                compactGlyph =
                    bounds.Width <= CompactGlyphMaximumDimension &&
                    bounds.Height <= CompactGlyphMaximumDimension &&
                    (parent.Width > CompactGlyphMaximumDimension * 2 ||
                     parent.Height > CompactGlyphMaximumDimension * 2);
            }

            internal bool IsBetterThan(VisualCandidate other)
            {
                if (compactGlyph != other.compactGlyph)
                {
                    return !compactGlyph;
                }
                if (broadBackground != other.broadBackground)
                {
                    return !broadBackground;
                }
                if (area != other.area)
                {
                    return area < other.area;
                }
                return prototypeIndex < other.prototypeIndex;
            }
        }
    }
}
