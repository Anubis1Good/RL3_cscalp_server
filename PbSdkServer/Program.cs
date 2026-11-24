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
// 3. Лучшие цены (с учетом только чужих заявок в стакане)
app.MapGet("/best-prices", (string slot, string ticker, LayerManagerService layers) =>
{
    try
    {
        var ctx = layers.GetContext(slot, ticker);
        var layer = ctx.Layer;
        var stock = layer.LastStock;
        if (stock == null || !layer.IsStockReceived)
            return Results.BadRequest(new { error = "Стакан еще не получен" });

        var (ok, fBid, fAsk, _) = GetForeignBestPrices(layer);

        return Results.Ok(new
        {
            bestBid = ok ? fBid : stock.BestBid,
            bestAsk = ok ? fAsk : stock.BestAsk,
            rawBestBid = stock.BestBid,
            rawBestAsk = stock.BestAsk,
            lastPrice = layer.LastTick?.Price ?? 0,
            step = layer.ContractInfo.Step,
            decimals = layer.ContractInfo.Decimals
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
            // Автоцена: встаем перед первой ЧУЖОЙ заявкой, если позволяет спред, иначе вместе с ней
            var (ok, foreignBestBid, foreignBestAsk, err) = GetForeignBestPrices(layer);
            if (!ok)
                return Results.BadRequest(new { error = err });

            if (isBuy)
            {
                double nextTick = Math.Round(foreignBestBid + step, decimals);
                finalPrice = nextTick < foreignBestAsk ? nextTick : foreignBestBid;
            }
            else
            {
                double nextTick = Math.Round(foreignBestAsk - step, decimals);
                finalPrice = nextTick > foreignBestBid ? nextTick : foreignBestAsk;
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

// 12. Декларативная синхронизация заявок (с учетом первой чужой заявки в стакане)
app.MapPost("/sync-orders", (SyncOrdersRequest req, LayerManagerService layers) =>
{
    try
    {
        var ctx = layers.GetContext(req.Slot, req.Ticker);
        var layer = ctx.Layer;

        if (!layer.DispatcherSlot.IsReady || !layer.IsReplicationsDone || !layer.IsStockReceived || layer.LastStock == null)
        {
            return Results.BadRequest(new { error = "Слой еще не готов к торговле" });
        }

        double step = layer.ContractInfo.Step;
        int decimals = layer.ContractInfo.Decimals;
        double eps = step * 0.1;

        var incomingOrders = req.Orders ?? new List<SyncOrderItem>();
        double foreignBestBid = 0;
        double foreignBestAsk = 0;

        // Ищем первую чужую заявку только если есть заявки с автоценой (Price == null)
        if (incomingOrders.Any(o => o.Amount > 0 && (!o.Price.HasValue || o.Price.Value <= 0)))
        {
            var (ok, fBid, fAsk, err) = GetForeignBestPrices(layer);
            if (!ok)
                return Results.BadRequest(new { error = err });

            foreignBestBid = fBid;
            foreignBestAsk = fAsk;
        }

        // Формируем целевой список заявок с точными ценами
        var desiredList = new List<(OrderAction Side, decimal Amount, double Price)>();
        foreach (var item in incomingOrders)
        {
            if (item.Amount <= 0) continue;

            bool isBuy = item.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase);
            bool isSell = item.Side.Equals("SELL", StringComparison.OrdinalIgnoreCase);
            if (!isBuy && !isSell)
                return Results.BadRequest(new { error = $"Некорректная сторона заявки: {item.Side}" });

            double finalPrice;
            if (item.Price.HasValue && item.Price.Value > 0)
            {
                finalPrice = Math.Round(Math.Round(item.Price.Value / step) * step, decimals);
            }
            else
            {
                if (isBuy)
                {
                    double nextTick = Math.Round(foreignBestBid + step, decimals);
                    finalPrice = nextTick < foreignBestAsk ? nextTick : foreignBestBid;
                }
                else
                {
                    double nextTick = Math.Round(foreignBestAsk - step, decimals);
                    finalPrice = nextTick > foreignBestBid ? nextTick : foreignBestAsk;
                }
            }

            desiredList.Add((isBuy ? OrderAction.Buy : OrderAction.Sell, item.Amount, finalPrice));
        }

                // Сверяем с активными лимитками: совпавшие или частично исполненные в ту же сторону оставляем
        var activeOrders = layer.Lomonitor._List.Where(o => o._RemainAmount > 0).ToList();
        var unmatchedDesired = new List<(OrderAction Side, decimal Amount, double Price)>(desiredList);
        int canceledCount = 0;

        foreach (var existing in activeOrders)
        {
            bool isPartiallyFilled = existing._RemainAmount < existing._SourceAmount;

            // 1. Ищем точное совпадение по стороне, объему и цене
            int matchIdx = unmatchedDesired.FindIndex(d =>
                d.Side == existing._Direction &&
                d.Amount == existing._RemainAmount &&
                Math.Abs(d.Price - existing._Price) < eps);

            // 2. Если точного совпадения по цене нет, но заявка ЧАСТИЧНО ИСПОЛНЕНА и направление то же — замораживаем её!
            if (matchIdx < 0 && req.FreezePartial && isPartiallyFilled)
            {
                matchIdx = unmatchedDesired.FindIndex(d =>
                    d.Side == existing._Direction &&
                    d.Amount == existing._RemainAmount);
            }

            if (matchIdx >= 0)
            {
                unmatchedDesired.RemoveAt(matchIdx); // Оставляем заявку в стакане без снятия!
            }
            else
            {
                layer.CancelOrder(existing._ID, wait_cancel: true);
                canceledCount++;
            }
        }

        // Выставляем недостающие заявки
        int placedCount = 0;
        foreach (var d in unmatchedDesired)
        {
            if (layer.SendOrder(d.Price, d.Amount, d.Side, OrderFlags.Limit))
                placedCount++;
        }

        return Results.Ok(new
        {
            success = true,
            foreignBestBid,
            foreignBestAsk,
            kept = desiredList.Count - unmatchedDesired.Count,
            canceled = canceledCount,
            placed = placedCount
        });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/debug-cluster-type", () =>
{
    var t = typeof(TickGraphicData);
    var props = t.GetProperties().Select(p => $"{p.PropertyType.Name} {p.Name}");
    var fields = t.GetFields().Select(f => $"{f.FieldType.Name} {f.Name}");
    return Results.Ok(new { props, fields });
});





app.Run("http://localhost:7777");

static (bool ok, double foreignBestBid, double foreignBestAsk, string error) GetForeignBestPrices(ILayer layer)
{
    int decimals = layer.ContractInfo.Decimals;
    var myBuyVolByPrice = new Dictionary<double, double>();
    var mySellVolByPrice = new Dictionary<double, double>();

    foreach (var o in layer.Lomonitor._List)
    {
        if (o._RemainAmount <= 0) continue;
        double p = Math.Round((double)o._Price, decimals);
        double amt = (double)o._RemainAmount;

        if (o._Direction == OrderAction.Buy)
            myBuyVolByPrice[p] = myBuyVolByPrice.GetValueOrDefault(p) + amt;
        else if (o._Direction == OrderAction.Sell)
            mySellVolByPrice[p] = mySellVolByPrice.GetValueOrDefault(p) + amt;
    }

    double foreignBestBid = 0;
    double foreignBestAsk = 0;

    var stock = layer.LastStock;
    if (stock == null)
        return (false, 0, 0, "Стакан равен null");

    lock (stock)
    {
        if (stock.IsDisposed)
            return (false, 0, 0, "Стакан недоступен (Disposed)");

        var bidSpan = stock.Bids;
        for (int i = 0; i < bidSpan.Length; i++)
        {
            double p = Math.Round((double)bidSpan[i].Price, decimals);
            double myVol = myBuyVolByPrice.GetValueOrDefault(p);
            if ((double)bidSpan[i].Amount - myVol > 1e-9)
            {
                foreignBestBid = p;
                break;
            }
        }

        var askSpan = stock.Asks;
        for (int i = 0; i < askSpan.Length; i++)
        {
            double p = Math.Round((double)askSpan[i].Price, decimals);
            double myVol = mySellVolByPrice.GetValueOrDefault(p);
            if ((double)askSpan[i].Amount - myVol > 1e-9)
            {
                foreignBestAsk = p;
                break;
            }
        }
    }

    if (foreignBestBid <= 0 || foreignBestAsk <= 0 || foreignBestAsk < foreignBestBid)
        return (false, foreignBestBid, foreignBestAsk, "Не найдены чужие заявки в стакане для автоцены");

    return (true, foreignBestBid, foreignBestAsk, "");
}


// Модели входящих JSON-запросов
public record SubscribeRequest(string Slot, List<string> Tickers);
public record OrderRequest(string Slot, string Ticker, string Side, decimal Amount, double? Price = null);
public record CancelRequest(string Slot, string Ticker, string? OrderId = null);

public record SyncOrderItem(string Side, decimal Amount, double? Price = null);
public record SyncOrdersRequest(string Slot, string Ticker, List<SyncOrderItem> Orders, bool FreezePartial = true);

