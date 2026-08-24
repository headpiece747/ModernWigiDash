namespace ModernWigiDash.Tests;

/// <summary>
/// The price-feed wire formats, both sides: the WebSocket subscribe frames
/// (request) and the payload parsers (response). Previously private logic
/// inside PriceFeedManager's message handlers, poll bodies, and subscribe
/// payloads, now pure and assertable.
/// </summary>
[TestClass]
public class PriceFeedMessagesTests
{
    [TestMethod]
    public void BinanceStreamKey_BaseCoin_LowerCasedUsdtTickerStream()
    {
        Assert.AreEqual("btcusdt@ticker", PriceFeedMessages.BinanceStreamKey("BTC"));
        Assert.AreEqual("ethusdt@ticker", PriceFeedMessages.BinanceStreamKey("ETH"));
    }

    [TestMethod]
    public void BuildBinanceSubscribe_MultipleCoins_OneFrameWithEveryStreamKeyInOrder()
    {
        const string expected = """{"method":"SUBSCRIBE","params":["btcusdt@ticker","ethusdt@ticker"],"id":1}""";

        Assert.AreEqual(expected, PriceFeedMessages.BuildBinanceSubscribe(["BTC", "ETH"]));
    }

    [TestMethod]
    public void BuildBinanceSubscribe_SingleCoin_SameFrameShapeAsTheBulkSend()
    {
        const string expected = """{"method":"SUBSCRIBE","params":["btcusdt@ticker"],"id":1}""";

        Assert.AreEqual(expected, PriceFeedMessages.BuildBinanceSubscribe(["BTC"]));
    }

    [TestMethod]
    public void BuildFinnhubSubscribe_Symbol_OneFramePerSymbol()
    {
        const string expected = """{"type":"subscribe","symbol":"AAPL"}""";

        Assert.AreEqual(expected, PriceFeedMessages.BuildFinnhubSubscribe("AAPL"));
    }

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

    [TestMethod]
    public void ParseCoinGeckoSimplePriceBatch_MultipleIds_ParsesAll()
    {
        var parsed = PriceFeedMessages.ParseCoinGeckoSimplePriceBatch(
            """{"bitcoin":{"usd":61234.5,"usd_24h_change":2.1},"ethereum":{"usd":3100.25}}""");

        Assert.AreEqual(2, parsed.Count);
        Assert.AreEqual(61234.5m, parsed["bitcoin"].Price);
        Assert.AreEqual(2.1m, parsed["bitcoin"].ChangePercent);
        Assert.AreEqual(3100.25m, parsed["ethereum"].Price);
        Assert.IsNull(parsed["ethereum"].ChangePercent);
    }

    [TestMethod]
    public void ParseCoinGeckoSimplePriceBatch_NullUsd_SkipsEntry()
    {
        var parsed = PriceFeedMessages.ParseCoinGeckoSimplePriceBatch("""{"bitcoin":{"usd":null}}""");

        Assert.AreEqual(0, parsed.Count);
    }

    [TestMethod]
    public void ParseCoinGeckoSimplePriceBatch_Malformed_ReturnsEmpty()
    {
        var parsed = PriceFeedMessages.ParseCoinGeckoSimplePriceBatch("not json");

        Assert.AreEqual(0, parsed.Count);
    }

    [TestMethod]
    public void TryParseFrankfurterSeries_LastEntryIsPrice_ChangeFromPreviousEntry()
    {
        const string json = """
        {
          "amount": 1.0,
          "base": "EUR",
          "start_date": "2026-07-30",
          "end_date": "2026-08-04",
          "rates": {
            "2026-07-30": { "USD": 1.1476 },
            "2026-07-31": { "USD": 1.1485 },
            "2026-08-03": { "USD": 1.1511 },
            "2026-08-04": { "USD": 1.1515 }
          }
        }
        """;
        Assert.IsTrue(PriceFeedMessages.TryParseFrankfurterSeries(json, "USD", out var price, out var change));
        Assert.AreEqual(1.1515m, price);
        Assert.AreEqual((1.1515m / 1.1511m - 1m) * 100m, change);
    }

    [TestMethod]
    public void TryParseFrankfurterSeries_HandlesMissingQuoteMalformedJsonAndSingleEntry()
    {
        const string json = """
        {
          "base": "EUR",
          "rates": {
            "2026-07-30": { "USD": 1.1476 }
          }
        }
        """;
        Assert.IsFalse(PriceFeedMessages.TryParseFrankfurterSeries(json, "GBP", out _, out _));
        Assert.IsFalse(PriceFeedMessages.TryParseFrankfurterSeries("not-json", "USD", out _, out _));

        Assert.IsTrue(PriceFeedMessages.TryParseFrankfurterSeries(json, "USD", out var price, out var change));
        Assert.AreEqual(1.1476m, price);
        Assert.AreEqual(0m, change);
    }

    [TestMethod]
    public void TryParseYahooChart_ValidChart_ParsesPriceAndChange()
    {
        const string json = """{"chart":{"result":[{"meta":{"regularMarketPrice":150.5,"chartPreviousClose":148.0}}]}}""";

        Assert.IsTrue(PriceFeedMessages.TryParseYahooChart(json, out var price, out var change));

        Assert.AreEqual(150.5m, price);
        Assert.AreEqual((150.5m / 148.0m - 1m) * 100m, change);
    }

    [TestMethod]
    public void TryParseYahooChart_MissingResultOrMalformed_Fails()
    {
        Assert.IsFalse(PriceFeedMessages.TryParseYahooChart("""{"chart":{"result":[]}}""", out _, out _),
            "an empty result array has no meta to read");
        Assert.IsFalse(PriceFeedMessages.TryParseYahooChart("not json", out _, out _));
    }
}
