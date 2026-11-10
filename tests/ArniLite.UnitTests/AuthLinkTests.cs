using ArniLite.Core;
using Xunit;

namespace ArniLite.UnitTests
{
    public class AuthLinkTests
    {
        [Fact]
        public void AllowsTheArniHostsAndLoopback()
        {
            Assert.True(AuthLink.TryValidate("https://chat.arnion.kz/?desktop_request=abcdefghijklmnopqrstuv", out _));
            Assert.True(AuthLink.TryValidate("https://api.arnion.ru/api/web/auth/yandex/start", out _));
            Assert.True(AuthLink.TryValidate("http://127.0.0.1:5173/?desktop_request=abcdefghijklmnopqrstuv", out _));
        }

        [Fact]
        public void RejectsSecretsAndOtherHosts()
        {
            Assert.False(AuthLink.TryValidate("https://chat.arnion.kz/?device_code=secret", out _));
            Assert.False(AuthLink.TryValidate("https://chat.arnion.kz/?access_token=secret", out _));
            Assert.False(AuthLink.TryValidate("https://evil.example/login", out _));
            Assert.False(AuthLink.TryValidate("http://chat.arnion.kz/", out _));
            Assert.False(AuthLink.TryValidate("javascript:alert(1)", out _));
        }
    }
}
