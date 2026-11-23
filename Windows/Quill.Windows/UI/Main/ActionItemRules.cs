using Quill.Core.Domain;

namespace Quill.Windows.UI;

/// <summary>Rules for the owner's tasks ("Mis tareas") and for ticking action items off.</summary>
internal static class ActionItemRules
{
    /// <summary>An action item is mine when the model flagged it or its owner is "me".</summary>
    public static bool IsMine(ActionItem item) =>
        item.IsMine || string.Equals(item.Owner?.Trim(), SpeakerIds.Me, StringComparison.OrdinalIgnoreCase);

    /// <summary>Indexes (into <see cref="MeetingSummary.ActionItems"/>) of my action items.</summary>
    public static IReadOnlyList<int> MineIndexes(MeetingSummary summary) =>
        summary.ActionItems
            .Select((item, index) => (item, index))
            .Where(pair => IsMine(pair.item))
            .Select(pair => pair.index)
            .ToList();

    /// <summary>
    /// Returns a copy of <paramref name="summary"/> with the item at <paramref name="index"/>
    /// marked done or pending. Everything else, meta included, is kept, so saving it writes
    /// back to the same file (final or live).
    /// </summary>
    public static MeetingSummary SetDone(MeetingSummary summary, int index, bool done)
    {
        if (index < 0 || index >= summary.ActionItems.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "no such action item");
        }

        if (summary.ActionItems[index].Done == done)
        {
            return summary;
        }

        var items = summary.ActionItems.ToArray();
        items[index] = items[index] with { Done = done };
        return summary with { ActionItems = items };
    }
}
