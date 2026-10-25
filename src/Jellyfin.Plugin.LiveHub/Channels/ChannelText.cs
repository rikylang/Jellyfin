using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Locale;
using Jellyfin.Plugin.LiveHub.Localization;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Users;

namespace Jellyfin.Plugin.LiveHub.Channels;

/// <summary>
/// Short, TV friendly names and localized descriptions (local time, competition/category, sources, status).
/// </summary>
public sealed class ChannelText
{
    /// <summary>Maximum length of a title on TV screens.</summary>
    public const int MaxTitle = 42;

    private readonly LocalizationService _loc;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelText"/> class.
    /// </summary>
    /// <param name="loc">Localization.</param>
    public ChannelText(LocalizationService loc)
    {
        _loc = loc;
    }

    /// <summary>Truncates a title at a word boundary ("Real Madrid vs Barcelona…").</summary>
    /// <param name="title">The title.</param>
    /// <param name="max">Maximum length.</param>
    /// <returns>The short title.</returns>
    public static string Shorten(string title, int max = MaxTitle)
    {
        ArgumentNullException.ThrowIfNull(title);
        var t = title.Trim();
        if (t.Length <= max)
        {
            return t;
        }

        var cut = t.LastIndexOf(' ', max - 1);
        return (cut > max / 2 ? t[..cut] : t[..(max - 1)]).TrimEnd(' ', '-', '·', ',', ':') + "…";
    }

    /// <summary>Builds the item name of an event.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="view">The user view.</param>
    /// <param name="style">Time style.</param>
    /// <returns>The name.</returns>
    public string Name(LiveEvent ev, UserView view, NameStyle style)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(view);
        var title = Shorten(ev.Title);
        var status = EventStatusCalculator.Status(ev, view.Now, view.Config);
        if (status == EventStatus.Live)
        {
            return _loc.Get(view.UiLanguage, "status.live") + " · " + title;
        }

        var local = view.Local(ev.Start);
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (style == NameStyle.TimeOnly)
        {
            return time + " · " + title;
        }

        return Day(local, view) + " " + time + " · " + title;
    }

    /// <summary>Builds the description of an event.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="streams">Streams visible to the user.</param>
    /// <param name="view">The user view.</param>
    /// <param name="providerNames">Provider id → name.</param>
    /// <returns>The overview.</returns>
    public string Overview(LiveEvent ev, IReadOnlyList<LiveStream> streams, UserView view, IReadOnlyDictionary<string, string> providerNames)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(providerNames);
        var lang = view.UiLanguage;
        var sb = new StringBuilder();
        var bucket = EventStatusCalculator.Bucket(ev, view.Now, view.Config);
        var local = view.Local(ev.Start);
        var when = Day(local, view) + " " + local.ToString("HH:mm", CultureInfo.InvariantCulture) + " (" + ZoneLabel(view) + ")";
        sb.Append(bucket switch
        {
            DisplayBucket.Live => _loc.Get(lang, "ov.liveNow") + " · " + when,
            DisplayBucket.Imminent => _loc.Format(lang, "ov.startsSoon", 15) + " · " + when,
            DisplayBucket.Soon => _loc.Format(lang, "ov.startsIn", Math.Max(1, (int)Math.Ceiling((ev.Start - view.Now).TotalMinutes / 15) * 15)) + " · " + when,
            DisplayBucket.Ended => _loc.Get(lang, "status.ended") + " · " + when,
            _ => when,
        });
        sb.Append('\n');

        var context = string.Join(" · ", new[] { ev.Competition, ev.Category }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (context.Length > 0)
        {
            sb.Append(context).Append('\n');
        }

        var sources = ev.SourceProviders.Where(view.ProviderVisible).Select(p => providerNames.TryGetValue(p, out var n) ? n : p).Distinct().ToList();
        if (sources.Count > 0)
        {
            sb.Append(_loc.Format(lang, "ov.sources", string.Join(", ", sources))).Append('\n');
        }

        var languages = streams.Select(s => s.Language).Concat(new[] { ev.Language }).Where(l => l is not null).Distinct().Select(l => LocaleNormalizer.LanguageName(l, lang)).ToList();
        if (languages.Count > 0)
        {
            sb.Append(_loc.Format(lang, "ov.languages", string.Join(", ", languages))).Append('\n');
        }

        var qualities = streams.Select(s => s.Height).Where(h => h is > 0).Distinct().OrderByDescending(h => h).Select(StreamClassifier.LabelFromHeight).ToList();
        if (qualities.Count > 0)
        {
            sb.Append(_loc.Format(lang, "ov.quality", string.Join(", ", qualities))).Append('\n');
        }

        if (streams.Count > 0 || ev.HasLazyStreams)
        {
            sb.Append(_loc.Format(lang, "ov.streams", ev.HasLazyStreams && streams.Count == 0 ? "…" : streams.Count.ToString(CultureInfo.InvariantCulture))).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            sb.Append('\n').Append(ev.Description!.Trim());
        }

        return sb.ToString().Trim();
    }

    /// <summary>Builds the description of a 24/7 channel.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="view">The user view.</param>
    /// <param name="providerName">The provider name.</param>
    /// <returns>The overview.</returns>
    public string ChannelOverview(LiveChannel channel, UserView view, string providerName)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(view);
        var lang = view.UiLanguage;
        var parts = new List<string> { _loc.Get(lang, "ov.channel247") };
        if (!string.IsNullOrWhiteSpace(channel.Category))
        {
            parts.Add(channel.Category!);
        }

        if (channel.Language is not null)
        {
            parts.Add(LocaleNormalizer.LanguageName(channel.Language, lang));
        }

        var sb = new StringBuilder(string.Join(" · ", parts)).Append('\n').Append(_loc.Format(lang, "ov.sources", providerName));
        if (!string.IsNullOrWhiteSpace(channel.Description))
        {
            sb.Append("\n\n").Append(channel.Description!.Trim());
        }

        return sb.ToString();
    }

    /// <summary>Gets a localized folder name.</summary>
    /// <param name="key">The folder key (live, today...).</param>
    /// <param name="uiLanguage">The UI language.</param>
    /// <returns>The name.</returns>
    public string Folder(string key, string uiLanguage) => _loc.Get(uiLanguage, "folder." + key);

    /// <summary>Names a day folder of "Upcoming": "1 · domani", "2 · lun 27/10".</summary>
    /// <param name="offset">Days from today (1..7).</param>
    /// <param name="view">The user view.</param>
    /// <returns>The name.</returns>
    public string DayFolder(int offset, UserView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var day = view.Local(view.Now).Date.AddDays(offset);
        var label = offset == 1 ? _loc.Get(view.UiLanguage, "day.tomorrow") : Day(day, view);
        return offset.ToString(CultureInfo.InvariantCulture) + " · " + label;
    }

    private string Day(DateTime local, UserView view)
    {
        var today = view.Local(view.Now).Date;
        if (local.Date == today)
        {
            return _loc.Get(view.UiLanguage, "day.today");
        }

        if (local.Date == today.AddDays(1))
        {
            return _loc.Get(view.UiLanguage, "day.tomorrow");
        }

        var culture = CultureInfo.GetCultureInfo(view.UiLanguage);
        return local.ToString("ddd dd/MM", culture);
    }

    private static string ZoneLabel(UserView view)
    {
        var offset = view.Zone.GetUtcOffset(view.Now);
        return "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }
}
