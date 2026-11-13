using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Limits;

internal sealed record GcalEvent(string Title, string? Start, string? End, bool AllDay);

internal sealed record GcalSnapshot(
    bool Configured,
    string Date,
    IReadOnlyList<GcalEvent> Events,
    DateTimeOffset FetchedAt,
    DateTimeOffset Timestamp,
    string? Error);

internal sealed record GcalReadResult(GcalSnapshot? Snapshot, string? ErrorMessage);

internal sealed class GcalReader
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly object _cacheLock = new();
    private string? _lastUrl;
    private string? _lastBody;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private string? _fetchError;

    public string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "limits",
        "google-ical.json");

    public GcalReadResult ReadLatestSnapshot()
    {
        string? url = ReadConfiguredUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            return new GcalReadResult(
                new GcalSnapshot(false, GcalParser.MinskTodayString(), [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null),
                null);
        }

        lock (_cacheLock)
        {
            if (!string.Equals(_lastUrl, url, StringComparison.Ordinal) ||
                _lastBody is null ||
                DateTimeOffset.UtcNow - _fetchedAt >= CacheTtl)
            {
                try
                {
                    _lastBody = Fetch(url);
                    _lastUrl = url;
                    _fetchedAt = DateTimeOffset.UtcNow;
                    _fetchError = null;
                }
                catch (Exception exception) when (IsGcalFailure(exception))
                {
                    _fetchError = GetSafeErrorMessage(exception);
                    if (_lastBody is null || !string.Equals(_lastUrl, url, StringComparison.Ordinal))
                    {
                        return new GcalReadResult(
                            new GcalSnapshot(true, GcalParser.MinskTodayString(), [], DateTimeOffset.MinValue, DateTimeOffset.UtcNow, _fetchError),
                            _fetchError);
                    }
                }
            }

            try
            {
                (string date, List<GcalEvent> events) = GcalParser.ExtractToday(_lastBody!);
                return new GcalReadResult(
                    new GcalSnapshot(true, date, events, _fetchedAt, DateTimeOffset.UtcNow, _fetchError),
                    _fetchError);
            }
            catch (Exception exception) when (IsGcalFailure(exception))
            {
                string error = GetSafeErrorMessage(exception);
                return new GcalReadResult(
                    new GcalSnapshot(true, GcalParser.MinskTodayString(), [], _fetchedAt, DateTimeOffset.UtcNow, error),
                    error);
            }
        }
    }

    private string? ReadConfiguredUrl()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("icalUrl", out JsonElement urlElement) ||
                urlElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? value = urlElement.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string Fetch(string url)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/calendar");
        request.Headers.UserAgent.ParseAdd("limits/1.3");
        using HttpResponseMessage response = HttpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Calendar feed returned HTTP {(int)response.StatusCode}.");
        }

        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static bool IsGcalFailure(Exception exception)
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
            TaskCanceledException => "Calendar request timed out.",
            HttpRequestException => "Calendar HTTP request failed.",
            _ => "Calendar feed parsing failed."
        };
    }
}

internal static class GcalParser
{
    private static readonly TimeSpan MinskOffset = TimeSpan.FromHours(3);
    private static readonly StringComparer OrdinalIgnore = StringComparer.OrdinalIgnoreCase;

