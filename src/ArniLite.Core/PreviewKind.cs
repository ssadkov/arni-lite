using System;
using System.IO;

namespace ArniLite.Core
{
    public enum PreviewKind { Text, Audio, Video }

    public static class PreviewPolicy
    {
        public static PreviewKind Classify(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".mp3": case ".wav": case ".wma": case ".m4a": case ".aac": case ".ogg": case ".flac":
                    return PreviewKind.Audio;
                case ".mp4": case ".m4v": case ".wmv": case ".avi": case ".mov": case ".mpg": case ".mpeg": case ".mkv": case ".webm":
                    return PreviewKind.Video;
                default: return PreviewKind.Text;
            }
        }
    }
}
