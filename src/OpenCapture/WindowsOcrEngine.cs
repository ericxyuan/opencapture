using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace OpenCapture
{
    internal sealed class OcrTextRegion
    {
        internal OcrTextRegion(string text, RectangleF bounds)
            : this(text, bounds, 0)
        {
        }

        internal OcrTextRegion(string text, RectangleF bounds, int lineIndex)
        {
            Text = text ?? string.Empty;
            Bounds = bounds;
            LineIndex = lineIndex;
        }

        internal string Text { get; private set; }
        internal RectangleF Bounds { get; private set; }
        internal int LineIndex { get; private set; }
    }

    internal sealed class OcrRecognition
    {
        internal static readonly OcrRecognition Empty = new OcrRecognition(
            string.Empty,
            new List<OcrTextRegion>().AsReadOnly());

        internal OcrRecognition(string text, IList<OcrTextRegion> regions)
        {
            Text = text ?? string.Empty;
            Regions = regions ?? new List<OcrTextRegion>().AsReadOnly();
        }

        internal string Text { get; private set; }
        internal IList<OcrTextRegion> Regions { get; private set; }
    }

    internal static class WindowsOcrEngine
    {
        private const int MaximumScaleFactor = 3;
        private const double HistogramTailFraction = 0.01;
        private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int LCMapStringEx(
            string localeName,
            uint mapFlags,
            string source,
            int sourceLength,
            [Out] char[] destination,
            int destinationLength,
            IntPtr versionInformation,
            IntPtr reserved,
            IntPtr sortHandle);

        private sealed class OcrInputVariant
        {
            internal byte[] Bytes;
            internal int PreparedWidth;
            internal int PreparedHeight;
            internal int SourceWidth;
            internal int SourceHeight;
        }

        private sealed class OcrCandidate
        {
            internal OcrRecognition Recognition;
            internal int Score;
        }

        internal static string Recognize(Bitmap image)
        {
            if (image == null)
            {
                throw new ArgumentNullException("image");
            }

            return RecognizeDetailed(image).Text;
        }

        internal static OcrRecognition RecognizeDetailed(Bitmap image)
        {
            return RecognizeDetailed(image, null);
        }

        internal static OcrRecognition RecognizeDetailed(Bitmap image, Func<bool> isCurrent)
        {
            if (image == null)
            {
                throw new ArgumentNullException("image");
            }

            if (isCurrent != null && !isCurrent()) return OcrRecognition.Empty;
            IList<OcrInputVariant> variants = EncodeOcrVariants(image);
            return Task.Run(delegate { return RecognizeAsync(variants, isCurrent); })
                .GetAwaiter()
                .GetResult();
        }

        internal static bool IsAvailable()
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        internal static IList<string> GetAvailableLanguageTags()
        {
            List<string> tags = new List<string>();
            try
            {
                foreach (Language language in OcrEngine.AvailableRecognizerLanguages)
                {
                    if (language != null && !string.IsNullOrEmpty(language.LanguageTag))
                    {
                        tags.Add(language.LanguageTag);
                    }
                }
            }
            catch
            {
            }
            return tags.AsReadOnly();
        }

        private static IList<OcrInputVariant> EncodeOcrVariants(Bitmap source)
        {
            List<OcrInputVariant> variants = new List<OcrInputVariant>();
            using (Bitmap prepared = CreatePreparedBitmap(source))
            {
                variants.Add(CreateVariant(prepared, source.Size));

                int[] histogram;
                using (Bitmap greyscale = CreateContrastGreyscale(prepared, out histogram))
                {
                    variants.Add(CreateVariant(greyscale, source.Size));
                    int threshold = CalculateOtsuThreshold(histogram);
                    using (Bitmap blackOnWhite = CreateThresholdBitmap(
                        greyscale,
                        threshold,
                        false))
                    using (Bitmap whiteOnBlack = CreateThresholdBitmap(
                        greyscale,
                        threshold,
                        true))
                    {
                        variants.Add(CreateVariant(blackOnWhite, source.Size));
                        variants.Add(CreateVariant(whiteOnBlack, source.Size));
                    }
                }
            }
            return variants.AsReadOnly();
        }

        private static Bitmap CreatePreparedBitmap(Bitmap source)
        {
            int maximum = (int)OcrEngine.MaxImageDimension;
            int longestSide = Math.Max(source.Width, source.Height);
            double scale = Math.Min(
                MaximumScaleFactor,
                maximum / (double)Math.Max(1, longestSide));
            int width = Math.Max(1, (int)Math.Floor(source.Width * scale));
            int height = Math.Max(1, (int)Math.Floor(source.Height * scale));
            Bitmap prepared = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(prepared))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = scale > 1.0
                    ? InterpolationMode.HighQualityBicubic
                    : InterpolationMode.HighQualityBilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            }
            return prepared;
        }

        private static unsafe Bitmap CreateContrastGreyscale(
            Bitmap source,
            out int[] mappedHistogram)
        {
            Rectangle bounds = new Rectangle(0, 0, source.Width, source.Height);
            Bitmap greyscale = new Bitmap(
                source.Width,
                source.Height,
                PixelFormat.Format32bppArgb);
            BitmapData sourceData = null;
            BitmapData greyData = null;
            int[] originalHistogram = new int[256];
            try
            {
                sourceData = source.LockBits(
                    bounds,
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                greyData = greyscale.LockBits(
                    bounds,
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                int y;
                for (y = 0; y < source.Height; y++)
                {
                    byte* input = (byte*)sourceData.Scan0 + y * sourceData.Stride;
                    byte* output = (byte*)greyData.Scan0 + y * greyData.Stride;
                    int x;
                    for (x = 0; x < source.Width; x++)
                    {
                        int value = (19 * input[0] + 183 * input[1] +
                            54 * input[2] + 128) >> 8;
                        originalHistogram[value]++;
                        output[0] = (byte)value;
                        output[1] = (byte)value;
                        output[2] = (byte)value;
                        output[3] = 255;
                        input += 4;
                        output += 4;
                    }
                }
            }
            finally
            {
                if (greyData != null) greyscale.UnlockBits(greyData);
                if (sourceData != null) source.UnlockBits(sourceData);
            }

            int pixelCount = source.Width * source.Height;
            int tailCount = Math.Max(1, (int)(pixelCount * HistogramTailFraction));
            int low = FindLowerPercentile(originalHistogram, tailCount);
            int high = FindUpperPercentile(originalHistogram, tailCount);
            mappedHistogram = new int[256];
            if (high - low < 24)
            {
                Array.Copy(originalHistogram, mappedHistogram, originalHistogram.Length);
                return greyscale;
            }

            BitmapData contrastData = null;
            try
            {
                contrastData = greyscale.LockBits(
                    bounds,
                    ImageLockMode.ReadWrite,
                    PixelFormat.Format32bppArgb);
                int y;
                for (y = 0; y < greyscale.Height; y++)
                {
                    byte* pixel = (byte*)contrastData.Scan0 + y * contrastData.Stride;
                    int x;
                    for (x = 0; x < greyscale.Width; x++)
                    {
                        int mapped = (pixel[0] - low) * 255 / (high - low);
                        mapped = Math.Max(0, Math.Min(255, mapped));
                        pixel[0] = (byte)mapped;
                        pixel[1] = (byte)mapped;
                        pixel[2] = (byte)mapped;
                        mappedHistogram[mapped]++;
                        pixel += 4;
                    }
                }
            }
            finally
            {
                if (contrastData != null) greyscale.UnlockBits(contrastData);
            }
            return greyscale;
        }

        private static unsafe Bitmap CreateThresholdBitmap(
            Bitmap greyscale,
            int threshold,
            bool inverted)
        {
            Rectangle bounds = new Rectangle(0, 0, greyscale.Width, greyscale.Height);
            Bitmap result = new Bitmap(
                greyscale.Width,
                greyscale.Height,
                PixelFormat.Format32bppArgb);
            BitmapData sourceData = null;
            BitmapData resultData = null;
            try
            {
                sourceData = greyscale.LockBits(
                    bounds,
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                resultData = result.LockBits(
                    bounds,
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);
                int y;
                for (y = 0; y < greyscale.Height; y++)
                {
                    byte* input = (byte*)sourceData.Scan0 + y * sourceData.Stride;
                    byte* output = (byte*)resultData.Scan0 + y * resultData.Stride;
                    int x;
                    for (x = 0; x < greyscale.Width; x++)
                    {
                        byte value = input[0] <= threshold ? (byte)0 : (byte)255;
                        if (inverted) value = (byte)(255 - value);
                        output[0] = value;
                        output[1] = value;
                        output[2] = value;
                        output[3] = 255;
                        input += 4;
                        output += 4;
                    }
                }
            }
            finally
            {
                if (resultData != null) result.UnlockBits(resultData);
                if (sourceData != null) greyscale.UnlockBits(sourceData);
            }
            return result;
        }

        private static int FindLowerPercentile(int[] histogram, int count)
        {
            int accumulated = 0;
            int value;
            for (value = 0; value < histogram.Length; value++)
            {
                accumulated += histogram[value];
                if (accumulated >= count) return value;
            }
            return 0;
        }

        private static int FindUpperPercentile(int[] histogram, int count)
        {
            int accumulated = 0;
            int value;
            for (value = histogram.Length - 1; value >= 0; value--)
            {
                accumulated += histogram[value];
                if (accumulated >= count) return value;
            }
            return 255;
        }

        private static int CalculateOtsuThreshold(int[] histogram)
        {
            long total = 0;
            long weightedTotal = 0;
            int value;
            for (value = 0; value < histogram.Length; value++)
            {
                total += histogram[value];
                weightedTotal += (long)value * histogram[value];
            }

            long backgroundWeight = 0;
            long backgroundWeighted = 0;
            double bestVariance = -1.0;
            int bestThreshold = 127;
            for (value = 0; value < histogram.Length; value++)
            {
                backgroundWeight += histogram[value];
                if (backgroundWeight == 0) continue;
                long foregroundWeight = total - backgroundWeight;
                if (foregroundWeight == 0) break;
                backgroundWeighted += (long)value * histogram[value];
                double backgroundMean = backgroundWeighted / (double)backgroundWeight;
                double foregroundMean =
                    (weightedTotal - backgroundWeighted) / (double)foregroundWeight;
                double difference = backgroundMean - foregroundMean;
                double variance = backgroundWeight * (double)foregroundWeight *
                    difference * difference;
                if (variance > bestVariance)
                {
                    bestVariance = variance;
                    bestThreshold = value;
                }
            }
            return bestThreshold;
        }

        private static byte[] EncodePng(Bitmap bitmap)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        private static OcrInputVariant CreateVariant(Bitmap bitmap, Size sourceSize)
        {
            return new OcrInputVariant
            {
                Bytes = EncodePng(bitmap),
                PreparedWidth = bitmap.Width,
                PreparedHeight = bitmap.Height,
                SourceWidth = sourceSize.Width,
                SourceHeight = sourceSize.Height
            };
        }

        private static async Task<OcrRecognition> RecognizeAsync(
            IList<OcrInputVariant> variants, Func<bool> isCurrent)
        {
            if (isCurrent != null && !isCurrent()) return OcrRecognition.Empty;
            IList<OcrEngine> engines = CreateAllLanguageEngines();
            if (engines.Count == 0)
            {
                throw new InvalidOperationException(
                    "Windows OCR is unavailable because no OCR language pack is installed.");
            }

            OcrCandidate best = null;
            Exception lastError = null;
            foreach (OcrInputVariant variant in variants)
            {
                if (isCurrent != null && !isCurrent()) return OcrRecognition.Empty;
                using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
                {
                    using (DataWriter writer = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(variant.Bytes);
                        await System.WindowsRuntimeSystemExtensions.AsTask(
                            writer.StoreAsync());
                        writer.DetachStream();
                    }

                    stream.Seek(0);
                    BitmapDecoder decoder = await System.WindowsRuntimeSystemExtensions.AsTask(
                        BitmapDecoder.CreateAsync(stream));
                    using (SoftwareBitmap softwareBitmap =
                        await System.WindowsRuntimeSystemExtensions.AsTask(
                            decoder.GetSoftwareBitmapAsync(
                                BitmapPixelFormat.Bgra8,
                                BitmapAlphaMode.Premultiplied)))
                    {
                        foreach (OcrEngine engine in engines)
                        {
                            // A closed overlay no longer needs the remaining
                            // contrast/language passes. Release their resources
                            // after the current native recognition finishes.
                            if (isCurrent != null && !isCurrent()) return OcrRecognition.Empty;
                            try
                            {
                                OcrResult result =
                                    await System.WindowsRuntimeSystemExtensions.AsTask(
                                        engine.RecognizeAsync(softwareBitmap));
                                OcrRecognition recognition = CreateRecognition(
                                    result,
                                    variant);
                                int score = ScoreCandidate(recognition.Text);
                                if (score > 0 && (best == null || score > best.Score))
                                {
                                    best = new OcrCandidate
                                    {
                                        Recognition = recognition,
                                        Score = score
                                    };
                                }
                            }
                            catch (Exception exception)
                            {
                                lastError = exception;
                            }
                        }
                    }
                }
            }

            if (best != null)
            {
                return best.Recognition;
            }
            if (lastError != null)
            {
                throw new InvalidOperationException(
                    "Windows could not recognise the selected text.",
                    lastError);
            }
            return OcrRecognition.Empty;
        }

        private static OcrRecognition CreateRecognition(
            OcrResult result,
            OcrInputVariant variant)
        {
            if (result == null)
            {
                return OcrRecognition.Empty;
            }

            string text = ConvertTraditionalChineseToSimplified(
                NormalizeRecognizedText(result.Text));
            List<OcrTextRegion> regions = new List<OcrTextRegion>();
            double scaleX = variant.SourceWidth /
                (double)Math.Max(1, variant.PreparedWidth);
            double scaleY = variant.SourceHeight /
                (double)Math.Max(1, variant.PreparedHeight);
            int lineIndex = 0;
            foreach (OcrLine line in result.Lines)
            {
                foreach (OcrWord word in line.Words)
                {
                    Windows.Foundation.Rect wordBounds = word.BoundingRect;
                    string wordText = ConvertTraditionalChineseToSimplified(
                        NormalizeRecognizedText(word.Text));
                    RectangleF mapped = new RectangleF(
                        (float)(wordBounds.X * scaleX),
                        (float)(wordBounds.Y * scaleY),
                        (float)(wordBounds.Width * scaleX),
                        (float)(wordBounds.Height * scaleY));
                    regions.Add(new OcrTextRegion(
                        wordText,
                        mapped,
                        lineIndex));
                }
                lineIndex++;
            }
            return new OcrRecognition(text, regions.AsReadOnly());
        }

        private static IList<OcrEngine> CreateAllLanguageEngines()
        {
            List<OcrEngine> engines = new List<OcrEngine>();
            HashSet<string> addedTags = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                OcrEngine preferred = OcrEngine.TryCreateFromUserProfileLanguages();
                AddEngine(preferred, engines, addedTags);
            }
            catch
            {
            }

            foreach (Language language in OcrEngine.AvailableRecognizerLanguages)
            {
                try
                {
                    AddEngine(
                        OcrEngine.TryCreateFromLanguage(language),
                        engines,
                        addedTags);
                }
                catch
                {
                }
            }
            return engines.AsReadOnly();
        }

        private static void AddEngine(
            OcrEngine engine,
            IList<OcrEngine> engines,
            ISet<string> addedTags)
        {
            if (engine == null || engine.RecognizerLanguage == null)
            {
                return;
            }
            string tag = engine.RecognizerLanguage.LanguageTag;
            if (!string.IsNullOrEmpty(tag) && addedTags.Add(tag))
            {
                engines.Add(engine);
            }
        }

        private static int ScoreCandidate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            int score = 0;
            int wordCharacters = 0;
            bool insideWord = false;
            foreach (char character in text)
            {
                if (char.IsLetterOrDigit(character))
                {
                    score += 5;
                    wordCharacters++;
                    if (!insideWord)
                    {
                        score += 3;
                        insideWord = true;
                    }
                }
                else if (char.IsWhiteSpace(character))
                {
                    score += 1;
                    insideWord = false;
                }
                else if (char.IsPunctuation(character) || char.IsSymbol(character))
                {
                    score += 1;
                }
                else if (character == '\uFFFD' || char.IsControl(character))
                {
                    score -= 8;
                }
            }
            return wordCharacters == 0 ? 0 : Math.Max(0, score);
        }

        internal static string NormalizeRecognizedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string trimmed = text.Trim();
            StringBuilder normalized = new StringBuilder(trimmed.Length);
            int index = 0;
            while (index < trimmed.Length)
            {
                char character = trimmed[index];
                if (character != ' ' && character != '\t')
                {
                    normalized.Append(character);
                    index++;
                    continue;
                }

                int nextIndex = index;
                while (nextIndex < trimmed.Length &&
                    (trimmed[nextIndex] == ' ' || trimmed[nextIndex] == '\t'))
                {
                    nextIndex++;
                }
                char previous = normalized.Length == 0
                    ? '\0'
                    : normalized[normalized.Length - 1];
                char next = nextIndex < trimmed.Length
                    ? trimmed[nextIndex]
                    : '\0';
                if (!IsCompactScriptCharacter(previous) ||
                    !IsCompactScriptCharacter(next))
                {
                    normalized.Append(' ');
                }
                index = nextIndex;
            }
            return normalized.ToString();
        }

        internal static string ConvertTraditionalChineseToSimplified(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            try
            {
                int required = LCMapStringEx(
                    "zh-CN",
                    LCMAP_SIMPLIFIED_CHINESE,
                    text,
                    text.Length,
                    null,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
                if (required <= 0)
                {
                    return text;
                }

                char[] converted = new char[required];
                int written = LCMapStringEx(
                    "zh-CN",
                    LCMAP_SIMPLIFIED_CHINESE,
                    text,
                    text.Length,
                    converted,
                    converted.Length,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
                if (written <= 0)
                {
                    return text;
                }
                int length = Math.Min(written, converted.Length);
                while (length > 0 && converted[length - 1] == '\0')
                {
                    length--;
                }
                return new string(converted, 0, length);
            }
            catch
            {
                return text;
            }
        }

        private static bool IsCompactScriptCharacter(char character)
        {
            return
                (character >= '\u3400' && character <= '\u4DBF') ||
                (character >= '\u4E00' && character <= '\u9FFF') ||
                (character >= '\uF900' && character <= '\uFAFF') ||
                (character >= '\u3040' && character <= '\u30FF') ||
                (character >= '\u1100' && character <= '\u11FF') ||
                (character >= '\uAC00' && character <= '\uD7AF');
        }
    }
}