    public static string MinskTodayString() => MinskToday().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly MinskToday() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime.Add(MinskOffset));

    public static (string date, List<GcalEvent> events) ExtractToday(string ics)
    {
        DateOnly today = MinskToday();
        List<Dictionary<string, List<(string parameters, string value)>>> vevents = ParseVevents(ics);
        List<(GcalEvent ev, DateTime sortKey, bool allDayFirst)> found = [];

        foreach (Dictionary<string, List<(string parameters, string value)>> props in vevents)
        {
            try
            {
                foreach ((GcalEvent ev, DateTime sortKey, bool allDayFirst) occurrence in ExpandEvent(props, today))
                {
                    found.Add(occurrence);
                }
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentOutOfRangeException)
            {
                continue;
            }
        }

        found.Sort(static (left, right) =>
        {
            int order = right.allDayFirst.CompareTo(left.allDayFirst);
            if (order != 0)
            {
                return order;
            }

            order = left.sortKey.CompareTo(right.sortKey);
            return order != 0 ? order : string.Compare(left.ev.Title, right.ev.Title, StringComparison.OrdinalIgnoreCase);
        });

        return (today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), found.Select(item => item.ev).ToList());
    }

    private static List<Dictionary<string, List<(string parameters, string value)>>> ParseVevents(string ics)
    {
        List<string> lines = [];
        StringBuilder? current = null;
        foreach (string raw in ics.Split(['\r', '\n']))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            if (raw[0] == ' ' || raw[0] == '\t')
            {
                current?.Append(raw.Substring(1));
                continue;
            }

            if (current is not null)
            {
                lines.Add(current.ToString());
            }

            current = new StringBuilder(raw);
        }

        if (current is not null)
        {
            lines.Add(current.ToString());
        }

        List<Dictionary<string, List<(string parameters, string value)>>> events = [];
        Dictionary<string, List<(string parameters, string value)>>? ev = null;
        foreach (string line in lines)
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            string head = line.Substring(0, colon);
            string value = line.Substring(colon + 1);
            string[] headParts = head.Split(';');
            string name = headParts[0].Trim().ToUpperInvariant();
            string parameters = headParts.Length > 1 ? string.Join(";", headParts.Skip(1)) : string.Empty;

            if (name == "BEGIN" && value.Trim().Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                ev = new Dictionary<string, List<(string parameters, string value)>>(OrdinalIgnore);
            }
            else if (name == "END" && value.Trim().Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (ev is not null)
                {
                    events.Add(ev);
                }

                ev = null;
            }
            else if (ev is not null)
            {
                if (!ev.TryGetValue(name, out List<(string parameters, string value)>? list))
                {
                    list = [];
                    ev[name] = list;
                }

                list.Add((parameters, value));
            }
        }

        return events;
    }

    private static List<(GcalEvent ev, DateTime sortKey, bool allDayFirst)> ExpandEvent(
        Dictionary<string, List<(string parameters, string value)>> props,
        DateOnly today)
    {
        List<(GcalEvent ev, DateTime sortKey, bool allDayFirst)> result = [];
        if (IsCancelled(props))
        {
            return result;
        }

        string title = Unescape(GetFirst(props, "SUMMARY") ?? "(no title)");
        (bool isDate, DateTime start) startMoment = ParseMoment(
            GetFirst(props, "DTSTART") ?? throw new InvalidOperationException("VEVENT has no DTSTART."),
            GetFirstParameters(props, "DTSTART"));
        DateTime endMoment = startMoment.start;
        string? dtend = GetFirst(props, "DTEND");
        if (dtend is not null)
        {
            endMoment = ParseMoment(dtend, GetFirstParameters(props, "DTEND")).wall;
        }
        else
        {
            string? duration = GetFirst(props, "DURATION");
            if (duration is not null)
            {
                endMoment = startMoment.start.Add(ParseDuration(duration));
            }
        }

        HashSet<string> exdates = ReadExdates(props, startMoment.isDate);
        string? rrule = GetFirst(props, "RRULE");
        Dictionary<string, string>? rule = rrule is null ? null : ParseRule(rrule);
        bool supportedRule = rule is not null && IsSupportedRule(rule);
        List<(DateTime start, DateTime end)> candidates = [];

        if (rule is null || !supportedRule)
        {
            candidates.Add((startMoment.start, endMoment));
        }
        else
        {
            candidates.AddRange(ExpandRecurrence(startMoment.start, endMoment, startMoment.isDate, rule, today));
        }

        foreach ((string parameters, string value) rdate in GetAll(props, "RDATE"))
        {
            try
            {
                (bool _, DateTime wall) moment = ParseMoment(rdate.value, rdate.parameters);
                candidates.Add((moment.wall, moment.wall + (endMoment - startMoment.start)));
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentOutOfRangeException)
            {
                continue;
            }
        }

        foreach ((DateTime start, DateTime end) candidate in candidates)
        {
            if (!OverlapsToday(candidate.start, candidate.end, startMoment.isDate, today))
            {
                continue;
            }

            if (exdates.Contains(ExdateKey(candidate.start, startMoment.isDate)))
            {
                continue;
            }

            result.Add((FormatEvent(title, candidate.start, candidate.end, startMoment.isDate), candidate.start, startMoment.isDate));
        }

        return result;
    }

    private static bool IsCancelled(Dictionary<string, List<(string parameters, string value)>> props)
    {
        string? status = GetFirst(props, "STATUS");
        return status is not null && status.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetFirst(Dictionary<string, List<(string parameters, string value)>> props, string name)
    {
        return props.TryGetValue(name, out List<(string parameters, string value)>? list) && list.Count > 0
            ? list[0].value
            : null;
    }

    private static string GetFirstParameters(Dictionary<string, List<(string parameters, string value)>> props, string name)
    {
        return props.TryGetValue(name, out List<(string parameters, string value)>? list) && list.Count > 0
            ? list[0].parameters
            : string.Empty;
    }

    private static List<(string parameters, string value)> GetAll(Dictionary<string, List<(string parameters, string value)>> props, string name)
    {
        return props.TryGetValue(name, out List<(string parameters, string value)>? list) ? list : [];
    }

    private static string Unescape(string value)
    {
        return value.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
    }

    private static string? GetParam(string parameters, string key)
    {
        foreach (string part in parameters.Split(';'))
        {
            int equals = part.IndexOf('=');
            if (equals > 0 && part.Substring(0, equals).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return part.Substring(equals + 1).Trim().Trim('"');
            }
        }

        return null;
    }

    private static (bool isDate, DateTime wall) ParseMoment(string value, string parameters)
    {
        string text = value.Trim();
        bool valueIsDate = (GetParam(parameters, "VALUE") ?? string.Empty).Equals("DATE", StringComparison.OrdinalIgnoreCase);
        if (valueIsDate || (text.Length == 8 && !text.Contains('T')))
        {
            DateOnly date = DateOnly.ParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture);
            return (true, date.ToDateTime(TimeOnly.MinValue));
        }

        bool utc = text.EndsWith('Z') || text.EndsWith('z');
        if (utc)
        {
            text = text.Substring(0, text.Length - 1);
        }

        DateTime wall = DateTime.ParseExact(text, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None);
        if (utc)
        {
            return (false, wall.Add(MinskOffset));
        }

        string? tzid = GetParam(parameters, "TZID");
        return (false, tzid is null ? wall : ConvertToMinsk(wall, tzid));
    }

    private static DateTime ConvertToMinsk(DateTime wall, string tzid)
    {
        if (tzid.Equals("UTC", StringComparison.OrdinalIgnoreCase) ||
            tzid.Equals("GMT", StringComparison.OrdinalIgnoreCase) ||
            tzid.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            return wall.Add(MinskOffset);
        }

        try
        {
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(tzid);
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone).Add(MinskOffset);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return wall;
        }
    }

    private static TimeSpan ParseDuration(string value)
    {
        string text = value.Trim();
        bool negative = text.StartsWith('-');
        text = text.TrimStart('-', '+');
        if (!text.StartsWith('P'))
        {
            throw new FormatException("Unsupported DURATION format.");
        }

        text = text.Substring(1);
        int days = 0;
        int hours = 0;
        int minutes = 0;
        int seconds = 0;
        int tIndex = text.IndexOf('T');
        string datePart = tIndex < 0 ? text : text.Substring(0, tIndex);
        string timePart = tIndex < 0 ? string.Empty : text.Substring(tIndex + 1);

        int dIndex = datePart.IndexOf('D');
        if (dIndex >= 0)
        {
            days = int.Parse(datePart.Substring(0, dIndex), CultureInfo.InvariantCulture);
        }

        foreach ((char designator, Action<int> assign) in new (char, Action<int>)[]
                 {
                     ('H', v => hours = v),
                     ('M', v => minutes = v),
                     ('S', v => seconds = v)
                 })
        {
            int index = timePart.IndexOf(designator);
            if (index < 0)
            {
                continue;
            }

            int start = index;
            while (start > 0 && char.IsDigit(timePart[start - 1]))
            {
                start -= 1;
            }

            assign(int.Parse(timePart.Substring(start, index - start), CultureInfo.InvariantCulture));
            timePart = timePart.Substring(0, start) + timePart.Substring(index + 1);
        }

        TimeSpan span = new(days, hours, minutes, seconds);
        return negative ? -span : span;
    }

    private static Dictionary<string, string> ParseRule(string rrule)
    {
        Dictionary<string, string> rule = new(OrdinalIgnore);
        foreach (string part in rrule.Split(';'))
        {
            int equals = part.IndexOf('=');
            if (equals > 0)
            {
                rule[part.Substring(0, equals).Trim().ToUpperInvariant()] = part.Substring(equals + 1).Trim();
            }
        }

        return rule;
    }

    private static bool IsSupportedRule(Dictionary<string, string> rule)
    {
        if (!rule.TryGetValue("FREQ", out string? freq))
        {
            return false;
        }

        return freq.ToUpperInvariant() switch
        {
            "DAILY" => true,
            "WEEKLY" => true,
            "MONTHLY" => !rule.ContainsKey("BYDAY"),
            "YEARLY" => !rule.ContainsKey("BYDAY") && !rule.ContainsKey("BYMONTH") &&
                        !rule.ContainsKey("BYMONTHDAY") && !rule.ContainsKey("BYYEARDAY") &&
                        !rule.ContainsKey("BYWEEKNO"),
            _ => false
        };
    }

    private static int GetInt(Dictionary<string, string> rule, string key, int fallback)
    {
        return rule.TryGetValue(key, out string? raw) &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static int? GetCount(Dictionary<string, string> rule)
    {
        return rule.TryGetValue("COUNT", out string? raw) &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static DateTime? GetUntil(Dictionary<string, string> rule)
    {
        if (!rule.TryGetValue("UNTIL", out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            (bool isDate, DateTime wall) moment = ParseMoment(raw, string.Empty);
            return moment.isDate ? moment.wall.AddDays(1).AddTicks(-1) : moment.wall;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static List<DayOfWeek> ParseByDay(Dictionary<string, string> rule, DayOfWeek fallback)
    {
        List<DayOfWeek> days = [];
        if (!rule.TryGetValue("BYDAY", out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            return [fallback];
        }

        foreach (string token in raw.Split(','))
        {
            string day = new string(token.Trim().ToUpperInvariant().Where(char.IsLetter).ToArray());
            DayOfWeek? parsed = day switch
            {
                "MO" => DayOfWeek.Monday,
                "TU" => DayOfWeek.Tuesday,
                "WE" => DayOfWeek.Wednesday,
                "TH" => DayOfWeek.Thursday,
                "FR" => DayOfWeek.Friday,
                "SA" => DayOfWeek.Saturday,
                "SU" => DayOfWeek.Sunday,
                _ => null
            };

            if (parsed is not null && !days.Contains(parsed.Value))
            {
                days.Add(parsed.Value);
            }
        }

        if (days.Count == 0)
        {
            return [fallback];
        }

        days.Sort(static (left, right) => DayOrder(left).CompareTo(DayOrder(right)));
        return days;
    }

    private static int DayOrder(DayOfWeek day) => ((int)day - (int)DayOfWeek.Monday + 7) % 7;

    private static DateOnly MondayOf(DateOnly date) => date.AddDays(-DayOrder(date.DayOfWeek));

    private static HashSet<string> ReadExdates(Dictionary<string, List<(string parameters, string value)>> props, bool allDay)
    {
        HashSet<string> exdates = new(StringComparer.Ordinal);
        foreach ((string parameters, string value) exdate in GetAll(props, "EXDATE"))
        {
            foreach (string token in exdate.value.Split(','))
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                try
                {
                    (bool _, DateTime wall) moment = ParseMoment(token, exdate.parameters);
                    exdates.Add(ExdateKey(moment.wall, allDay));
                }
                catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentOutOfRangeException)
                {
                    continue;
                }
            }
        }

        return exdates;
    }

    private static string ExdateKey(DateTime wall, bool allDay)
    {
        return allDay
            ? wall.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : wall.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
    }

    private static List<(DateTime start, DateTime end)> ExpandRecurrence(
        DateTime startWall,
        DateTime endWall,
        bool allDay,
        Dictionary<string, string> rule,
        DateOnly today)
    {
        List<(DateTime start, DateTime end)> result = [];
        TimeSpan duration = endWall - startWall;
        string freq = rule["FREQ"].ToUpperInvariant();
        int interval = GetInt(rule, "INTERVAL", 1);
        int? count = GetCount(rule);
        DateTime? until = GetUntil(rule);
        int emitted = 0;
        int guard = 0;

        bool accept(DateTime candidate)
        {
            if (candidate < startWall)
            {
                return false;
            }

            if (until is not null && candidate > until.Value)
            {
                return false;
            }

            emitted += 1;
            return DateOnly.FromDateTime(candidate) == today;
        }

        if (freq == "DAILY")
        {
            DateTime cursor = startWall;
            while (guard++ < 5000)
            {
                if (DateOnly.FromDateTime(cursor) > today)
                {
                    break;
                }

                if (accept(cursor))
                {
                    result.Add((cursor, cursor + duration));
                }

                if (count is not null && emitted >= count)
                {
                    break;
                }

                cursor = cursor.AddDays(interval);
            }

            return result;
        }

        if (freq == "WEEKLY")
        {
            List<DayOfWeek> days = ParseByDay(rule, startWall.DayOfWeek);
            DateOnly anchorMonday = MondayOf(DateOnly.FromDateTime(startWall));
            int week = 0;
            while (guard++ < 2600)
            {
                DateOnly weekMonday = anchorMonday.AddDays(week * 7 * interval);
                if (weekMonday > today)
                {
                    break;
                }

                foreach (DayOfWeek day in days)
                {
                    DateTime candidate = weekMonday.AddDays(DayOrder(day)).ToDateTime(TimeOnly.FromTimeSpan(startWall.TimeOfDay));
                    if (candidate < startWall)
                    {
                        continue;
                    }

                    if (until is not null && candidate > until.Value)
                    {
                        return result;
                    }

                    if (accept(candidate))
                    {
                        result.Add((candidate, candidate + duration));
                    }

                    if (count is not null && emitted >= count)
                    {
                        return result;
                    }
                }

                week += 1;
            }

            return result;
        }

        if (freq == "MONTHLY")
        {
            List<int> monthDays = [];
            if (rule.TryGetValue("BYMONTHDAY", out string? rawDays))
            {
                foreach (string token in rawDays.Split(','))
                {
                    if (int.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int day))
                    {
                        monthDays.Add(day);
                    }
                }
            }

            if (monthDays.Count == 0)
            {
                monthDays.Add(startWall.Day);
            }

            int month = 0;
            while (guard++ < 1200)
            {
                DateTime anchor = startWall.AddMonths(month * interval);
                DateOnly anchorMonth = new(anchor.Year, anchor.Month, 1);
                if (anchorMonth > today)
                {
                    break;
                }

                int daysInMonth = DateTime.DaysInMonth(anchor.Year, anchor.Month);
                foreach (int monthDay in monthDays.Distinct().OrderBy(static day => day))
                {
                    int day = monthDay > 0 ? monthDay : daysInMonth + monthDay + 1;
                    if (day < 1 || day > daysInMonth)
                    {
                        continue;
                    }

                    DateTime candidate = new(anchor.Year, anchor.Month, day, startWall.Hour, startWall.Minute, startWall.Second);
                    if (candidate < startWall)
                    {
                        continue;
                    }

                    if (until is not null && candidate > until.Value)
                    {
                        return result;
                    }

                    if (accept(candidate))
                    {
                        result.Add((candidate, candidate + duration));
                    }

                    if (count is not null && emitted >= count)
                    {
                        return result;
                    }
                }

                month += 1;
            }

            return result;
        }

        if (freq == "YEARLY")
        {
            int year = 0;
            while (guard++ < 200)
            {
                DateTime candidate;
                try
                {
                    int daysInMonth = DateTime.DaysInMonth(startWall.Year + year * interval, startWall.Month);
                    candidate = new DateTime(
                        startWall.Year + year * interval,
                        startWall.Month,
                        Math.Min(startWall.Day, daysInMonth),
                        startWall.Hour,
                        startWall.Minute,
                        startWall.Second);
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;
                }

                if (DateOnly.FromDateTime(candidate) > today)
                {
                    break;
                }

                if (accept(candidate))
                {
                    result.Add((candidate, candidate + duration));
                }

                if (count is not null && emitted >= count)
                {
                    break;
                }

                year += 1;
            }

            return result;
        }

        return result;
    }

    private static bool OverlapsToday(DateTime start, DateTime end, bool allDay, DateOnly today)
    {
        if (allDay)
        {
            DateOnly startDate = DateOnly.FromDateTime(start);
            DateOnly endDate = DateOnly.FromDateTime(end);
            if (endDate <= startDate)
            {
                return startDate == today;
            }

            return startDate <= today && today < endDate;
        }

        return DateOnly.FromDateTime(start) <= today && today <= DateOnly.FromDateTime(end);
    }

    private static GcalEvent FormatEvent(string title, DateTime start, DateTime end, bool allDay)
    {
        if (allDay)
        {
            return new GcalEvent(title, null, null, true);
        }

        return new GcalEvent(
            title,
            start.ToString("HH:mm", CultureInfo.InvariantCulture),
            end > start ? end.ToString("HH:mm", CultureInfo.InvariantCulture) : null,
            false);
    }
}
