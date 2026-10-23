using System;
using System.Collections.Generic;

namespace Cpfs.Core.Layout;

// Which collapsible sections are open. A section the user has not opened or closed follows its default: open when
// it needs attention (an error, or something only an administrator can read). Once the user chooses, that choice
// stays for as long as the store lives, whatever later readings say.
public sealed class SectionExpansion
{
    private readonly Dictionary<string, bool> _chosen = new(StringComparer.Ordinal);

    public bool IsExpanded(string id, bool needsAttention) => _chosen.TryGetValue(id, out var open) ? open : needsAttention;

    public void Set(string id, bool expanded) => _chosen[id] = expanded;

    public void SetAll(IEnumerable<string> ids, bool expanded)
    {
        foreach (var id in ids)
        {
            _chosen[id] = expanded;
        }
    }
}
