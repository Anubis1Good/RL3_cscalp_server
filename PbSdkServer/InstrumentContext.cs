using ICore;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

public record CandleDto(DateTime Time, double Open, double High, double Low, double Close, decimal Volume);
public record TickDto(DateTime Time, double Price, decimal Amount, string Direction);

public class InstrumentContext : ILayerEventsReceiver
{
    public ILayer Layer { get; }
    public string SlotKey { get; }
    public string Ticker { get; }

    // Кольцевой буфер для сырой ленты тиков (храним последние 5000 тиков)
    private readonly ConcurrentQueue<TickDto> _ticks = new();
    private const int MaxTicksBuffer = 5000;

    // Ожидатели ответов от SDK для свечей и кластеров
    private TaskCompletionSource<List<BaseTicksCandle>>? _candlesTcs;
    private TaskCompletionSource<List<TickGraphicData>>? _clustersTcs;

    // Кэш свечей по таймфрейму (в минутах) для мгновенной достройки из тиков
    private readonly ConcurrentDictionary<int, List<CandleDto>> _cachedCandles = new();
    private readonly object _candleLock = new();

    public InstrumentContext(string slotKey, string ticker, ILayer layer)
    {
        SlotKey = slotKey;
        Ticker = ticker;
        Layer = layer;
        Layer.AddLayerEventsReceiverAsync(this, CancellationToken.None).GetAwaiter().GetResult();
    }

    public LayerEventsReceiverSubscribtions LayerSubscriptions =>
        LayerEventsReceiverSubscribtions.Assets |
        LayerEventsReceiverSubscribtions.OrderStates |
        LayerEventsReceiverSubscribtions.OrderStock |
        LayerEventsReceiverSubscribtions.Position |
        LayerEventsReceiverSubscribtions.Replications |
        LayerEventsReceiverSubscribtions.Trades |
        LayerEventsReceiverSubscribtions.Ticks;

    // --- Запрос и кэширование свечей ---
    public async Task<List<CandleDto>> GetCandlesAsync(int tfMinutes, int limit)
    {
        // Если история по этому таймфрейму уже загружена и обновляется тиками — отдаем из памяти
        if (_cachedCandles.TryGetValue(tfMinutes, out var existing) && existing.Count >= limit)
        {
            lock (_candleLock)
            {
                return existing.TakeLast(limit).ToList();
            }
        }

        // Иначе запрашиваем историю с сервера с запасом (учитывая ночные паузы и выходные)
        var tcs = new TaskCompletionSource<List<BaseTicksCandle>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _candlesTcs = tcs;

        var tf = new CandleTimeframe($"{tfMinutes}m", TimeSpan.FromMinutes(tfMinutes));
        DateTime end = DateTime.UtcNow.AddHours(3); // МСК
        DateTime start = end.AddMinutes(-tfMinutes * limit * 4 - 4320); // +3 дня запаса на выходные

        bool requested = Layer.RequestCandles(this, tf, start, end);
        if (!requested)
            throw new Exception("Сервер отклонил запрос свечей (RequestCandles вернул false).");

        // Ждем ответ от сервера максимум 10 секунд
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        if (completedTask != tcs.Task)
            throw new TimeoutException("Таймаут ожидания свечей от сервера.");

        var rawCandles = await tcs.Task;
        var dtos = rawCandles
            .Select(c => new CandleDto(c.StartTime, c.Open, c.High, c.Low, c.Close, c.Volume))
            .OrderBy(c => c.Time)
            .ToList();

        lock (_candleLock)
        {
            _cachedCandles[tfMinutes] = dtos;
            return dtos.TakeLast(limit).ToList();
        }
    }

    // --- Запрос кластеров ---
    public async Task<List<TickGraphicData>> GetClustersAsync(int tfMinutes, int limit)
    {
        var tcs = new TaskCompletionSource<List<TickGraphicData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _clustersTcs = tcs;

        var tf = new CandleTimeframe($"{tfMinutes}m", TimeSpan.FromMinutes(tfMinutes));
        DateTime end = DateTime.UtcNow.AddHours(3);
        DateTime start = end.AddMinutes(-tfMinutes * limit * 4 - 4320);

        if (!Layer.RequestClusters(this, tf, start, end))
            throw new Exception("Сервер отклонил запрос кластеров.");

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        if (completedTask != tcs.Task)
            throw new TimeoutException("Таймаут ожидания кластеров от сервера.");

        var raw = await tcs.Task;
        return raw.TakeLast(limit).ToList();
    }

