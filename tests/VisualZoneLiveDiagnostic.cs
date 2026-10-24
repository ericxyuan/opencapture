using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace OpenCapture
{
    internal static class VisualZoneLiveDiagnostic
    {
        [STAThread]
        private static int Main()
        {
            NativeMethods.TryEnablePerMonitorV2DpiAwareness();
            Rectangle candidate = new Rectangle(951, 1528, 66, 72);
            Rectangle parent = new Rectangle(951, 1528, 396, 72);
            Point centre = new Point(candidate.Left + candidate.Width / 2, candidate.Top + candidate.Height / 2);

            using (CapturedFrame frame = CaptureEngine.CaptureVirtualDesktop(false))
            {
                Console.WriteLine("desktop={0}", frame.DesktopBounds);
                Point cursor;
                if (NativeMethods.TryGetPhysicalCursorPosition(out cursor))
                {
                    Console.WriteLine("cursor={0} inCandidate={1}", cursor, candidate.Contains(cursor));
                }

                Rectangle cropBounds = Rectangle.Intersect(
                    frame.DesktopBounds,
                    Rectangle.FromLTRB(parent.Left - 24, parent.Top - 24, parent.Right + 24, parent.Bottom + 8));
                string cropPath = Path.Combine(Path.GetTempPath(), "OpenCapture-live-taskbar-candidate.png");
                using (Bitmap crop = frame.Crop(cropBounds))
                {
                    crop.Save(cropPath, ImageFormat.Png);
                }
                Console.WriteLine("crop={0} cropBounds={1}", cropPath, cropBounds);

                using (VisualZoneDetector detector = new VisualZoneDetector(frame))
                {
                    Rectangle refined;
                    bool valid = detector.TryValidateCandidate(centre, candidate, parent, out refined);
                    Console.WriteLine("validate-centre={0} refined={1}", valid, refined);
                    DumpPrivateSignals(detector, candidate, parent);
                }
            }
            return 0;
        }

        private static void DumpPrivateSignals(
            VisualZoneDetector detector,
            Rectangle candidate,
            Rectangle parent)
        {
            Type type = typeof(VisualZoneDetector);
            MethodInfo edgeMethod = type.GetMethod(
                "TryRefineCandidateEdges",
                BindingFlags.Instance | BindingFlags.NonPublic);
            object[] edgeArguments = new object[] { candidate, parent, Rectangle.Empty };
            bool edgeValid = (bool)edgeMethod.Invoke(detector, edgeArguments);
            Console.WriteLine("edgeValid={0} edgeBounds={1}", edgeValid, edgeArguments[2]);

            MethodInfo contentMethod = type.GetMethod(
                "HasIsolatedCandidateContent",
                BindingFlags.Instance | BindingFlags.NonPublic);
            bool contentValid = (bool)contentMethod.Invoke(
                detector,
                new object[] { candidate, parent });
            Console.WriteLine("contentValid={0}", contentValid);

            MethodInfo surfaceMethod = type.GetMethod(
                "TryFindCandidateSurfaceColour",
                BindingFlags.Instance | BindingFlags.NonPublic);
            object[] surfaceArguments = new object[] { candidate, parent, 0, 0, 0 };
            bool surfaceValid = (bool)surfaceMethod.Invoke(detector, surfaceArguments);
            int surface = (int)surfaceArguments[2];
            int outerForeground = (int)surfaceArguments[3];
            int outerSamples = (int)surfaceArguments[4];
            Console.WriteLine(
                "surfaceValid={0} surface=#{1:X6} outerForeground={2}/{3}",
                surfaceValid,
                surface & 0xffffff,
                outerForeground,
                outerSamples);

            int[] pixels = (int[])type.GetField(
                "sourcePixels",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(detector);
            Rectangle desktop = (Rectangle)type.GetField(
                "desktopBounds",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(detector);
            int width = (int)type.GetField(
                "sourceWidth",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(detector);
            int step = (int)type.GetField(
                "sampleStep",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(detector);

            Dictionary<int, int> distances = new Dictionary<int, int>();
            int foreground = 0;
            int total = 0;
            int minimumX = int.MaxValue;
            int minimumY = int.MaxValue;
            int maximumX = int.MinValue;
            int maximumY = int.MinValue;
            int firstX = candidate.Left + step / 2;
            int firstY = candidate.Top + step / 2;
            int y;
            for (y = firstY; y < candidate.Bottom; y += step)
            {
                int x;
                for (x = firstX; x < candidate.Right; x += step)
                {
                    int colour = pixels[(y - desktop.Top) * width + x - desktop.Left];
                    int distance = ColourDistance(colour, surface);
                    int bucket = distance / 4 * 4;
                    int count;
                    distances.TryGetValue(bucket, out count);
                    distances[bucket] = count + 1;
                    total++;
                    if (distance < 14)
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

            Rectangle content = foreground == 0
                ? Rectangle.Empty
                : Rectangle.FromLTRB(
                    Math.Max(candidate.Left, minimumX - step / 2),
                    Math.Max(candidate.Top, minimumY - step / 2),
                    Math.Min(candidate.Right, maximumX + step / 2 + 1),
                    Math.Min(candidate.Bottom, maximumY + step / 2 + 1));
            Console.WriteLine(
                "step={0} foreground={1}/{2} ({3}%) content={4}",
                step,
                foreground,
                total,
                total == 0 ? 0 : foreground * 100 / total,
                content);

            List<int> keys = new List<int>(distances.Keys);
            keys.Sort();
            foreach (int key in keys)
            {
                Console.WriteLine("distance {0}-{1}: {2}", key, key + 3, distances[key]);
            }
        }

        private static int ColourDistance(int first, int second)
        {
            int red = Math.Abs(((first >> 16) & 255) - ((second >> 16) & 255));
            int green = Math.Abs(((first >> 8) & 255) - ((second >> 8) & 255));
            int blue = Math.Abs((first & 255) - (second & 255));
            int weighted = 2 * red * red + 4 * green * green + blue * blue;
            return (int)Math.Sqrt(weighted / 7.0);
        }
    }
}
