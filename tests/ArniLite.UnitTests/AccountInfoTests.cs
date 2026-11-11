using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArniLite.Api;
using Xunit;

namespace ArniLite.UnitTests
{
    public class AccountInfoTests
    {
        [Theory]
        [InlineData("null")]
        [InlineData("false")]
        [InlineData("\"legacy\"")]
        public void FreeAccountWithoutBudgetStillShowsIdentityAndLimits(string budget)
        {
            var account = AccountInfo.Parse("{\"user\":{\"email\":\"user@example.com\",\"plan\":\"FREE\",\"budget\":" + budget
                + ",\"freeSteps\":{\"remaining\":102,\"limit\":150,\"resetsAt\":\"2026-11-12T00:00:00.000Z\"}}}");
            Assert.Equal("user@example.com", account.Email);
            Assert.Equal("Бесплатный аккаунт", account.AccountType);
            Assert.False(account.HasBudget);
            Assert.Contains("Бесплатные шаги: 102 из 150", account.LimitLines);
            Assert.Contains("Бесплатные шаги обновятся", account.ResetInfo);
        }

        [Fact]
        public void PaidAccountShowsBalanceAndBothRemainingWindows()
        {
            var account = AccountInfo.Parse("{\"user\":{\"email\":\"user@example.com\",\"plan\":\"PRO\",\"budget\":{\"enabled\":true,\"expired\":false,"
                + "\"balanceMicros\":1250000,\"validUntil\":\"2026-12-01T00:00:00Z\","
                + "\"fiveHour\":{\"remainingMicros\":500000,\"requestLimit\":10,\"requests\":3},"
                + "\"weekly\":{\"remainingMicros\":1000000,\"requestLimit\":50,\"requests\":49}},\"freeSteps\":null}}");
            Assert.Equal("Доступ по бюджету", account.AccountType);
            Assert.Equal(new[] { "Баланс: $1.25", "5 часов: осталось $0.50 · 7 из 10 запросов", "7 дней: осталось $1.00 · 1 из 50 запросов" }, account.LimitLines);
            Assert.Contains("01.12.2026", account.ResetInfo);
        }

        [Fact]
        public void UsesServerExchangeRateAndDoesNotInventMissingLimits()
        {
            var account = AccountInfo.Parse("{\"user\":{\"email\":\"user@example.com\",\"plan\":\"PRO\",\"budget\":{\"balanceMicros\":2000000},\"billingRates\":{\"RUB\":100}}}");
            Assert.Contains("200", account.LimitLines.Single());
            Assert.Contains("₽", account.LimitLines.Single());
            var missing = AccountInfo.Parse("{\"user\":{\"email\":\"user@example.com\",\"budget\":null,\"freeSteps\":null}}");
            Assert.Equal(new[] { "Данные о лимитах не предоставлены" }, missing.LimitLines);
            Assert.Throws<FormatException>(() => AccountInfo.Parse("{\"user\":null}"));
        }

        [Fact]
        public async Task CatalogExcludesRefusedFreeEndpointsButKeepsOtherModels()
        {
            const string body = "{\"data\":[{\"id\":\"thinkingmachines/inkling:free\"},{\"id\":\"thinkingmachines/inkling-2-small:free\"},{\"id\":\"thinkingmachines/inkling-2-small\"},{\"id\":\"another/model:free\"}]}";
            using var http = new HttpClient(new JsonHandler(body));
            var models = await new ArniApiClient(http).ListModelsAsync("fake-token", CancellationToken.None);
            Assert.Equal(new[] { "another/model:free", "thinkingmachines/inkling-2-small" }, models.Select(model => model.Id));
            Assert.Equal("Провайдер отклонил эту модель для Arni Lite. Выберите другую модель.",
                ApiErrorText.ForUser("thinkingmachines/inkling-2-small:free is only available on agentic harnesses."));
        }

        private sealed class JsonHandler : HttpMessageHandler
        {
            private readonly string _body;
            public JsonHandler(string body) { _body = body; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
        }
    }
}
