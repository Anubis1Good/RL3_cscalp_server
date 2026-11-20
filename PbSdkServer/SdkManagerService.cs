using ICore;
using PbSdk;
using PbSdkConsole;
using System.Collections.Concurrent;

public class AccountConfig
{
    public string Alias { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public Dictionary<string, string> Servers { get; set; } = new();
}

public class SdkManagerService
{
    private IPbSdkManager? _sdk;
    public ConcurrentDictionary<string, IDispatcherSlot> Slots { get; } = new();
    private readonly List<DispatcherSlotEventsReceiver> _receivers = new();
    private CancellationTokenSource? _reconnectCts;

    public event Func<string, Task>? OnSlotReady;

    public async Task InitializeAndConnectAsync(List<AccountConfig> accounts)
    {
        Console.WriteLine("Creating SDK...");
        _sdk = PbSdkFactory.Create();

        if (!await _sdk.AuthAsync())
            throw new Exception("SDK Authorization failed");

        _sdk.Initialize();
        await _sdk.StartAsync();
        Console.WriteLine("SDK Started.");

        IDispatcher dispatcher = _sdk.PrivodBondarDispatcher;
        int slotIndex = 0;

        foreach (var acc in accounts)
        {
            if (acc.Servers == null || acc.Servers.Count == 0) continue;

            foreach (var (marketKey, stockLongName) in acc.Servers)
            {
                string slotKey = $"{acc.Alias}:{marketKey}";
                IDispatcherSlot? slot = null;

                if (slotIndex < dispatcher.Slots.Count)
                    slot = dispatcher.GetSlot(slotIndex);
                else
                    dispatcher.TryAddSlot(DispatcherSlotType.XDealingCommon, out slot, out _);
                
                slotIndex++;

                if (slot != null)
                {
                    var receiver = new DispatcherSlotEventsReceiver(slot);
                    receiver.Log += msg =>
                    {
                        // Не засоряем консоль ежесекундными обновлениями баланса
                        if (msg.Contains("OnFinDataUpdate") || msg.Contains("Currency:"))
                            return;

                        Console.WriteLine($"[{slotKey}] {msg}");
                    };

                    receiver.CanCreateLayers += sender =>
                    {
                        Console.WriteLine($"[{slotKey}] Слот готов / переподключен.");
                        _ = Task.Run(async () =>
                        {
                            if (OnSlotReady != null)
                                await OnSlotReady.Invoke(slotKey);
                        });
                    };
                    _receivers.Add(receiver);

                    var serverInfo = slot.PublicServersInfo.FirstOrDefault(s => s.StockLongName == stockLongName);
                    if (serverInfo != null)
                    {
                        slot.PublicServerInfo = serverInfo;
                        slot.Account = acc.Account;
                        slot.Password = acc.Password;

                        Slots[slotKey] = slot;
                        await slot.ConnectAsync(CancellationToken.None);
                    }
                    else
                    {
                        Console.WriteLine($"[{slotKey}] ОШИБКА: Сервер '{stockLongName}' не найден.");
                    }
                }
            }
        }

        // Запускаем фоновый сторож для автоматического переподключения при обрывах связи
        _reconnectCts = new CancellationTokenSource();
        _ = Task.Run(() => ReconnectLoopAsync(_reconnectCts.Token));
    }
    // Добавляем поле в класс SdkManagerService:
    private readonly ConcurrentDictionary<string, bool> _isConnecting = new();

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Проверяем раз в 10 секунд, чтобы давать серверу спокойно завершить рукопожатие
            await Task.Delay(10000, ct);

            foreach (var (slotKey, slot) in Slots)
            {
                // Если слот подключен ИЛИ прямо сейчас уже идет попытка подключения — пропускаем
                if (slot.IsConnected || _isConnecting.GetValueOrDefault(slotKey, false))
                    continue;

                _isConnecting[slotKey] = true;
                Console.WriteLine($"[{slotKey}] Обнаружен разрыв связи. Попытка переподключения...");

                try
                {
                    await slot.ConnectAsync(ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{slotKey}] Ошибка при переподключении: {ex.Message}");
                }
                finally
                {
                    _isConnecting[slotKey] = false;
                }
            }
        }
    }


    public IDispatcherSlot? GetSlot(string slotKey) =>
        Slots.TryGetValue(slotKey, out var slot) ? slot : null;

    public async Task StopAsync()
    {
        _reconnectCts?.Cancel();
        if (_sdk != null)
            await _sdk.StopAsync();
    }
}
