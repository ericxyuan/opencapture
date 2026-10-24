using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace OpenCapture
{
    // Reads the dependency-free MJPEG container produced by MjpegAviWriter.
    // Frames are decoded one at a time, so reviewing/cropping a long video does
    // not load the recording into memory or touch desktop/HDR capture again.
    internal sealed class MjpegAviReader : IDisposable
    {
        private readonly BinaryReader reader;
        private long framesStart;
        private long framesEnd;

        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal int FrameCount { get; private set; }
        internal int FramesPerSecond { get; private set; }

        internal MjpegAviReader(string path)
        {
            reader = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
            try
            {
                if (ReadFourCc() != "RIFF") throw new InvalidDataException("Invalid recording.");
                reader.ReadUInt32();
                if (ReadFourCc() != "AVI ") throw new InvalidDataException("Invalid recording.");
                ReadChunks(reader.BaseStream.Length);
                if (Width <= 0 || Height <= 0 || framesStart == 0)
                    throw new InvalidDataException("Recording has no image stream.");
                reader.BaseStream.Position = framesStart;
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        private string ReadFourCc() { return Encoding.ASCII.GetString(reader.ReadBytes(4)); }

        private void ReadChunks(long end)
        {
            while (reader.BaseStream.Position + 8 <= end)
            {
                string id = ReadFourCc();
                uint length = reader.ReadUInt32();
                long data = reader.BaseStream.Position;
                long next = data + length + (length & 1);
                if (next > end || next < data) throw new InvalidDataException("Incomplete recording.");
                if (id == "LIST" && length >= 4)
                {
                    string type = ReadFourCc();
                    if (type == "movi")
                    {
                        framesStart = reader.BaseStream.Position;
                        framesEnd = data + length;
                    }
                    else if (type == "hdrl" || type == "strl")
                    {
                        ReadChunks(data + length);
                    }
                }
                else if (id == "avih" && length >= 40)
                {
                    uint microseconds = reader.ReadUInt32();
                    FramesPerSecond = microseconds == 0 ? 60 : (int)Math.Round(1000000.0 / microseconds);
                    reader.BaseStream.Position = data + 16;
                    FrameCount = checked((int)reader.ReadUInt32());
                    reader.BaseStream.Position = data + 32;
                    Width = checked((int)reader.ReadUInt32());
                    Height = checked((int)reader.ReadUInt32());
                }
                else if (id == "strh" && length >= 28)
                {
                    string streamType = ReadFourCc();
                    if (streamType == "vids")
                    {
                        reader.BaseStream.Position = data + 20;
                        uint scale = reader.ReadUInt32();
                        uint rate = reader.ReadUInt32();
                        if (scale > 0) FramesPerSecond = (int)Math.Round(rate / (double)scale);
                    }
                }
                reader.BaseStream.Position = next;
            }
        }

        internal Bitmap ReadFrame()
        {
            while (reader.BaseStream.Position + 8 <= framesEnd)
            {
                string id = ReadFourCc();
                uint length = reader.ReadUInt32();
                long data = reader.BaseStream.Position;
                long next = data + length + (length & 1);
                if (next > framesEnd || length > 64 * 1024 * 1024)
                    throw new InvalidDataException("Invalid recording frame.");
                if (id == "00dc")
                {
                    byte[] bytes = reader.ReadBytes(checked((int)length));
                    reader.BaseStream.Position = next;
                    using (MemoryStream stream = new MemoryStream(bytes))
                    using (Image decoded = Image.FromStream(stream))
                    {
                        return new Bitmap(decoded);
                    }
                }
                reader.BaseStream.Position = next;
            }
            return null;
        }

        public void Dispose() { reader.Dispose(); }

        internal static void CropVideo(string source, string destination, Rectangle crop)
        {
            using (MjpegAviReader input = new MjpegAviReader(source))
            {
                crop = Rectangle.Intersect(new Rectangle(0, 0, input.Width, input.Height), crop);
                if (crop.IsEmpty) throw new ArgumentOutOfRangeException("crop");
                bool outputOwned = false;
                try
                {
                    using (MjpegAviWriter output = new MjpegAviWriter(
                        destination, crop.Width, crop.Height, input.FramesPerSecond))
                    using (MemoryStream jpeg = new MemoryStream())
                    using (EncoderParameters parameters = new EncoderParameters(1))
                    {
                        outputOwned = true;
                        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 86L);
                        ImageCodecInfo codec = null;
                        foreach (ImageCodecInfo item in ImageCodecInfo.GetImageEncoders())
                            if (item.FormatID == ImageFormat.Jpeg.Guid) codec = item;
                        if (codec == null) throw new InvalidOperationException("Windows JPEG encoding is unavailable.");
                        Bitmap frame;
                        while ((frame = input.ReadFrame()) != null)
                        {
                            using (frame)
                            using (Bitmap cropped = frame.Clone(crop, PixelFormat.Format32bppRgb))
                            {
                                jpeg.Position = 0;
                                jpeg.SetLength(0);
                                cropped.Save(jpeg, codec, parameters);
                                output.WriteFrame(jpeg.GetBuffer(), checked((int)jpeg.Length));
                            }
                        }
                    }
                }
                catch
                {
                    if (outputOwned && File.Exists(destination))
                    {
                        try { File.Delete(destination); }
                        catch (Exception exception) { ErrorLog.Write("Could not remove incomplete video crop", exception); }
                    }
                    throw;
                }
            }
        }
    }
}
