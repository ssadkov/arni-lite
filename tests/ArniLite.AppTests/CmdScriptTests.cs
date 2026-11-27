using System.Text;
using ArniLite.Windows;
using Xunit;

namespace ArniLite.AppTests
{
    public class CmdScriptTests
    {
        [Fact]
        public void CyrillicPathSurvivesOemEncodingThatCmdUses()
        {
            const string path = @"c:\Assist\1. Работа\Урок12\Урок 12-registration.html";
            var text = CmdScript.Text("\"" + path + "\"");
            var oem = Encoding.GetEncoding(866);
            Assert.Equal(text, oem.GetString(oem.GetBytes(text)));

            var misread = oem.GetString(new UTF8Encoding(false).GetBytes(text));
            Assert.Contains("╨а╨░╨▒╨╛╤В╨░", misread);
            Assert.DoesNotContain("Работа", misread);
        }

        [Fact]
        public void ScriptEncodingIsTheSystemOemCodePageWithoutBom()
        {
            var encoding = CmdScript.Encoding;
            Assert.Equal(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage, encoding.CodePage);
            Assert.Empty(encoding.GetPreamble());
        }
    }
}
