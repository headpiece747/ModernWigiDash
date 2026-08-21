using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The price-feed symbol catalog: the single crypto alias table, symbol/FX
/// validation and normalization, and the asset-kind/feed-key mapping.
/// Extracted from PriceFeedManager so the symbol surface is directly
/// testable; the manager keeps the subscriptions, loops, and price map.
/// </summary>
internal static class SymbolCatalog
{
    /// <summary>Canonical base coin for a crypto alias plus its CoinGecko API id.</summary>
    internal sealed record CryptoAlias(string Symbol, string CoinGeckoId);

    /// <summary>
    /// One crypto symbol table: user-facing alias → canonical base coin + the
    /// CoinGecko API id used by the REST fallback. A single table makes a
    /// symbol with a working live feed but a missing fallback id
    /// unrepresentable — the fallback can never silently lose a coin.
    /// </summary>
    internal static readonly FrozenDictionary<string, CryptoAlias> CryptoAliases =
        new Dictionary<string, CryptoAlias>(StringComparer.OrdinalIgnoreCase)
        {
            ["bitcoin"] = new("BTC", "bitcoin"),
            ["btc"] = new("BTC", "bitcoin"),
            ["ethereum"] = new("ETH", "ethereum"),
            ["eth"] = new("ETH", "ethereum"),
            ["solana"] = new("SOL", "solana"),
            ["sol"] = new("SOL", "solana"),
            ["dogecoin"] = new("DOGE", "dogecoin"),
            ["doge"] = new("DOGE", "dogecoin"),
            ["cardano"] = new("ADA", "cardano"),
            ["ada"] = new("ADA", "cardano"),
            ["ripple"] = new("XRP", "ripple"),
            ["xrp"] = new("XRP", "ripple"),
            ["polkadot"] = new("DOT", "polkadot"),
            ["dot"] = new("DOT", "polkadot"),
            ["litecoin"] = new("LTC", "litecoin"),
            ["ltc"] = new("LTC", "litecoin"),
            ["avalanche-2"] = new("AVAX", "avalanche-2"),
            ["avax"] = new("AVAX", "avalanche-2"),
            ["chainlink"] = new("LINK", "chainlink"),
            ["link"] = new("LINK", "chainlink"),
            ["polygon"] = new("POL", "polygon-ecosystem-token"),
            ["pol"] = new("POL", "polygon-ecosystem-token"),
            ["matic-network"] = new("MATIC", "matic-network"),
            ["matic"] = new("MATIC", "matic-network"),
            ["tron"] = new("TRX", "tron"),
            ["trx"] = new("TRX", "tron"),
            ["shiba-inu"] = new("SHIB", "shiba-inu"),
            ["shib"] = new("SHIB", "shiba-inu"),
            ["uniswap"] = new("UNI", "uniswap"),
            ["uni"] = new("UNI", "uniswap"),
            ["cosmos"] = new("ATOM", "cosmos"),
            ["atom"] = new("ATOM", "cosmos"),
            ["near"] = new("NEAR", "near"),
            ["aptos"] = new("APT", "aptos"),
            ["apt"] = new("APT", "aptos"),
            ["arbitrum"] = new("ARB", "arbitrum"),
            ["arb"] = new("ARB", "arbitrum"),
            ["optimism"] = new("OP", "optimism"),
            ["op"] = new("OP", "optimism"),
            ["sui"] = new("SUI", "sui"),
            ["render"] = new("RNDR", "render-token"),
            ["rndr"] = new("RNDR", "render-token"),
            ["filecoin"] = new("FIL", "filecoin"),
            ["fil"] = new("FIL", "filecoin"),
            ["theta"] = new("THETA", "theta-token"),
            ["bnb"] = new("BNB", "binancecoin"),
            ["toncoin"] = new("TON", "the-open-network"),
            ["ton"] = new("TON", "the-open-network"),
            ["mantle"] = new("MNT", "mantle"),
            ["mnt"] = new("MNT", "mantle"),
            ["injective"] = new("INJ", "injective"),
            ["inj"] = new("INJ", "injective"),
            ["pepe"] = new("PEPE", "pepe"),
            ["floki"] = new("FLOKI", "floki"),
            ["bonk"] = new("BONK", "bonk"),
            ["hedera"] = new("HBAR", "hedera-hashgraph"),
            ["hbar"] = new("HBAR", "hedera-hashgraph"),
            ["vechain"] = new("VET", "vechain"),
            ["vet"] = new("VET", "vechain"),
            ["aave"] = new("AAVE", "aave"),
            ["maker"] = new("MKR", "maker"),
            ["mkr"] = new("MKR", "maker"),
            ["curve"] = new("CRV", "curve-dao-token"),
            ["crv"] = new("CRV", "curve-dao-token"),
            ["eos"] = new("EOS", "eos"),
            ["fetch"] = new("FET", "fetch-ai"),
            ["fet"] = new("FET", "fetch-ai"),
            ["fetch-ai"] = new("FET", "fetch-ai"),
            ["the-graph"] = new("GRT", "the-graph"),
            ["grt"] = new("GRT", "the-graph"),
            ["sei"] = new("SEI", "sei"),
            ["starknet"] = new("STRK", "starknet"),
            ["strk"] = new("STRK", "starknet"),
            ["immutable"] = new("IMX", "immutable-x"),
            ["imx"] = new("IMX", "immutable-x"),
            ["dydx"] = new("DYDX", "dydx"),
            ["pendle"] = new("PENDLE", "pendle"),
            ["kaspa"] = new("KAS", "kaspa"),
            ["kas"] = new("KAS", "kaspa"),
            ["fantom"] = new("FTM", "fantom"),
            ["ftm"] = new("FTM", "fantom"),
            ["algorand"] = new("ALGO", "algorand"),
            ["algo"] = new("ALGO", "algorand"),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> KnownCryptos = CryptoAliases.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the CoinGecko API id for a canonical base coin, or null when unknown.</summary>
    internal static string? CoinGeckoIdFor(string baseCoin)
        => CryptoAliases.TryGetValue(baseCoin, out var alias) ? alias.CoinGeckoId : null;

    /// <summary>
    /// Accepts only well-formed ticker/pair symbols (ASCII letters, digits, and
    /// '.', '-', ':' separators, up to 32 chars) so user-typed input can never
    /// pollute feed URLs or subscription payloads.
    /// </summary>
    internal static bool IsValidSymbol(string symbol) =>
        symbol.Length is > 0 and <= 32 && symbol.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':');

