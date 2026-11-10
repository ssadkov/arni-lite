using ArniLite.Api;
using Xunit;

namespace ArniLite.UnitTests
{
    public class CatalogModelTests
    {
        [Fact]
        public void FormatsFreeAndPaidPricesPerMillion()
        {
            Assert.Equal("бесплатно", CatalogModel.FormatPrice(true, "0", "0"));
            Assert.Equal("бесплатно", CatalogModel.FormatPrice(false, "0", "0"));
            Assert.Equal("$0.5 / $2 за 1M", CatalogModel.FormatPrice(false, "0.0000005", "0.000002"));
            Assert.Equal("Bedrock", CatalogModel.ProviderLabel("bedrock"));
            Assert.Equal("OpenRouter", CatalogModel.ProviderLabel("openrouter"));
        }
    }
}
