using System.Globalization;
using System.Text.Json;

namespace Limits;

internal sealed record RatesSnapshot(
    decimal? UsdBynToday,
    string? UsdBynTodayDate,
    decimal? UsdBynTomorrow,
    string? UsdBynTomorrowDate,
    decimal? EthUsd,
    string? EthSource,
    DateTimeOffset NbrbFetchedAt,
    DateTimeOffset EthFetchedAt,
    DateTimeOffset Timestamp);

internal sealed record RatesReadResult(RatesSnapshot? Snapshot, string? ErrorMessage);

internal sealed class RatesReader
{
    private static readonly TimeSpan NbrbCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan EthCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private const int NbrbUsdCode = 431;

    private readonly object _cacheLock = new();
    private decimal? _usdToday;
    private string? _usdTodayDate;
    private decimal? _usdTomorrow;
    private string? _usdTomorrowDate;
    private DateTimeOffset _nbrbFetchedAt = DateTimeOffset.MinValue;
    private decimal? _ethUsd;
    private string? _ethSource;
    private DateTimeOffset _ethFetchedAt = DateTimeOffset.MinValue;

    public RatesReadResult ReadLatestSnapshot()
    {
        List<string> errors = [];
        bool nbrbStale;
        bool ethStale;
        lock (_cacheLock)
        {
            nbrbStale = DateTimeOffset.UtcNow - _nbrbFetchedAt >= NbrbCacheTtl;
            ethStale = DateTimeOffset.UtcNow - _ethFetchedAt >= EthCacheTtl;
        }

        if (nbrbStale)
        {
            try
            {
                (decimal today, string todayDate, decimal? tomorrow, string? tomorrowDate) = ReadNbrbUsd();
                lock (_cacheLock)
                {
                    _usdToday = today;
                    _usdTodayDate = todayDate;
                    _usdTomorrow = tomorrow;
                    _usdTomorrowDate = tomorrowDate;
                    _nbrbFetchedAt = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception exception) when (IsRatesFailure(exception))
            {
                errors.Add(GetSafeErrorMessage(exception));
            }
        }

        if (ethStale)
        {
            try
            {
                (decimal price, string source) = ReadEthUsd();
                lock (_cacheLock)
                {
                    _ethUsd = price;
                    _ethSource = source;
                    _ethFetchedAt = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception exception) when (IsRatesFailure(exception))
            {
                errors.Add(GetSafeErrorMessage(exception));
            }
        }

        lock (_cacheLock)
        {
            if (_usdToday is null && _ethUsd is null)
            {
                return new RatesReadResult(null, errors.Count > 0 ? string.Join(" ", errors) : "Rates are unavailable.");
            }

            return new RatesReadResult(
                new RatesSnapshot(
                    _usdToday,
                    _usdTodayDate,
                    _usdTomorrow,
                    _usdTomorrowDate,
                    _ethUsd,
                    _ethSource,
                    _nbrbFetchedAt,
                    _ethFetchedAt,
                    DateTimeOffset.UtcNow),
                errors.Count > 0 ? string.Join(" ", errors) : null);
        }
    }

    private static DateOnly GetMinskDate()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            TimeZoneInfo minsk = TimeZoneInfo.FindSystemTimeZoneById("Belarus Standard Time");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, minsk).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(now.UtcDateTime.AddHours(3));
        }
        catch (InvalidTimeZoneException)
        {
            return DateOnly.FromDateTime(now.UtcDateTime.AddHours(3));
        }
    }

    private static (decimal today, string todayDate, decimal? tomorrow, string? tomorrowDate) ReadNbrbUsd()
    {
        DateOnly minskToday = GetMinskDate();
        DateOnly start = minskToday.AddDays(-3);
        DateOnly end = minskToday.AddDays(1);
        string url = $"https://api.nbrb.by/exrates/rates/dynamics/{NbrbUsdCode}?startdate={start:yyyy-MM-dd}&enddate={end:yyyy-MM-dd}";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("limits/1.3");
        using HttpResponseMessage response = HttpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"NBRB dynamics returned HTTP {(int)response.StatusCode}.");
        }

        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("NBRB dynamics response was not an array.");
        }

        List<(DateOnly date, decimal rate)> entries = [];
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("Date", out JsonElement dateElement) ||
                !element.TryGetProperty("Cur_OfficialRate", out JsonElement rateElement))
            {
                continue;
            }

            if (!DateTime.TryParse(
                    dateElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTime parsedDate))
            {
                continue;
            }

            decimal rate;
            try
            {
                rate = rateElement.GetDecimal();
            }
            catch (FormatException)
            {
                continue;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            entries.Add((DateOnly.FromDateTime(parsedDate), rate));
        }

        entries.Sort(static (left, right) => left.date.CompareTo(right.date));

        DateOnly tomorrowDate = minskToday.AddDays(1);
        (DateOnly date, decimal rate)? todayEntry = null;
        (DateOnly date, decimal rate)? tomorrowEntry = null;
        foreach ((DateOnly date, decimal rate) entry in entries)
        {
            if (entry.date <= minskToday)
            {
                todayEntry = entry;
            }

            if (entry.date == tomorrowDate)
            {
                tomorrowEntry = entry;
            }
        }

        if (todayEntry is null)
        {
            throw new InvalidOperationException("NBRB dynamics contained no USD rate for today.");
        }

        return (
            todayEntry.Value.rate,
            todayEntry.Value.date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            tomorrowEntry.HasValue ? tomorrowEntry.Value.rate : null,
            tomorrowEntry.HasValue ? tomorrowEntry.Value.date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null);
    }

    private static (decimal price, string source) ReadEthUsd()
    {
        try
        {
            return (ReadKrakenEth(), "kraken");
        }
        catch (Exception exception) when (IsRatesFailure(exception))
        {
            return (ReadCoinbaseEth(), "coinbase");
        }
    }

    private static decimal ReadKrakenEth()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "https://api.kraken.com/0/public/Ticker?pair=ETHUSD");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("limits/1.3");
        using HttpResponseMessage response = HttpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Kraken ticker returned HTTP {(int)response.StatusCode}.");
        }

        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("error", out JsonElement errorElement) &&
            errorElement.ValueKind == JsonValueKind.Array &&
            errorElement.GetArrayLength() > 0)
        {
            throw new InvalidOperationException("Kraken ticker returned an error.");
        }

        if (!root.TryGetProperty("result", out JsonElement result) ||
            !result.TryGetProperty("XETHZUSD", out JsonElement pair) ||
            !pair.TryGetProperty("c", out JsonElement close) ||
            close.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Kraken ticker response was unexpected.");
        }

        foreach (JsonElement item in close.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                decimal.TryParse(item.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal price))
            {
                return price;
            }

            if (item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out decimal numericPrice))
            {
                return numericPrice;
            }
        }

        throw new InvalidOperationException("Kraken ticker contained no ETH price.");
    }

    private static decimal ReadCoinbaseEth()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "https://api.coinbase.com/v2/prices/ETH-USD/spot");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("limits/1.3");
        using HttpResponseMessage response = HttpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Coinbase spot returned HTTP {(int)response.StatusCode}.");
        }

        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) ||
            !data.TryGetProperty("amount", out JsonElement amount) ||
            amount.ValueKind != JsonValueKind.String ||
            !decimal.TryParse(amount.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal price))
        {
            throw new JsonException("Coinbase spot response was unexpected.");
        }

        return price;
    }

    private static bool IsRatesFailure(Exception exception)
    {
        return exception is
            HttpRequestException or
            TaskCanceledException or
            IOException or
            JsonException or
            InvalidOperationException or
            FormatException;
    }

    private static string GetSafeErrorMessage(Exception exception)
    {
        return exception switch
        {
            TaskCanceledException => "Rates request timed out.",
            HttpRequestException => "Rates HTTP request failed.",
            _ => "Rates response parsing failed."
        };
    }
}