    /// <summary>FX pair keys are exactly six letters (e.g. "EURUSD").</summary>
    internal static bool IsValidFxKey(string key) =>
        key.Length == 6 && key.All(char.IsAsciiLetter);

    /// <summary>
    /// Validates an FX subscription input: either the "XXX/YYY" pair form (both
    /// halves and the shape checked by <see cref="TryParseFxPair"/>) or a bare
    /// six-letter key. Returns the normalized key when valid.
    /// </summary>
    internal static bool IsValidFxInput(string symbol, out string fxKey)
    {
        fxKey = "";
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        if (symbol.Contains('/'))
        {
            if (!TryParseFxPair(symbol, out _, out _)) return false;
        }
        else if (!IsValidSymbol(symbol))
        {
            return false;
        }
        fxKey = NormalizeFxKey(symbol);
        return IsValidFxKey(fxKey);
    }

    internal static void LogInvalidSymbol(string? symbol)
    {
        // Config path: a wrongly-typed symbol must be diagnosable in the
        // field — the shared log, not a Debug-only line. The preview keeps the
        // null spelling for diagnosis but routes through the ONE sanitization
        // rule (flatten + bound) so this line can never drift from the rest of
        // the price-feed logging.
        FileLog.Write($"[PRICE-FEED] Skipping invalid feed symbol '{LogSanitizer.Sanitize(symbol ?? "<null>")}'");
    }

    internal static bool IsCrypto(string symbol) => KnownCryptos.Contains(symbol);

    internal static string NormalizeSymbol(string symbol) =>
        CryptoAliases.TryGetValue(symbol, out var alias) ? alias.Symbol : symbol.ToUpperInvariant();

    /// <summary>
    /// Maps a user-entered symbol to the canonical feed key for an asset kind:
    /// crypto aliases resolve to the base coin (e.g. "bitcoin" → "BTC"), FX
    /// pairs to "EURUSD", everything else to the upper-cased symbol.
    /// </summary>
    internal static string ToFeedKey(string symbol, AssetKind kind) => kind switch
    {
        AssetKind.Crypto => CryptoAliases.TryGetValue(symbol, out var alias) ? alias.Symbol : symbol.ToUpperInvariant(),
        AssetKind.Fx => NormalizeFxKey(symbol),
        _ => symbol.ToUpperInvariant()
    };

    // Timeout guards the match against catastrophic backtracking on hostile input.
    private static readonly Regex FxPairRegex = new("^(?<base>[A-Za-z]{3})/(?<quote>[A-Za-z]{3})$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    internal static bool TryParseFxPair(string symbol, out string baseCurrency, out string quoteCurrency)
    {
        baseCurrency = "";
        quoteCurrency = "";
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        Match match = FxPairRegex.Match(symbol.Trim());
        if (!match.Success) return false;
        baseCurrency = match.Groups["base"].Value.ToUpperInvariant();
        quoteCurrency = match.Groups["quote"].Value.ToUpperInvariant();
        return true;
    }

    internal static string NormalizeFxKey(string symbol)
        => symbol.Trim().ToUpperInvariant().Replace("/", "", StringComparison.Ordinal);

    internal static AssetKind DetectAssetKind(string symbol, string assetType) => assetType switch
    {
        "Crypto" => AssetKind.Crypto,
        "Stock" => AssetKind.Stock,
        "FX Pair" => AssetKind.Fx,
        _ when TryParseFxPair(symbol, out _, out _) => AssetKind.Fx,
        _ => IsCrypto(symbol) ? AssetKind.Crypto : AssetKind.Stock
    };
}
