using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The price-feed wire formats — previously private parse logic inside
/// PriceFeedManager's message handlers and poll bodies, now pure and
/// assertable.
/// </summary>
[TestClass]
public class PriceFeedMessagesTests
{
    [TestMethod]
    public void TryParseBinanceTicker_NestedDataPayload_ParsesCoinAndPrice()
    {
        const string json = """{"data":{"s":"BTCUSDT","c":"61234.50","P":"2.34"},"stream":"btcusdt@ticker"}""";

        Assert.IsTrue(PriceFeedMessages.TryParseBinanceTicker(json, out var coin, out var price, out var change));

        Assert.AreEqual("BTC", coin);
        Assert.AreEqual(61234.50m, price);
        Assert.AreEqual(2.34m, change);
    }

    [TestMethod]
    public void TryParseBinanceTicker_FlatPayload_Parses()
    {
        const string json = """{"e":"24hrTicker","s":"ETHUSDT","c":"3000.1","P":"-1.25"}""";

        Assert.IsTrue(PriceFeedMessages.TryParseBinanceTicker(json, out var coin, out var price, out var change));

        Assert.AreEqual("ETH", coin);
        Assert.AreEqual(3000.1m, price);
        Assert.AreEqual(-1.25m, change);
    }

    [TestMethod]
    public void TryParseBinanceTicker_NonUsdtOrMalformed_Fails()
    {
        Assert.IsFalse(PriceFeedMessages.TryParseBinanceTicker("""{"data":{"s":"BTCBUSD","c":"1","P":"1"}}""", out _, out _, out _),
            "non-USDT pairs are ignored");
        Assert.IsFalse(PriceFeedMessages.TryParseBinanceTicker("""{"foo":1}""", out _, out _, out _));
        Assert.IsFalse(PriceFeedMessages.TryParseBinanceTicker("not json", out _, out _, out _));
    }

    [TestMethod]
    public void TryParseFinnhubTrades_TradeMessage_ReturnsAllTrades()
    {
        const string json = """{"type":"trade","data":[{"s":"AAPL","p":192.5},{"s":"MSFT","p":415.2}]}""";

        Assert.IsTrue(PriceFeedMessages.TryParseFinnhubTrades(json, out var trades));

        Assert.AreEqual(2, trades.Count);
        Assert.AreEqual(("AAPL", 192.5m), (trades[0].Symbol, trades[0].Price));
        Assert.AreEqual(("MSFT", 415.2m), (trades[1].Symbol, trades[1].Price));
    }

    [TestMethod]
    public void TryParseFinnhubTrades_NonTradeMessage_EmptyNotError()
    {
        const string json = """{"type":"ping"}""";

        Assert.IsTrue(PriceFeedMessages.TryParseFinnhubTrades(json, out var trades));
        Assert.AreEqual(0, trades.Count);
    }

    [TestMethod]
    public void TryParseFinnhubTrades_Malformed_Fails()
    {
        Assert.IsFalse(PriceFeedMessages.TryParseFinnhubTrades("not json", out _));
    }

    [TestMethod]
    public void TryParseBinanceRestTicker_StringNumbers_Parses()
    {
        const string json = """{"lastPrice":"1.2345","priceChangePercent":"3.21"}""";

        Assert.IsTrue(PriceFeedMessages.TryParseBinanceRestTicker(json, out var price, out var change));
        Assert.AreEqual(1.2345m, price);
        Assert.AreEqual(3.21m, change);
    }

    [TestMethod]
    public void TryParseBinanceRestTicker_Malformed_Fails()
    {
        Assert.IsFalse(PriceFeedMessages.TryParseBinanceRestTicker("""{"lastPrice":"abc"}""", out _, out _));
        Assert.IsFalse(PriceFeedMessages.TryParseBinanceRestTicker("not json", out _, out _));
    }

    [TestMethod]
    public void TryParseFinnhubQuote_WithAndWithoutDayChange()
    {
        Assert.IsTrue(PriceFeedMessages.TryParseFinnhubQuote("""{"c":192.5,"dp":1.5}""", out var price, out var change));
        Assert.AreEqual(192.5m, price);
        Assert.AreEqual(1.5m, change);

        Assert.IsTrue(PriceFeedMessages.TryParseFinnhubQuote("""{"c":192.5,"dp":null}""", out price, out change));
        Assert.AreEqual(192.5m, price);
        Assert.IsNull(change, "a null dp leaves the change unknown rather than zero");
    }

    [TestMethod]
    public void TryParseCoinGeckoSimplePrice_WithAndWithoutChange()
    {
        const string json = """{"bitcoin":{"usd":61234.5,"usd_24h_change":2.34}}""";

        Assert.IsTrue(PriceFeedMessages.TryParseCoinGeckoSimplePrice(json, "bitcoin", out var price, out var change));
        Assert.AreEqual(61234.5m, price);
        Assert.AreEqual(2.34m, change);

        Assert.IsTrue(PriceFeedMessages.TryParseCoinGeckoSimplePrice("""{"bitcoin":{"usd":61234.5}}""", "bitcoin", out price, out change));
        Assert.IsNull(change);
    }

    [TestMethod]
    public void TryParseCoinGeckoSimplePrice_MissingCoin_Fails()
    {
        Assert.IsFalse(PriceFeedMessages.TryParseCoinGeckoSimplePrice("""{"bitcoin":{"usd":1}}""", "ethereum", out _, out _));
    }
}
