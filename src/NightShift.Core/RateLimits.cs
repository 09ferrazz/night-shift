using System.Text.Json;

namespace NightShift.Core;

public sealed record RateWindow(double UsedPercent, DateTimeOffset? ResetsAt, int DurationMinutes);

public sealed class RateSnapshot
{
    public bool Known { get; private init; }
    public bool ServerBlocked { get; private init; }
    public RateWindow? Primary { get; private init; }
    public RateWindow? Secondary { get; private init; }
    public bool IsBlocked => ServerBlocked || Windows.Any(w => w.UsedPercent >= 100);
    private IEnumerable<RateWindow> Windows => new[] { Primary, Secondary }.OfType<RateWindow>();

    public static RateSnapshot ParseClaude(JsonElement info)
    {
        if (info.ValueKind != JsonValueKind.Object) return new();
        var status = CodexSession.Text(info, "status");
        var blocked = status == "rejected" && CodexSession.Text(info, "errorCode") != "credits_required";
        DateTimeOffset? reset = null;
        if (info.TryGetProperty("resetsAt", out var at) && at.TryGetInt64(out var seconds))
            try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
        RateWindow? window = null;
        if (info.TryGetProperty("utilization", out var value) && value.TryGetDouble(out var fraction) && double.IsFinite(fraction))
            window = new(Math.Clamp(fraction * 100, 0, 100), reset, CodexSession.Text(info,"rateLimitType") == "seven_day" ? 10080 : 300);
        return new() { Known = status is "allowed" or "allowed_warning" or "rejected", ServerBlocked = blocked,
            Primary = window?.DurationMinutes == 10080 ? null : window, Secondary = window?.DurationMinutes == 10080 ? window : null };
    }

    public static RateSnapshot Parse(JsonElement payload)
    {
        JsonElement bucket = default;
        if (payload.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object
            && map.TryGetProperty("codex", out var codex)) bucket = codex;
        else if (payload.TryGetProperty("rateLimits", out var legacy)) bucket = legacy;
        if (bucket.ValueKind != JsonValueKind.Object) return new();
        var primary = Window(bucket, "primary");
        var secondary = Window(bucket, "secondary");
        var blocked = bucket.TryGetProperty("rateLimitReachedType", out var state) && state.ValueKind != JsonValueKind.Null;
        return new() { Known = primary is not null || secondary is not null, Primary = primary, Secondary = secondary, ServerBlocked = blocked };
    }

    public DateTimeOffset? GetRetryAt(DateTimeOffset now)
    {
        if (!IsBlocked) return null;
        var depleted = Windows.Where(w => w.UsedPercent >= 100).ToList();
        if (depleted.Count == 0 || depleted.Any(w => w.ResetsAt is null)) return now.AddMinutes(5);
        var last = depleted.Max(w => w.ResetsAt!.Value);
        return last > now ? last.AddSeconds(15) : now.AddMinutes(5);
    }

    private static RateWindow? Window(JsonElement bucket, string name)
    {
        if (!bucket.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("usedPercent", out var used) || !used.TryGetDouble(out var pct) || !double.IsFinite(pct)) return null;
        DateTimeOffset? reset = null;
        if (value.TryGetProperty("resetsAt", out var at) && at.TryGetInt64(out var unix))
        {
            try { reset = DateTimeOffset.FromUnixTimeSeconds(unix); } catch (ArgumentOutOfRangeException) { }
        }
        var duration = value.TryGetProperty("windowDurationMins", out var mins) && mins.TryGetInt32(out var number) ? number : 0;
        return new(Math.Clamp(pct, 0, 100), reset, duration);
    }
}
