using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LootView.Services;

/// <summary>
/// Looks up market board prices from Universalis for the player's home world.
///
/// Requests run in the background and results are cached, so the loot window only ever
/// reads what has already arrived. Nothing here touches game state or ImGui.
/// </summary>
public class MarketPriceService : IDisposable
{
    /// <summary>
    /// Universalis times out on large batches - 100 ids returns HTTP 504, 50 takes about
    /// seven seconds. Twenty comes back in under two.
    /// </summary>
    private const int BatchSize = 20;

    /// <summary>How long a fetched price stays usable before it is queued again.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Spacing between batches. Universalis allows 25 requests a second; this is far under.</summary>
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Back-off after a failed batch, so a broken world name cannot spin.</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);

    /// <summary>The only response fields we read. Everything else is stripped server side.</summary>
    private static readonly string[] FieldNames =
    [
        "itemID", "averagePriceNQ", "averagePriceHQ", "minPriceNQ", "minPriceHQ", "hasData"
    ];

    private static readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly ConfigurationService configService;
    private readonly CancellationTokenSource cancellation = new();

    private readonly Dictionary<uint, MarketPrice> cache = new();
    private readonly HashSet<uint> pending = new();
    private readonly object cacheLock = new();

    private readonly Dictionary<uint, bool> marketableCache = new();
    private readonly object marketableLock = new();

    private Task worker;
    private DateTime failedUntil = DateTime.MinValue;
    private bool warnedAboutWorld;

    public MarketPriceService(ConfigurationService configService)
    {
        this.configService = configService;

        if (!httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            httpClient.DefaultRequestHeaders.Add("User-Agent", "LootView-FFXIV-Plugin (+https://github.com/GitHixy/LootView)");
        }
    }

    /// <summary>A single item's market board prices, as of when they were fetched.</summary>
    public readonly record struct MarketPrice(
        uint ItemId,
        double AverageNq,
        double AverageHq,
        double MinNq,
        double MinHq,
        bool HasData,
        DateTime FetchedAt)
    {
        /// <summary>Recent average sale price for the quality in question.</summary>
        public double Average(bool hq)
        {
            var value = hq ? AverageHq : AverageNq;
            // Quiet items often have history for one quality only; fall back rather than show zero.
            return value > 0 ? value : (hq ? AverageNq : AverageHq);
        }

        /// <summary>Cheapest listing right now for the quality in question.</summary>
        public double Minimum(bool hq)
        {
            var value = hq ? MinHq : MinNq;
            return value > 0 ? value : (hq ? MinNq : MinHq);
        }
    }

    /// <summary>The world prices are quoted for, or null when it cannot be determined yet.</summary>
    public string WorldName { get; private set; }

    /// <summary>True while a batch is in flight.</summary>
    public bool IsFetching { get; private set; }

    /// <summary>True once the home world is known and lookups can actually run.</summary>
    public bool HasWorld => !string.IsNullOrEmpty(WorldName);

    /// <summary>True while backing off after a failed request.</summary>
    public bool IsPaused => DateTime.Now < failedUntil;

    /// <summary>True when at least one lookup is queued or running.</summary>
    public bool HasWork
    {
        get
        {
            lock (cacheLock) return pending.Count > 0;
        }
    }

    /// <summary>
    /// True when the item can appear on the market board at all. Answered from the game's
    /// own sheets, so filtering costs nothing and unsellable items are never requested.
    ///
    /// Resolved one row at a time and memoised. Scanning the whole item sheet up front
    /// cost a visible hitch on the frame the panel first appeared, and every loot list
    /// only ever asks about a handful of ids.
    /// </summary>
    public bool IsMarketable(uint itemId)
    {
        if (itemId == 0) return false;

        lock (marketableLock)
        {
            if (marketableCache.TryGetValue(itemId, out var known)) return known;

            var marketable = false;
            try
            {
                var row = Plugin.DataManager.GameData?.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRow(itemId);

                // ItemSearchCategory is what puts an item in the market board's browser;
                // untradable items are excluded even when they have one.
                marketable = row.HasValue
                             && row.Value.ItemSearchCategory.RowId > 0
                             && !row.Value.IsUntradable;
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug(ex, "Market: could not read item {ItemId}", itemId);
            }

            marketableCache[itemId] = marketable;
            return marketable;
        }
    }

    /// <summary>A cached price, if one has been fetched and has not gone stale.</summary>
    public bool TryGetPrice(uint itemId, out MarketPrice price)
    {
        lock (cacheLock)
        {
            return cache.TryGetValue(itemId, out price);
        }
    }

    /// <summary>
    /// Queues any of these items whose prices are missing or stale. Safe to call every
    /// frame: it only starts a worker when there is something new to fetch.
    /// </summary>
    public void RequestPrices(IEnumerable<uint> itemIds)
    {
        if (!configService.Configuration.EnableMarketPrices) return;
        if (DateTime.Now < failedUntil) return;

        var world = ResolveWorldName();
        if (string.IsNullOrEmpty(world)) return;

        var now = DateTime.Now;

        lock (cacheLock)
        {
            foreach (var id in itemIds)
            {
                if (id == 0 || pending.Contains(id)) continue;
                if (cache.TryGetValue(id, out var cached) && now - cached.FetchedAt < CacheLifetime) continue;
                if (!IsMarketable(id)) continue;

                pending.Add(id);
            }

            if (pending.Count == 0) return;
        }

        if (worker is { IsCompleted: false }) return;

        worker = Task.Run(() => DrainQueueAsync(world), cancellation.Token);
    }

    /// <summary>Drops every cached price, so the next request refetches.</summary>
    public void ClearCache()
    {
        lock (cacheLock)
        {
            cache.Clear();
            pending.Clear();
        }
        failedUntil = DateTime.MinValue;
        WorldName = null;
        warnedAboutWorld = false;
    }

    /// <summary>
    /// The world Universalis should price against. Resolved once and remembered, because
    /// without it there is nothing to query and the whole feature silently does nothing.
    /// </summary>
    private string ResolveWorldName()
    {
        if (!string.IsNullOrEmpty(WorldName)) return WorldName;

        try
        {
            // IPlayerState is the purpose-built accessor and stays valid across zone
            // changes; the local player object can briefly be null during them.
            var world = Plugin.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText();

            if (string.IsNullOrEmpty(world))
            {
                world = Plugin.ObjectTable.LocalPlayer?.HomeWorld.ValueNullable?.Name.ExtractText();
            }

            if (!string.IsNullOrEmpty(world))
            {
                WorldName = world;
                warnedAboutWorld = false;
                Plugin.Log.Info($"Market: pricing against home world '{world}'");
            }
            else if (!warnedAboutWorld)
            {
                warnedAboutWorld = true;
                Plugin.Log.Warning("Market: could not resolve your home world, so price lookups are paused");
            }
        }
        catch (Exception ex)
        {
            if (!warnedAboutWorld)
            {
                warnedAboutWorld = true;
                Plugin.Log.Error(ex, "Market: failed to resolve the home world");
            }
        }

        return WorldName;
    }

    private async Task DrainQueueAsync(string world)
    {
        IsFetching = true;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                uint[] batch;
                lock (cacheLock)
                {
                    if (pending.Count == 0) return;
                    batch = pending.Take(BatchSize).ToArray();
                }

                var ok = await FetchBatchAsync(world, batch).ConfigureAwait(false);

                lock (cacheLock)
                {
                    foreach (var id in batch) pending.Remove(id);
                }

                if (!ok)
                {
                    // Give the API - or the network - room to recover before trying again.
                    failedUntil = DateTime.Now + FailureBackoff;
                    lock (cacheLock) pending.Clear();
                    return;
                }

                await Task.Delay(BatchDelay, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Plugin is unloading.
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Market price worker failed");
            failedUntil = DateTime.Now + FailureBackoff;
        }
        finally
        {
            IsFetching = false;
        }
    }

    private async Task<bool> FetchBatchAsync(string world, uint[] itemIds)
    {
        try
        {
            var ids = string.Join(",", itemIds);

            // A one-id query returns the item object directly, with no "items" wrapper, and
            // the field filter has to match that shape: asking for "items.hasData" against a
            // bare object strips the entire response and yields {}.
            var prefix = itemIds.Length == 1 ? string.Empty : "items.";
            var fields = string.Join(",", FieldNames.Select(f => prefix + f));

            // entries must be non-zero: the average prices are derived from recent sale
            // history, and asking for none of it returns zeroes. listings we never read.
            var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(world)}/{ids}" +
                      $"?listings=0&entries=5&fields={Uri.EscapeDataString(fields)}";

            using var response = await httpClient.GetAsync(url, cancellation.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Plugin.Log.Warning($"Universalis returned {(int)response.StatusCode} for {itemIds.Length} item(s)");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(cancellation.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var fetchedAt = DateTime.Now;
            var results = new List<MarketPrice>();

            // A multi-item query nests results under "items"; a single id returns the
            // object on its own, so both shapes have to be handled.
            if (document.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var entry in items.EnumerateObject())
                {
                    results.Add(ReadPrice(entry.Value, fetchedAt));
                }
            }
            else
            {
                results.Add(ReadPrice(document.RootElement, fetchedAt));
            }

            var withData = results.Count(r => r.HasData);
            Plugin.Log.Info($"Market: fetched {results.Count} price(s) from Universalis for {world}, {withData} with data");

            lock (cacheLock)
            {
                foreach (var price in results)
                {
                    if (price.ItemId > 0) cache[price.ItemId] = price;
                }

                // Items the API said nothing about still get an entry, so we do not ask again
                // on the next frame.
                foreach (var id in itemIds)
                {
                    if (!cache.ContainsKey(id))
                    {
                        cache[id] = new MarketPrice(id, 0, 0, 0, 0, false, fetchedAt);
                    }
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to fetch market prices from Universalis");
            return false;
        }
    }

    private static MarketPrice ReadPrice(JsonElement element, DateTime fetchedAt)
    {
        return new MarketPrice(
            ItemId: ReadUInt(element, "itemID"),
            AverageNq: ReadDouble(element, "averagePriceNQ"),
            AverageHq: ReadDouble(element, "averagePriceHQ"),
            MinNq: ReadDouble(element, "minPriceNQ"),
            MinHq: ReadDouble(element, "minPriceHQ"),
            HasData: element.TryGetProperty("hasData", out var h) && h.ValueKind == JsonValueKind.True,
            FetchedAt: fetchedAt);
    }

    private static uint ReadUInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n) ? n : 0;

    private static double ReadDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : 0;

    public void Dispose()
    {
        try
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error disposing MarketPriceService");
        }
    }
}
