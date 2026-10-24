using System;
using System.Drawing;

namespace OpenCapture
{
    internal sealed class SelectionEventArgs : EventArgs
    {
        internal SelectionEventArgs(Rectangle selection)
        {
            Selection = selection;
        }

        internal Rectangle Selection { get; private set; }
    }

    internal sealed class ColorCopyEventArgs : EventArgs
    {
        internal ColorCopyEventArgs(string hexCode)
        {
            if (string.IsNullOrEmpty(hexCode))
            {
                throw new ArgumentException("A HEX colour code is required.", "hexCode");
            }

            HexCode = hexCode;
        }

        internal string HexCode { get; private set; }
    }

    internal sealed class TextCopyRequestEventArgs : EventArgs
    {
        internal TextCopyRequestEventArgs(Rectangle selection)
            : this(selection, null)
        {
        }

        internal TextCopyRequestEventArgs(
            Rectangle selection,
            OcrRecognition recognition)
        {
            if (selection.Width <= 0 || selection.Height <= 0)
            {
                throw new ArgumentOutOfRangeException("selection");
            }

            Selection = selection;
            Recognition = recognition;
        }

        internal Rectangle Selection { get; private set; }
        internal OcrRecognition Recognition { get; private set; }
    }

    internal sealed class RecordingRequestEventArgs : EventArgs
    {
        internal RecordingRequestEventArgs(Rectangle selection)
        {
            if (selection.Width <= 0 || selection.Height <= 0)
            {
                throw new ArgumentOutOfRangeException("selection");
            }
            Selection = selection;
        }

        internal Rectangle Selection { get; private set; }
    }

    internal sealed class RecordingCompletedEventArgs : EventArgs
    {
        internal RecordingCompletedEventArgs(string path, int frameCount, Exception error)
        {
            Path = path;
            FrameCount = frameCount;
            Error = error;
        }

        internal string Path { get; private set; }
        internal int FrameCount { get; private set; }
        internal Exception Error { get; private set; }
    }
}
