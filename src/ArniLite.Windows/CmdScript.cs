using System.Globalization;
using System.Text;

namespace ArniLite.Windows
{
    public static class CmdScript
    {
        // cmd.exe reads a batch file in the OEM code page. A UTF-8 script is decoded as CP866
        // on a Russian system, so a path like "Работа" becomes "╨а╨░╨▒╨╛╤В╨░" and Windows
        // reports that the file cannot be found.
        public static Encoding Encoding =>
            Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

        public static string Text(string command)
        {
            return "@echo off\r\n" + (command ?? string.Empty) + "\r\n";
        }
    }
}
