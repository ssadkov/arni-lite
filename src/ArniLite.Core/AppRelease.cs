using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArniLite.Core
{
    public sealed class ReleaseVersion : IComparable<ReleaseVersion>
    {
        public Version Number { get; }
        public string Prerelease { get; }
        public string Text { get; }
        private ReleaseVersion(string text, Version number, string prerelease) { Text = text; Number = number; Prerelease = prerelease; }
        public static ReleaseVersion Parse(string text)
        {
            var match = Regex.Match(text ?? "", @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*))?(?:\+[0-9A-Za-z.-]+)?$");
            if (!match.Success) throw new FormatException("Invalid release version.");
            var number = new Version(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
            var pre = match.Groups[4].Value;
            if (pre.Split('.').Any(p => p.Length > 1 && p[0] == '0' && p.All(char.IsDigit))) throw new FormatException("Invalid prerelease identifier.");
            return new ReleaseVersion(number + (pre.Length == 0 ? "" : "-" + pre), number, pre);
        }
        public int CompareTo(ReleaseVersion other)
        {
            var numeric = Number.CompareTo(other.Number);
            if (numeric != 0) return numeric;
            if (Prerelease.Length == 0 || other.Prerelease.Length == 0) return Prerelease.Length == other.Prerelease.Length ? 0 : (Prerelease.Length == 0 ? 1 : -1);
            var left = Prerelease.Split('.'); var right = other.Prerelease.Split('.');
            for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
            {
                var ln = left[i].All(char.IsDigit); var rn = right[i].All(char.IsDigit);
                var c = ln && rn ? (left[i].Length == right[i].Length ? string.CompareOrdinal(left[i], right[i]) : left[i].Length.CompareTo(right[i].Length))
                    : ln != rn ? (ln ? -1 : 1) : string.CompareOrdinal(left[i], right[i]);
                if (c != 0) return c;
            }
            return left.Length.CompareTo(right.Length);
        }
    }

    public sealed class UpdateManifest
    {
        public int Schema { get; set; }
        public string Version { get; set; } = "";
        public string MinimumWindows { get; set; } = "";
        public List<UpdateInstaller> Installers { get; set; } = new List<UpdateInstaller>();
        public UpdateInstaller? Select(string currentVersion, string architecture, Version windows, bool includePreview)
        {
            if (Schema != 1 || Installers == null || Installers.Count > 8) throw new FormatException("Invalid update manifest.");
            var version = ReleaseVersion.Parse(Version);
            if (version.Text != Version || !System.Version.TryParse(MinimumWindows, out var minimum) || minimum.Build < 0) throw new FormatException("Invalid update compatibility.");
            if ((!includePreview && version.Prerelease.Length > 0) || version.CompareTo(ReleaseVersion.Parse(currentVersion)) <= 0 || windows < minimum) return null;
            var matches = Installers.Where(i => i.Architecture == architecture).ToArray();
            if (matches.Length == 0) return null;
            if (matches.Length != 1) throw new FormatException("Duplicate installer.");
            var installer = matches[0];
            if ((architecture != "x64" && architecture != "x86") || installer.FileName != "ArniLiteSetup-" + Version + "-" + architecture + ".exe"
                || installer.Bytes < 1 || installer.Bytes > 256 * 1024 * 1024 || !Regex.IsMatch(installer.Sha256 ?? "", "^[0-9a-fA-F]{64}$")) throw new FormatException("Invalid update installer.");
            return installer;
        }
    }

    public sealed class UpdateInstaller
    {
        public string Architecture { get; set; } = "";
        public string FileName { get; set; } = "";
        public long Bytes { get; set; }
        public string Sha256 { get; set; } = "";
    }

    public static class SignedUpdate
    {
        public static void Verify(byte[] manifest, byte[] signature, string publicKeyXml)
        {
            if (manifest.Length == 0 || manifest.Length > 64 * 1024 || signature.Length > 1024) throw new CryptographicException("Invalid signed manifest size.");
            var key = XElement.Parse(publicKeyXml);
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(key.Element("Modulus")!.Value), Exponent = Convert.FromBase64String(key.Element("Exponent")!.Value) });
            if (!rsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException("Invalid update signature.");
        }
    }
}
