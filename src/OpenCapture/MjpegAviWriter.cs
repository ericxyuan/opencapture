using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenCapture
{
    /// <summary>
    /// Minimal AVI 1.0 MJPEG writer. MJPEG keeps the executable dependency-free
    /// and produces files that Windows media players can open directly.
    /// </summary>
    internal sealed class MjpegAviWriter : IDisposable
    {
        private const long MaximumFileBytes = 1900L * 1024L * 1024L;

        private readonly FileStream stream;
        private readonly BinaryWriter writer;
        private readonly List<IndexEntry> index;
        private readonly int framesPerSecond;
        private readonly long riffSizePosition;
        private readonly long totalFramesPosition;
        private readonly long streamLengthPosition;
        private readonly long suggestedBufferPosition;
        private readonly long streamSuggestedBufferPosition;
        private readonly long moviSizePosition;
        private readonly long moviTypePosition;
        private int frameCount;
        private int largestFrame;
        private bool disposed;

        internal MjpegAviWriter(string path, int width, int height, int fps)
        {
            if (width <= 0 || height <= 0 || width > short.MaxValue || height > short.MaxValue)
            {
                throw new ArgumentOutOfRangeException("width", "AVI dimensions are invalid.");
            }
            if (fps < 1 || fps > 120)
            {
                throw new ArgumentOutOfRangeException("fps");
            }

            framesPerSecond = fps;
            index = new List<IndexEntry>();
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            writer = new BinaryWriter(stream, Encoding.ASCII);

            WriteFourCc("RIFF");
            riffSizePosition = stream.Position;
            writer.Write(0U);
            WriteFourCc("AVI ");

            WriteFourCc("LIST");
            long headerListSize = stream.Position;
            writer.Write(0U);
            WriteFourCc("hdrl");

            WriteFourCc("avih");
            writer.Write(56U);
            writer.Write((uint)Math.Round(1000000.0 / fps));
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(0x10U);
            totalFramesPosition = stream.Position;
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(1U);
            suggestedBufferPosition = stream.Position;
            writer.Write(0U);
            writer.Write((uint)width);
            writer.Write((uint)height);
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(0U);

            WriteFourCc("LIST");
            long streamListSize = stream.Position;
            writer.Write(0U);
            WriteFourCc("strl");

            WriteFourCc("strh");
            writer.Write(56U);
            WriteFourCc("vids");
            WriteFourCc("MJPG");
            writer.Write(0U);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0U);
            writer.Write(1U);
            writer.Write((uint)fps);
            writer.Write(0U);
            streamLengthPosition = stream.Position;
            writer.Write(0U);
            streamSuggestedBufferPosition = stream.Position;
            writer.Write(0U);
            writer.Write(uint.MaxValue);
            writer.Write(0U);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write((short)width);
            writer.Write((short)height);

            WriteFourCc("strf");
            writer.Write(40U);
            writer.Write(40U);
            writer.Write(width);
            writer.Write(height);
            writer.Write((ushort)1);
            writer.Write((ushort)24);
            WriteFourCc("MJPG");
            writer.Write((uint)(width * height * 3L));
            writer.Write(0);
            writer.Write(0);
            writer.Write(0U);
            writer.Write(0U);

            PatchListSize(streamListSize);
            PatchListSize(headerListSize);

            WriteFourCc("LIST");
            moviSizePosition = stream.Position;
            writer.Write(0U);
            moviTypePosition = stream.Position;
            WriteFourCc("movi");
        }

        internal int FrameCount
        {
            get { return frameCount; }
        }

        internal long CurrentFileSizeBytes
        {
            get
            {
                // Include the index that will be appended if recording stops now.
                return stream.Position + 8L + index.Count * 16L;
            }
        }

        internal bool CanWriteFrame(int byteCount)
        {
            return byteCount > 0 &&
                stream.Position + byteCount + 32L + (index.Count + 1L) * 16L < MaximumFileBytes;
        }

        internal void WriteFrame(byte[] buffer, int byteCount)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(typeof(MjpegAviWriter).FullName);
            }
            if (buffer == null || byteCount <= 0 || byteCount > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("byteCount");
            }
            if (!CanWriteFrame(byteCount))
            {
                throw new IOException("The recording reached the AVI size limit.");
            }

            long chunkPosition = stream.Position;
            WriteFourCc("00dc");
            writer.Write((uint)byteCount);
            writer.Write(buffer, 0, byteCount);
            if ((byteCount & 1) != 0)
            {
                writer.Write((byte)0);
            }

            index.Add(new IndexEntry(
                (uint)(chunkPosition - moviTypePosition),
                (uint)byteCount));
            frameCount++;
            largestFrame = Math.Max(largestFrame, byteCount);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;

            try
            {
                long moviEnd = stream.Position;
                PatchUInt32(moviSizePosition, (uint)(moviEnd - moviSizePosition - 4));

                WriteFourCc("idx1");
                writer.Write((uint)(index.Count * 16));
                foreach (IndexEntry entry in index)
                {
                    WriteFourCc("00dc");
                    writer.Write(0x10U);
                    writer.Write(entry.Offset);
                    writer.Write(entry.Size);
                }

                long fileEnd = stream.Position;
                PatchUInt32(totalFramesPosition, (uint)frameCount);
                PatchUInt32(streamLengthPosition, (uint)frameCount);
                PatchUInt32(suggestedBufferPosition, (uint)largestFrame);
                PatchUInt32(streamSuggestedBufferPosition, (uint)largestFrame);
                PatchUInt32(riffSizePosition, (uint)(fileEnd - 8));
                stream.Position = fileEnd;
                writer.Flush();
                stream.Flush(true);
            }
            finally
            {
                writer.Dispose();
                stream.Dispose();
            }
        }

        private void PatchListSize(long sizePosition)
        {
            long end = stream.Position;
            PatchUInt32(sizePosition, (uint)(end - sizePosition - 4));
            stream.Position = end;
        }

        private void PatchUInt32(long position, uint value)
        {
            long end = stream.Position;
            stream.Position = position;
            writer.Write(value);
            stream.Position = end;
        }

        private void WriteFourCc(string value)
        {
            if (value == null || value.Length != 4)
            {
                throw new ArgumentException("A FOURCC must contain four characters.", "value");
            }
            writer.Write(Encoding.ASCII.GetBytes(value));
        }

        private struct IndexEntry
        {
            internal IndexEntry(uint offset, uint size)
            {
                Offset = offset;
                Size = size;
            }

            internal uint Offset;
            internal uint Size;
        }
    }
}
