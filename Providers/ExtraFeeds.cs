using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Limits;

internal sealed partial class TrayApplication
{
    private readonly ConcurrentQueue<Action> _extraFeedResults = new();
    private readonly UnetBalanceReader _unetBalanceReader = new();
    private readonly RatesReader _ratesReader = new();
    private readonly GcalReader _gcalReader = new();
    private volatile bool _unetRefreshInFlight;
    private volatile bool _ratesRefreshInFlight;
    private volatile bool _gcalRefreshInFlight;
    private UnetBalanceSnapshot? _lastUnetSnapshot;
    private RatesSnapshot? _lastRatesSnapshot;
    private GcalSnapshot? _lastGcalSnapshot;

    partial void RefreshExtraFeeds()
    {
        RefreshUnetFeed();
        RefreshRatesFeed();
        RefreshGcalFeed();
    }

    partial void ApplyExtraFeedResults()
    {
        while (_extraFeedResults.TryDequeue(out Action? apply))
        {
            apply();
        }
    }

    partial void PublishExtraCounters(JsonObject root)
    {
        root["unet"] = BuildUnetCounterJson(_lastUnetSnapshot);
        root["rates"] = BuildRatesCounterJson(_lastRatesSnapshot);
        root["gcal"] = BuildGcalCounterJson(_lastGcalSnapshot);
    }

    private void RefreshUnetFeed()
    {
        if (_unetRefreshInFlight)
        {
            return;
        }

        _unetRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        Task.Run(() =>
        {
            UnetBalanceReadResult result;
            try
            {
                result = _unetBalanceReader.ReadLatestSnapshot();
            }
            catch (Exception)
            {
                result = new UnetBalanceReadResult(null, "UNET balance refresh failed.");
            }

            _extraFeedResults.Enqueue(() =>
            {
                _lastUnetSnapshot = result.Snapshot;
                _unetRefreshInFlight = false;
            });

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, ExtraFeedResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _unetRefreshInFlight = false;
            }
        });
    }

    private void RefreshRatesFeed()
    {
        if (_ratesRefreshInFlight)
        {
            return;
        }

        _ratesRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        Task.Run(() =>
        {
            RatesReadResult result;
            try
            {
                result = _ratesReader.ReadLatestSnapshot();
            }
            catch (Exception)
            {
                result = new RatesReadResult(null, "Rates refresh failed.");
            }

            _extraFeedResults.Enqueue(() =>
            {
                _lastRatesSnapshot = result.Snapshot;
                _ratesRefreshInFlight = false;
            });

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, ExtraFeedResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _ratesRefreshInFlight = false;
            }
        });
    }

    private void RefreshGcalFeed()
    {
        if (_gcalRefreshInFlight)
        {
            return;
        }

        _gcalRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        Task.Run(() =>
        {
            GcalReadResult result;
            try
            {
                result = _gcalReader.ReadLatestSnapshot();
            }
            catch (Exception)
            {
                result = new GcalReadResult(null, "Calendar refresh failed.");
            }

            _extraFeedResults.Enqueue(() =>
            {
                _lastGcalSnapshot = result.Snapshot;
                _gcalRefreshInFlight = false;
            });

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, ExtraFeedResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _gcalRefreshInFlight = false;
            }
        });
    }

    private static void SetNullableDecimal(JsonObject target, string propertyName, decimal? value)
    {
        target[propertyName] = value is { } number
            ? JsonValue.Create(number)
            : null;
    }

    private static JsonObject? BuildUnetCounterJson(UnetBalanceSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        JsonArray accounts = new();
        foreach (UnetAccountBalance account in snapshot.Accounts)
        {
            JsonObject accountJson = new()
            {
                ["username"] = account.Username,
                ["currency"] = account.Currency,
                ["isAvailable"] = account.IsAvailable,
                ["error"] = account.Error
            };
            SetNullableDecimal(accountJson, "balance", account.Balance);
            accounts.Add((JsonNode)accountJson);
        }

        return new JsonObject
        {
            ["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp),
            ["isAvailable"] = snapshot.Accounts.Any(account => account.IsAvailable),
            ["accounts"] = accounts
        };
    }

    private static JsonObject? BuildRatesCounterJson(RatesSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        JsonObject result = new()
        {
            ["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp),
            ["usdBynTodayDate"] = snapshot.UsdBynTodayDate,
            ["usdBynTomorrowDate"] = snapshot.UsdBynTomorrowDate,
            ["ethSource"] = snapshot.EthSource,
            ["nbrbFetchedAt"] = FormatCounterTimestamp(snapshot.NbrbFetchedAt),
            ["ethFetchedAt"] = FormatCounterTimestamp(snapshot.EthFetchedAt)
        };
        SetNullableDecimal(result, "usdBynToday", snapshot.UsdBynToday);
        SetNullableDecimal(result, "usdBynTomorrow", snapshot.UsdBynTomorrow);
        SetNullableDecimal(result, "ethUsd", snapshot.EthUsd);
        return result;
    }

    private static JsonObject? BuildGcalCounterJson(GcalSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        JsonArray events = [];
        foreach (GcalEvent calendarEvent in snapshot.Events)
        {
            events.Add((JsonNode)new JsonObject
            {
                ["title"] = calendarEvent.Title,
                ["start"] = calendarEvent.Start,
                ["end"] = calendarEvent.End,
                ["allDay"] = calendarEvent.AllDay
            });
        }

        return new JsonObject
        {
            ["timestamp"] = FormatCounterTimestamp(snapshot.Timestamp),
            ["configured"] = snapshot.Configured,
            ["date"] = snapshot.Date,
            ["events"] = events,
            ["fetchedAt"] = FormatCounterTimestamp(snapshot.FetchedAt),
            ["error"] = snapshot.Error
        };
    }
}