    public List<TickDto> GetTicks(int limit) => _ticks.TakeLast(limit).ToList();

    // --- Обработка входящих тиков и достройка свечей ---
    private void ProcessIncomingTick(TickGraphicData tick)
    {
        if (tick.IsEmpty || tick.Price <= 0) return;

        var dto = new TickDto(tick.UpdateDateTime, tick.Price, tick.Amount, tick.Direction.ToString());
        _ticks.Enqueue(dto);
        while (_ticks.Count > MaxTicksBuffer)
            _ticks.TryDequeue(out _);

        // Обновляем последнюю свечу во всех активных кэшах таймфреймов
        lock (_candleLock)
        {
            foreach (var (tfMin, candles) in _cachedCandles)
            {
                if (candles.Count == 0) continue;

                // Выравниваем время тика по границе таймфрейма
                long intervalTicks = TimeSpan.FromMinutes(tfMin).Ticks;
                DateTime barStart = new DateTime((tick.UpdateDateTime.Ticks / intervalTicks) * intervalTicks, tick.UpdateDateTime.Kind);

                var last = candles[^1];
                if (barStart == last.Time)
                {
                    candles[^1] = last with
                    {
                        High = Math.Max(last.High, tick.Price),
                        Low = Math.Min(last.Low, tick.Price),
                        Close = tick.Price,
                        Volume = last.Volume + tick.Amount
                    };
                }
                else if (barStart > last.Time)
                {
                    candles.Add(new CandleDto(barStart, tick.Price, tick.Price, tick.Price, tick.Price, tick.Amount));
                }
            }
        }
    }

    // --- Реализация колбэков ILayerEventsReceiver ---
    public void OnTick(ILayer sender, TickGraphicData tick) => ProcessIncomingTick(tick);
    public void OnTick(ILayer sender, List<TickGraphicData> ticks)
    {
        foreach (var t in ticks) ProcessIncomingTick(t);
    }

    public void OnCandlesReceived(ILayer sender, CandleTimeframe timeframe, List<BaseTicksCandle> candles) =>
        _candlesTcs?.TrySetResult(candles);

    public void OnClustersReceived(ILayer sender, CandleTimeframe timeframe, List<TickGraphicData> clusters) =>
        _clustersTcs?.TrySetResult(clusters);

    public void OnLimitOrderOpFailed(ILayer sender, int code, string msg) =>
        Console.WriteLine($"[{SlotKey} | {Ticker}] Ошибка заявки ({code}): {msg}");

    public void OnStopOrderOpFailed(ILayer sender, int code, string msg) =>
        Console.WriteLine($"[{SlotKey} | {Ticker}] Ошибка стоп-заявки ({code}): {msg}");

    public Task OnLayerDestroyedAsync(ILayer sender, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[{SlotKey} | {Ticker}] Слой уничтожен (разрыв связи).");
        return Task.CompletedTask;
    }

    // Остальные обязательные методы интерфейса
    public void OnReplicationBegin(ILayer sender) { }
    public void OnReplicationEnd(ILayer sender) => Console.WriteLine($"[{SlotKey} | {Ticker}] Репликация завершена.");
    public void OnTrade(ILayer sender, TradeData data) { }
    public void OnClosingTrade(ILayer sender, ClosingTradeData data) { }
    public void OnTradesPosition(ILayer sender, ITradesPositionData data) { }
    public void OnPositionUpdate(ILayer sender, decimal amount, double price) { }
    public void OnLimitOrdersChanged(ILayer sender, ReadOnlyCollection<LimitOrder> list) { }
    public void OnLimitOrderPingReceived(ILayer sender, long ms) { }
    public void OnStopOrdersChanged(ILayer sender, ReadOnlyCollection<StopOrder> list) { }
    public void OnStopOrderPingReceived(ILayer sender, long ms) { }
    public void OnStockUpdate(ILayer sender, ContractOrderStock stock) { }
    public void OnTick_DarkPool(ILayer sender, TickGraphicData tick) { }
    public void OnWatchlistUpdate(ILayer sender, IWatchlist data) { }
    public Task OnContractExpiredAsync(ILayer sender, IContractInfo current, IContractInfo next) => Task.CompletedTask;
}
