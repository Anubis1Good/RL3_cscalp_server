using ICore;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Configuration.AddJsonFile("accounts.json", optional: false, reloadOnChange: false);

builder.Services.AddSingleton<SdkManagerService>();
builder.Services.AddSingleton<LayerManagerService>();

var app = builder.Build();

var accounts = builder.Configuration.GetSection("Accounts").Get<List<AccountConfig>>() ?? new();
Console.WriteLine($"Загружено аккаунтов из конфига: {accounts.Count}");

var sdkService = app.Services.GetRequiredService<SdkManagerService>();
await sdkService.InitializeAndConnectAsync(accounts);

app.Lifetime.ApplicationStopping.Register(() =>
{
    sdkService.StopAsync().GetAwaiter().GetResult();
});

// 1. Статус слотов и балансы
app.MapGet("/status", (SdkManagerService sdk) =>
{
    return Results.Ok(sdk.Slots.Select(kvp => new
    {
        Alias = kvp.Key,
        IsReady = kvp.Value.IsReady,
        IsConnected = kvp.Value.IsConnected
    }));
});

app.MapGet("/contracts", (string slot, string? search, SdkManagerService sdk) =>
{
    var s = sdk.GetSlot(slot);
    if (s?.ContractsTable == null)
        return Results.BadRequest(new { error = "Слот не найден или таблица не загружена" });

    var allContracts = new List<IContractInfo>();
    s.ContractsTable.GetAllContracts(ref allContracts);


    IEnumerable<IContractInfo> query = allContracts;
    if (!string.IsNullOrWhiteSpace(search))
    {
        query = query.Where(c =>
            c.Symbol.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            c.SecurityID.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    return Results.Ok(query.Take(30).Select(c => new
    {
        symbol = c.Symbol,
        securityId = c.SecurityID,
        step = c.Step,
        decimals = c.Decimals
    }));
});



// 2. Подписка на список тикеров
app.MapPost("/subscribe", async (SubscribeRequest req, LayerManagerService layers) =>
{
    try
    {
        var res = await layers.SubscribeAsync(req.Slot, req.Tickers);
        return Results.Ok(res);
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 3. Лучшие цены (BestBid / BestAsk)
app.MapGet("/best-prices", (string slot, string ticker, LayerManagerService layers) =>
{
    try
    {
        var ctx = layers.GetContext(slot, ticker);
        var stock = ctx.Layer.LastStock;
        if (stock == null || !ctx.Layer.IsStockReceived)
            return Results.BadRequest(new { error = "Стакан еще не получен" });

        return Results.Ok(new
        {
            bestBid = stock.BestBid,
            bestAsk = stock.BestAsk,
            lastPrice = ctx.Layer.LastTick?.Price ?? 0,
            step = ctx.Layer.ContractInfo.Step,
            decimals = ctx.Layer.ContractInfo.Decimals
        });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 4. Глубина стакана
app.MapGet("/orderbook", (string slot, string ticker, int? depth, LayerManagerService layers) =>
{
    try
    {
        var ctx = layers.GetContext(slot, ticker);
        var stock = ctx.Layer.LastStock;
        if (stock == null || !ctx.Layer.IsStockReceived)
            return Results.BadRequest(new { error = "Стакан еще не получен" });

        int maxDepth = depth ?? 20;
        var bids = new List<object>();
        var asks = new List<object>();

        lock (stock)
        {
            if (!stock.IsDisposed)
            {
                var bidSpan = stock.Bids;
                int bidCount = Math.Min(maxDepth, bidSpan.Length);
                for (int i = 0; i < bidCount; i++)
                {
                    bids.Add(new { price = bidSpan[i].Price, amount = bidSpan[i].Amount });
                }

                var askSpan = stock.Asks;
                int askCount = Math.Min(maxDepth, askSpan.Length);
                for (int i = 0; i < askCount; i++)
                {
                    asks.Add(new { price = askSpan[i].Price, amount = askSpan[i].Amount });
                }
            }
        }

        return Results.Ok(new
        {
            bestBid = stock.BestBid,
            bestBidAmount = stock.BestBidAmount,
            bestAsk = stock.BestAsk,
            bestAskAmount = stock.BestAskAmount,
            bids,
            asks
        });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});


// 5. График (Свечи OHLCV)
app.MapGet("/candles", async (string slot, string ticker, int? tf_minutes, int? limit, LayerManagerService layers) =>
{
    try
    {
        var ctx = layers.GetContext(slot, ticker);
        var candles = await ctx.GetCandlesAsync(tf_minutes ?? 5, limit ?? 200);
        return Results.Ok(candles);
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 6. Сырая лента тиков
app.MapGet("/ticks", (string slot, string ticker, int? limit, LayerManagerService layers) =>
{
    try
    {
        var ctx = layers.GetContext(slot, ticker);
        return Results.Ok(ctx.GetTicks(limit ?? 500));
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 7. Кластера
app.MapGet("/clusters", async (string slot, string ticker, int? tf_minutes, int? limit, LayerManagerService layers) =>
{
    try
    {
        var ctx = layers.GetContext(slot, ticker);
        var clusters = await ctx.GetClustersAsync(tf_minutes ?? 5, limit ?? 50);
        return Results.Ok(clusters);
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 8. Текущая позиция
app.MapGet("/position", (string slot, string ticker, LayerManagerService layers) =>
{
    try
    {
        var ctx = layers.GetContext(slot, ticker);
        return Results.Ok(new
        {
            amount = ctx.Layer.Posmonitor._PositionAmount,
            avgPrice = ctx.Layer.Posmonitor._PositionPrice,
            isReplicationsDone = ctx.Layer.IsReplicationsDone
        });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 9. Активные заявки
app.MapGet("/orders", (string slot, string ticker, LayerManagerService layers) =>
{
    try
    {
        var ctx = layers.GetContext(slot, ticker);
        var limitOrders = ctx.Layer.Lomonitor._List.Select(o => new
        {
            id = o._ID,
            price = o._Price,
            sourceAmount = o._SourceAmount,
            remainAmount = o._RemainAmount,
            direction = o._Direction.ToString(),
            state = o._LastState.ToString()
        });
        var stopOrders = ctx.Layer.Somonitor._List.Select(o => new
        {
            id = o._ID,
            stopPrice = o._StopPrice,
            amount = o._Amount,
            direction = o._Direction.ToString(),
            state = o._LastState.ToString()
        });
        return Results.Ok(new { limitOrders, stopOrders });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});


// 10. Отправка лимитной заявки (с авторасчетом лучшей мейкерской цены, если price == null)
app.MapPost("/order", (OrderRequest req, LayerManagerService layers) =>
{
    try
    {
        var ctx = layers.GetContext(req.Slot, req.Ticker);
        var layer = ctx.Layer;

        if (!layer.DispatcherSlot.IsReady || !layer.IsReplicationsDone || !layer.IsStockReceived || layer.LastStock == null)
        {
            return Results.BadRequest(new
            {
                error = "Слой еще не готов к торговле",
                slotReady = layer.DispatcherSlot.IsReady,
                replicationsDone = layer.IsReplicationsDone,
                isReplications = layer.IsReplications,
                stockReceived = layer.IsStockReceived
            });
        }


        bool isBuy = req.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase);
        bool isSell = req.Side.Equals("SELL", StringComparison.OrdinalIgnoreCase);
        if (!isBuy && !isSell)
            return Results.BadRequest(new { error = "Поле Side должно быть 'BUY' или 'SELL'" });

        double step = layer.ContractInfo.Step;
        int decimals = layer.ContractInfo.Decimals;
        double finalPrice;

        if (req.Price.HasValue && req.Price.Value > 0)
        {
            // Округляем переданную цену до шага инструмента
            finalPrice = Math.Round(Math.Round(req.Price.Value / step) * step, decimals);
        }
        else
        {
            // Автоцена: встаем лучшим в стакан, если позволяет спред, иначе вместе с лучшим
            double bestBid = layer.LastStock.BestBid;
            double bestAsk = layer.LastStock.BestAsk;

            if (bestBid <= 0 || bestAsk <= 0 || bestAsk < bestBid)
                return Results.BadRequest(new { error = "Некорректный стакан для авторасчета цены" });

            if (isBuy)
            {
                double nextTick = Math.Round(bestBid + step, decimals);
                finalPrice = nextTick < bestAsk ? nextTick : bestBid;
            }
            else
            {
                double nextTick = Math.Round(bestAsk - step, decimals);
                finalPrice = nextTick > bestBid ? nextTick : bestAsk;
            }
        }

        OrderAction action = isBuy ? OrderAction.Buy : OrderAction.Sell;
        bool success = layer.SendOrder(finalPrice, req.Amount, action, OrderFlags.Limit);

        return Results.Ok(new
        {
            success,
            side = req.Side.ToUpperInvariant(),
            price = finalPrice,
            amount = req.Amount
        });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// 11. Снятие заявок (конкретной по ID или всех по тикеру)
app.MapPost("/cancel", (CancelRequest req, LayerManagerService layers) =>
{
    try
    {
        var ctx = layers.GetContext(req.Slot, req.Ticker);
        var layer = ctx.Layer;

        if (!string.IsNullOrWhiteSpace(req.OrderId))
        {
            bool ok = layer.CancelOrder(req.OrderId, wait_cancel: true);
            if (!ok) ok = layer.CancelStopOrder(req.OrderId, wait_cancel: true);
            return Results.Ok(new { success = ok, canceledId = req.OrderId });
        }
        else
        {
            foreach (var o in layer.Lomonitor._List.ToList())
                layer.CancelOrder(o._ID, wait_cancel: true);
            foreach (var o in layer.Somonitor._List.ToList())
                layer.CancelStopOrder(o._ID, wait_cancel: true);

            return Results.Ok(new { success = true, message = "Все заявки по инструменту сняты" });
        }
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});




app.Run("http://localhost:7777");

// Модели входящих JSON-запросов
public record SubscribeRequest(string Slot, List<string> Tickers);
public record OrderRequest(string Slot, string Ticker, string Side, decimal Amount, double? Price = null);
public record CancelRequest(string Slot, string Ticker, string? OrderId = null);
