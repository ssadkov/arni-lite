using System;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArniLite.Core;

namespace ArniLite
{
    public sealed class ComposerAttachment
    {
        private readonly Action<ComposerAttachment> _remove;
        public ComposerAttachment(string name, string text, bool truncated, ChatImagePart image, Action<ComposerAttachment> remove)
        {
            Name = name ?? string.Empty;
            Text = text ?? string.Empty;
            Truncated = truncated;
            Image = image;
            _remove = remove;
            RemoveCommand = new RelayCommand(() => _remove(this));
        }

        public string Name { get; }
        public string Text { get; }
        public bool Truncated { get; }
        public ChatImagePart Image { get; }
        public ICommand RemoveCommand { get; }
    }

    public static class ComposerAttachments
    {
        private const int MaxImageFileBytes = 30 * 1024 * 1024;
        private const int MaxImageBytes = 2500000;

        public static ComposerAttachment ReadFile(string path, Action<ComposerAttachment> remove)
        {
            if (Directory.Exists(path))
            {
                throw new InvalidDataException("AttachmentFolder");
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new InvalidDataException("AttachmentMissing");
            }

            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("AttachmentTooLarge");
            }

            var name = info.Name;
            if (IsImageName(name))
            {
                if (info.Length > MaxImageFileBytes)
                {
                    throw new InvalidDataException("AttachmentTooLarge");
                }

                return ImageFile(path, name, info.Length, remove);
            }

            byte[] sample;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                sample = new byte[Math.Min(8000, stream.Length)];
                var read = 0;
                while (read < sample.Length)
                {
                    var n = stream.Read(sample, read, sample.Length - read);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }
            }

            if (AttachmentText.IsBinary(sample))
            {
                throw new InvalidDataException("AttachmentBinary");
            }

            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, false)))
            {
                var buffer = new char[AttachmentText.MaxChars + 1];
                var count = reader.Read(buffer, 0, buffer.Length);
                text = new string(buffer, 0, count);
            }

            var clipped = AttachmentText.Clip(text, out var truncated);
            return new ComposerAttachment(name, clipped, truncated, null, remove);
        }

        public static ComposerAttachment FromImage(BitmapSource source, string name, Action<ComposerAttachment> remove)
        {
            if (source == null)
            {
                throw new InvalidDataException("AttachmentImageFailed");
            }

            Encoded encoded;
            try
            {
                encoded = Encode(ScaleLikeArni(source, false), false);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new InvalidDataException("AttachmentImageFailed");
            }
            return new ComposerAttachment(name, string.Empty, false, new ChatImagePart
            {
                MediaType = encoded.MediaType,
                DataBase64 = Convert.ToBase64String(encoded.Bytes),
            }, remove);
        }

        private static ComposerAttachment ImageFile(string path, string name, long length, Action<ComposerAttachment> remove)
        {
            BitmapSource frame;
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0)
                    {
                        throw new InvalidDataException("AttachmentImageFailed");
                    }

                    frame = decoder.Frames[0];
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new InvalidDataException("AttachmentImageFailed");
            }

            var jpeg = name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
            var png = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var gif = name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
            if (!gif && (frame.PixelWidth <= 768 || frame.PixelHeight <= 768) && frame.PixelWidth <= 8000 && frame.PixelHeight <= 8000 && length <= MaxImageBytes && (jpeg || png))
            {
                return new ComposerAttachment(name, string.Empty, false, new ChatImagePart
                {
                    MediaType = jpeg ? "image/jpeg" : "image/png",
                    DataBase64 = Convert.ToBase64String(File.ReadAllBytes(path)),
                }, remove);
            }

            var encoded = Encode(ScaleLikeArni(frame, gif), jpeg);
            return new ComposerAttachment(name, string.Empty, false, new ChatImagePart
            {
                MediaType = encoded.MediaType,
                DataBase64 = Convert.ToBase64String(encoded.Bytes),
            }, remove);
        }

        private static Encoded Encode(BitmapSource source, bool jpeg)
        {
            source = Freeze(source);
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var bytes = jpeg ? AsJpeg(source) : AsPng(source);
                if (bytes.Length <= MaxImageBytes && source.PixelWidth <= 8000 && source.PixelHeight <= 8000)
                {
                    return new Encoded(jpeg ? "image/jpeg" : "image/png", bytes);
                }

                var edge = Math.Max(480, Math.Max(source.PixelWidth, source.PixelHeight) / 2);
                source = ScaleToEdge(source, edge);
                jpeg = true;
            }

            throw new InvalidDataException("AttachmentTooLarge");
        }

        private static BitmapSource ScaleLikeArni(BitmapSource source, bool gif)
        {
            source = Freeze(source);
            var width = source.PixelWidth;
            var height = source.PixelHeight;
            if (width == 0 || height == 0 || ((width <= 768 || height <= 768) && !gif))
            {
                return source;
            }

            if (width > 2048 || height > 2048)
            {
                var fit = 2048.0 / Math.Max(width, height);
                width = Math.Max(1, (int)Math.Round(width * fit));
                height = Math.Max(1, (int)Math.Round(height * fit));
            }

            var shortSide = Math.Min(width, height);
            var fitShort = 768.0 / shortSide;
            width = Math.Max(1, (int)Math.Round(width * fitShort));
            height = Math.Max(1, (int)Math.Round(height * fitShort));
            return Freeze(new TransformedBitmap(source, new ScaleTransform(width / (double)source.PixelWidth, height / (double)source.PixelHeight)));
        }

        private static BitmapSource ScaleToEdge(BitmapSource source, int edge)
        {
            source = Freeze(source);
            var longest = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longest <= edge || longest == 0)
            {
                return source;
            }

            var scale = edge / (double)longest;
            return Freeze(new TransformedBitmap(source, new ScaleTransform(scale, scale)));
        }

        private static BitmapSource Freeze(BitmapSource source)
        {
            if (source.IsFrozen)
            {
                return source;
            }

            var clone = source.Clone();
            clone.Freeze();
            return clone;
        }

        private static byte[] AsPng(BitmapSource source)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static byte[] AsJpeg(BitmapSource source)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static bool IsImageName(string name)
        {
            return name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class Encoded
        {
            public Encoded(string mediaType, byte[] bytes)
            {
                MediaType = mediaType;
                Bytes = bytes;
            }

            public string MediaType { get; }
            public byte[] Bytes { get; }
        }
    }
}
