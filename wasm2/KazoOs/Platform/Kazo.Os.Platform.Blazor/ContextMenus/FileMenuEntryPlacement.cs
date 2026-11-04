using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Files;

namespace Kazo.Os.Platform.Blazor.ContextMenus;

/// <summary>
/// Places the registered entries in a file menu (ADR 0151 §4), once they are filtered. Pure: the
/// rules are tested on menus built by hand.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>The default entries keep their groups.</item>
/// <item><c>first</c>: one group shared by every app, at the top of the menu.</item>
/// <item><c>after: X</c>: right after X, in X's group; several entries after the same X follow one another.</item>
/// <item><c>afterGroup: X</c>: a new group right after X's group, shared by the entries placed after the same X (ADR 0159 §6).</item>
/// <item>No position: the shared group of the apps, right before the group of "Properties" (at the end without it).</item>
/// <item>At the same place: installation rank, then declaration order — the order of the candidates.</item>
/// <item>An anchor that is not in this menu, or a same-app entry that was not placed, sends the entry to the shared group.</item>
/// </list>
/// Anchors are searched among the top-level entries only: a submenu's entries are not places.
/// </remarks>
public static class FileMenuEntryPlacement
{
    /// <summary>One entry to place: its app, its registration, and the item built for it (submenu included).</summary>
    /// <param name="AppId">The app that registered it.</param>
    /// <param name="Entry">The registration (position fields).</param>
    /// <param name="Item">The menu item.</param>
    public sealed record Candidate(string AppId, AppContextMenuEntryManifest Entry, HackMenuItemDefinition Item);

    /// <summary>Places <paramref name="candidates"/> in <paramref name="menu"/>, in their order.</summary>
    /// <param name="menu">The menu, with the default entries.</param>
    /// <param name="candidates">The filtered entries, in installation rank then declaration order.</param>
    public static void Place(HackMenuDefinition menu, IReadOnlyList<Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return;
        }

        HackMenuGroupDefinition? firstGroup = null;
        HackMenuGroupDefinition? commonGroup = null;
        Dictionary<string, HackMenuGroupDefinition> afterGroups = new(StringComparer.Ordinal);
        Dictionary<string, string> lastAfter = new(StringComparer.Ordinal);

        // Entries anchored to an entry of their own app may come before it in declaration order: place
        // what can be placed, repeat while something moves, then send the rest to the shared group.
        List<Candidate> pending = [.. candidates];
        bool moved = true;
        while (pending.Count > 0 && moved)
        {
            moved = false;
            foreach (Candidate candidate in pending.ToArray())
            {
                AppContextMenuEntryManifest entry = candidate.Entry;
                if (entry.First)
                {
                    firstGroup ??= InsertGroup(menu, 0);
                    firstGroup.Items.Add(candidate.Item);
                }
                else if (entry.After is { } after)
                {
                    string anchor = AnchorId(candidate.AppId, after);
                    if (!TryLocate(menu, lastAfter.GetValueOrDefault(anchor, anchor), out HackMenuGroupDefinition? group, out int index))
                    {
                        if (IsOwnEntry(candidate, after, candidates))
                        {
                            continue;
                        }

                        Common(menu, ref commonGroup).Items.Add(candidate.Item);
                    }
                    else
                    {
                        group.Items.Insert(index + 1, candidate.Item);
                        lastAfter[anchor] = candidate.Item.Id;
                    }
                }
                else if (entry.AfterGroup is { } afterGroup)
                {
                    string anchor = AnchorId(candidate.AppId, afterGroup);
                    if (afterGroups.TryGetValue(anchor, out HackMenuGroupDefinition? shared))
                    {
                        shared.Items.Add(candidate.Item);
                    }
                    else if (TryLocate(menu, anchor, out HackMenuGroupDefinition? group, out _))
                    {
                        HackMenuGroupDefinition created = InsertGroup(menu, menu.Groups.IndexOf(group) + 1);
                        created.Items.Add(candidate.Item);
                        afterGroups[anchor] = created;
                    }
                    else if (IsOwnEntry(candidate, afterGroup, candidates))
                    {
                        continue;
                    }
                    else
                    {
                        Common(menu, ref commonGroup).Items.Add(candidate.Item);
                    }
                }
                else
                {
                    Common(menu, ref commonGroup).Items.Add(candidate.Item);
                }

                pending.Remove(candidate);
                moved = true;
            }
        }

        foreach (Candidate candidate in pending)
        {
            Common(menu, ref commonGroup).Items.Add(candidate.Item);
        }
    }

    /// <summary>The menu id an anchor stands for: a default id as is, an entry of the same app by its menu id.</summary>
    private static string AnchorId(string appId, string anchor) =>
        AppContextMenus.DefaultItemIds.Contains(anchor) ? anchor : FileMenuEntries.IdOf(appId, anchor);

    /// <summary>Whether the anchor names an entry of the candidate's app that is still to be placed in this menu.</summary>
    private static bool IsOwnEntry(Candidate candidate, string anchor, IReadOnlyList<Candidate> candidates) =>
        !AppContextMenus.DefaultItemIds.Contains(anchor)
        && candidates.Any(other => other.AppId == candidate.AppId && other.Entry.Name == anchor && !ReferenceEquals(other, candidate));

    private static bool TryLocate(
        HackMenuDefinition menu,
        string id,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HackMenuGroupDefinition? group,
        out int index)
    {
        foreach (HackMenuGroupDefinition candidate in menu.Groups)
        {
            for (int position = 0; position < candidate.Items.Count; position++)
            {
                if (candidate.Items[position].Id == id)
                {
                    group = candidate;
                    index = position;
                    return true;
                }
            }
        }

        group = null;
        index = -1;
        return false;
    }

    /// <summary>The shared group of the apps, created right before the group of "Properties", or at the end.</summary>
    private static HackMenuGroupDefinition Common(HackMenuDefinition menu, ref HackMenuGroupDefinition? common)
    {
        if (common is not null)
        {
            return common;
        }

        int index = TryLocate(menu, HackFileMenuItems.Properties, out HackMenuGroupDefinition? properties, out _)
            ? menu.Groups.IndexOf(properties)
            : menu.Groups.Count;
        common = InsertGroup(menu, index);
        return common;
    }

    private static HackMenuGroupDefinition InsertGroup(HackMenuDefinition menu, int index)
    {
        HackMenuGroupDefinition group = new();
        menu.Groups.Insert(index, group);
        return group;
    }
}
