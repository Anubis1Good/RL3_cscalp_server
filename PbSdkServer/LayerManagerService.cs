using ICore;
using System.Collections.Concurrent;

public class LayerManagerService
{
    private readonly SdkManagerService _sdkManager;
    private readonly ConcurrentDictionary<string, InstrumentContext> _contexts = new();

    public LayerManagerService(SdkManagerService sdkManager)
    {
        _sdkManager = sdkManager;
        // При каждом реконнекте слота автоматически восстанавливаем все его слои
        _sdkManager.OnSlotReady += RecreateLayersForSlotAsync;
    }

    public async Task<Dictionary<string, string>> SubscribeAsync(string slotKey, List<string> tickers)
    {
        var slot = _sdkManager.GetSlot(slotKey)
            ?? throw new Exception($"Слот '{slotKey}' не найден или не подключен.");

        if (!slot.IsReady || slot.ContractsTable == null)
            throw new Exception($"Слот '{slotKey}' еще не готов (IsReady = false).");

        var results = new Dictionary<string, string>();

        foreach (var rawTicker in tickers)
        {
            string ticker = rawTicker.Trim().ToUpperInvariant();
            string contextKey = $"{slotKey}|{ticker}";

            if (_contexts.ContainsKey(contextKey))
            {
                results[ticker] = "Already subscribed";
                continue;
            }

            results[ticker] = await CreateOrReplaceContextAsync(slot, slotKey, ticker);
        }

        return results;
    }

    private async Task<string> CreateOrReplaceContextAsync(IDispatcherSlot slot, string slotKey, string ticker)
    {
        IContractInfo? contract = slot.ContractsTable!.SearchContractByID(ticker)
            ?? slot.ContractsTable.SearchContractBySymbol(ticker);

        if (contract == null)
            return "Error: Ticker not found";

        var (layer, isNew) = await slot.InvokeLayerAsync(contract, CancellationToken.None);
        if (layer == null)
            return "Error: Failed to invoke layer";

        var context = new InstrumentContext(slotKey, ticker, layer);
        _contexts[$"{slotKey}|{ticker}"] = context;

        if (isNew)
        {
            await slot.InvokeLayerEndAsync(this, layer, CancellationToken.None);
        }

        return $"Subscribed ({contract.SecurityID})";
    }

    private async Task RecreateLayersForSlotAsync(string slotKey)
    {
        var tickersToRestore = _contexts.Keys
            .Where(k => k.StartsWith($"{slotKey}|", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Split('|')[1])
            .ToList();

        if (tickersToRestore.Count == 0) return;

        var slot = _sdkManager.GetSlot(slotKey);
        if (slot == null || slot.ContractsTable == null) return;

        Console.WriteLine($"[{slotKey}] Восстанавливаем {tickersToRestore.Count} слоев после переподключения...");
        foreach (var ticker in tickersToRestore)
        {
            var res = await CreateOrReplaceContextAsync(slot, slotKey, ticker);
            Console.WriteLine($"[{slotKey} | {ticker}] Реконнект слоя: {res}");
        }
    }

    public InstrumentContext GetContext(string slotKey, string ticker)
    {
        string contextKey = $"{slotKey}|{ticker.Trim().ToUpperInvariant()}";
        if (!_contexts.TryGetValue(contextKey, out var ctx))
            throw new Exception($"Инструмент '{ticker}' на слоте '{slotKey}' не подписан. Сначала вызовите /subscribe.");

        return ctx;
    }
}
