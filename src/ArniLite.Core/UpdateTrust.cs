using System.IO;

namespace ArniLite.Core
{
    public static class UpdateTrust
    {
        public static string PublicKeyXml
        {
            get
            {
                using var stream = typeof(UpdateTrust).Assembly.GetManifestResourceStream("ArniLite.Core.UpdatesPublicKey.xml");
                using var reader = new StreamReader(stream!);
                return reader.ReadToEnd();
            }
        }
    }
}
