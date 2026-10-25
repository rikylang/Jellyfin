using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.LiveTv;

/// <summary>
/// Places events on stable per-category channels ("Calcio 1…N"): an event keeps its slot across refreshes, the
/// number of slots of a category only grows (channel items and numbers never jump around) and overlapping events
/// go to the next free slot.
/// </summary>
public static class SlotAllocator
{
    /// <summary>Default category for events without one.</summary>
    public const string DefaultCategory = "Live";

    private static readonly TimeSpan ForgetCategoryAfter = TimeSpan.FromDays(14);

    /// <summary>Builds the slot channel id of a category.</summary>
    /// <param name="category">The category.</param>
    /// <param name="slot">The zero-based slot.</param>
    /// <returns>The id.</returns>
    public static string SlotId(string category, int slot) => "slot-" + ChannelIds.Short(category) + "-" + (slot + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Allocates events to slots and updates the state.</summary>
    /// <param name="events">The candidate events.</param>
    /// <param name="state">The persisted state (updated).</param>
    /// <param name="now">Now.</param>
    /// <param name="config">Configuration.</param>
    /// <returns>The assignments by slot channel id, and the categories in display order.</returns>
    public static (Dictionary<string, List<SlotAssignment>> Assignments, List<(string Category, CategorySlots Slots)> Categories) Allocate(
        IEnumerable<LiveEvent> events, LineupState state, DateTimeOffset now, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(config);
        var max = Math.Clamp(config.MaxSlotsPerCategory, 1, 20);
        var horizon = now.AddDays(Math.Clamp(config.UpcomingDays, 1, 14));
        var result = new Dictionary<string, List<SlotAssignment>>(StringComparer.Ordinal);
        var newEventSlots = new Dictionary<string, int>(StringComparer.Ordinal);

        var candidates = events
            .Select(e => (Event: e, End: EventStatusCalculator.EstimatedEnd(e, config)))
            .Where(x => x.End > now && x.Event.Start < horizon)
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Event.Category) ? DefaultCategory : x.Event.Category!.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in candidates.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!state.Categories.TryGetValue(group.Key, out var cat))
            {
                cat = new CategorySlots { Order = state.Categories.Count == 0 ? 0 : state.Categories.Values.Max(c => c.Order) + 1 };
                state.Categories[group.Key] = cat;
            }

            cat.LastSeen = now;
            var placed = new List<List<(DateTimeOffset Start, DateTimeOffset End)>>();
            for (var i = 0; i < max; i++)
            {
                placed.Add(new List<(DateTimeOffset, DateTimeOffset)>());
            }

            bool Free(int slot, DateTimeOffset start, DateTimeOffset end) => placed[slot].TrueForAll(p => end <= p.Start || start >= p.End);

            var ordered = group.OrderBy(x => x.Event.Start).ThenBy(x => x.Event.Key, StringComparer.Ordinal).ToList();

            // Pass 1: events keep their previous slot when it is still free; pass 2: the others take the first free slot.
            var pending = new List<(LiveEvent Event, DateTimeOffset End)>();
            foreach (var (ev, end) in ordered)
            {
                if (state.EventSlots.TryGetValue(ev.Key, out var previous) && previous < max && Free(previous, ev.Start, end))
                {
                    Place(previous, ev, end);
                }
                else
                {
                    pending.Add((ev, end));
                }
            }

            foreach (var (ev, end) in pending)
            {
                for (var slot = 0; slot < max; slot++)
                {
                    if (Free(slot, ev.Start, end))
                    {
                        Place(slot, ev, end);
                        break;
                    }
                }
            }

            var used = placed.FindLastIndex(p => p.Count > 0) + 1;
            cat.Slots = Math.Clamp(Math.Max(cat.Slots, used), 1, max);

            void Place(int slot, LiveEvent ev, DateTimeOffset end)
            {
                placed[slot].Add((ev.Start, end));
                newEventSlots[ev.Key] = slot;
                var id = SlotId(group.Key, slot);
                if (!result.TryGetValue(id, out var list))
                {
                    list = new List<SlotAssignment>();
                    result[id] = list;
                }

                list.Add(new SlotAssignment(id, ev, ev.Start, end));
            }
        }

        foreach (var stale in state.Categories.Where(kv => now - kv.Value.LastSeen > ForgetCategoryAfter).Select(kv => kv.Key).ToList())
        {
            state.Categories.Remove(stale);
        }

        state.EventSlots = newEventSlots;
        foreach (var list in result.Values)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        var categories = state.Categories.OrderBy(kv => kv.Value.Order).Select(kv => (kv.Key, kv.Value)).ToList();
        return (result, categories);
    }
}
