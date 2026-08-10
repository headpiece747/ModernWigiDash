using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The price-feed symbol surface (SymbolCatalog): symbol/FX validation and
/// normalization, alias resolution, the asset-kind mapping, and the single
/// crypto table's CoinGecko invariant — the pure surface extracted from the
/// feed-lifecycle tests so it is pinned in one place.
/// </summary>
[TestClass]
public class SymbolCatalogTests
{
    [TestMethod]
    public void TryParseFxPair_ValidPair_SplitsBaseAndQuote()
    {
        Assert.IsTrue(SymbolCatalog.TryParseFxPair("EUR/USD", out string baseCur, out string quoteCur));
        Assert.AreEqual("EUR", baseCur);
        Assert.AreEqual("USD", quoteCur);
    }

    [TestMethod]
    public void TryParseFxPair_NonPair_ReturnsFalse()
    {
        Assert.IsFalse(SymbolCatalog.TryParseFxPair("AAPL", out _, out _));
        Assert.IsFalse(SymbolCatalog.TryParseFxPair("EUR-USD", out _, out _));
        Assert.IsFalse(SymbolCatalog.TryParseFxPair("E/U", out _, out _));
        Assert.IsFalse(SymbolCatalog.TryParseFxPair("", out _, out _));
    }

    [TestMethod]
    public void NormalizeFxKey_TrimsAndUppercases_RemovesSlash()
    {
        Assert.AreEqual("EURUSD", SymbolCatalog.NormalizeFxKey(" eur/usd "));
        Assert.AreEqual("USDJPY", SymbolCatalog.NormalizeFxKey("usd/jpy"));
        Assert.AreEqual("GBPUSD", SymbolCatalog.NormalizeFxKey("gbpusd"));
    }

    [TestMethod]
    public void IsValidFxInput_PairAndBareKeyForms_AcceptAndNormalize()
    {
        Assert.IsTrue(SymbolCatalog.IsValidFxInput("EUR/USD", out string pairKey));
        Assert.AreEqual("EURUSD", pairKey);
        Assert.IsTrue(SymbolCatalog.IsValidFxInput("EURUSD", out string bareKey));
        Assert.AreEqual("EURUSD", bareKey);
    }

    [TestMethod]
    public void IsValidFxInput_InvalidShapes_Rejected()
    {
        Assert.IsFalse(SymbolCatalog.IsValidFxInput("EUR/U", out _));
        Assert.IsFalse(SymbolCatalog.IsValidFxInput("E/U", out _));
        Assert.IsFalse(SymbolCatalog.IsValidFxInput("123456", out _));
        Assert.IsFalse(SymbolCatalog.IsValidFxInput("", out _));
    }

    [TestMethod]
    public void IsValidSymbol_WellFormedSymbols_Accepted()
    {
        Assert.IsTrue(SymbolCatalog.IsValidSymbol("AAPL"));
        Assert.IsTrue(SymbolCatalog.IsValidSymbol("BTC"));
        Assert.IsTrue(SymbolCatalog.IsValidSymbol("BRK.B"));
        Assert.IsTrue(SymbolCatalog.IsValidSymbol("a1-z:2"));
    }

    [TestMethod]
    public void IsValidSymbol_HostileInput_Rejected()
    {
        Assert.IsFalse(SymbolCatalog.IsValidSymbol(""));
        Assert.IsFalse(SymbolCatalog.IsValidSymbol("AAPL&x=1"));
        Assert.IsFalse(SymbolCatalog.IsValidSymbol(new string('A', 33)));
        Assert.IsFalse(SymbolCatalog.IsValidSymbol("BTC ETH"));
        Assert.IsFalse(SymbolCatalog.IsValidSymbol("BTC$"));
    }

    [TestMethod]
    public void ToFeedKey_CryptoAlias_ResolvesToBaseCoin()
    {
        Assert.AreEqual("BTC", SymbolCatalog.ToFeedKey("bitcoin", AssetKind.Crypto));
        Assert.AreEqual("ARB", SymbolCatalog.ToFeedKey("arbitrum", AssetKind.Crypto));
        Assert.AreEqual("BTC", SymbolCatalog.ToFeedKey("BTC", AssetKind.Crypto));
    }

    [TestMethod]
    public void ToFeedKey_FxAndStock_NormalizePerKind()
    {
        Assert.AreEqual("EURUSD", SymbolCatalog.ToFeedKey("eur/usd", AssetKind.Fx));
        Assert.AreEqual("AAPL", SymbolCatalog.ToFeedKey("aapl", AssetKind.Stock));
    }

    [TestMethod]
    public void NormalizeSymbol_ResolvesAlias_ElseUppercases()
    {
        Assert.AreEqual("BTC", SymbolCatalog.NormalizeSymbol("bitcoin"));
        Assert.AreEqual("AAPL", SymbolCatalog.NormalizeSymbol("aapl"));
    }

    [TestMethod]
    public void DetectAssetKind_ExplicitType_Wins()
    {
        Assert.AreEqual(AssetKind.Crypto, SymbolCatalog.DetectAssetKind("AAPL", "Crypto"));
        Assert.AreEqual(AssetKind.Stock, SymbolCatalog.DetectAssetKind("BTC", "Stock"));
        Assert.AreEqual(AssetKind.Fx, SymbolCatalog.DetectAssetKind("BTC", "FX Pair"));
    }

    [TestMethod]
    public void DetectAssetKind_Auto_InferFromShape()
    {
        Assert.AreEqual(AssetKind.Fx, SymbolCatalog.DetectAssetKind("EUR/USD", "Auto"));
        Assert.AreEqual(AssetKind.Stock, SymbolCatalog.DetectAssetKind("AAPL", "Auto"));
        Assert.AreEqual(AssetKind.Crypto, SymbolCatalog.DetectAssetKind("BTC", "Auto"));
    }

    [TestMethod]
    public void CoinGeckoIdFor_KnownBaseCoin_ReturnsApiId()
    {
        Assert.AreEqual("bitcoin", SymbolCatalog.CoinGeckoIdFor("BTC"));
        Assert.AreEqual("arbitrum", SymbolCatalog.CoinGeckoIdFor("ARB"));
        Assert.IsNull(SymbolCatalog.CoinGeckoIdFor("UNKNOWN"));
    }

    [TestMethod]
    public void CryptoAliasTable_EveryBaseCoinResolvesAsItsOwnKey_WithCoinGeckoId()
    {
        foreach (string baseCoin in SymbolCatalog.CryptoAliases.Values.Select(a => a.Symbol).Distinct())
        {
            Assert.IsTrue(
                SymbolCatalog.CryptoAliases.TryGetValue(baseCoin, out var alias),
                $"{baseCoin} must resolve as its own alias key so the CoinGecko fallback can find it");
            Assert.AreEqual(baseCoin, alias.Symbol);
            Assert.IsFalse(string.IsNullOrEmpty(alias.CoinGeckoId), $"{baseCoin} must have a CoinGecko id");
        }
    }
}
