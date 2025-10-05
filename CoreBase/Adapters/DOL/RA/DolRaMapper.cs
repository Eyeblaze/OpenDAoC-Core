using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Abstractions;
namespace DOL.Adapters.DOL.RA;
public sealed class DolRaMapper
{
    public RealmAbility Map(int id, string name, int max, IEnumerable<int> prereqIds)
        => new(new RealmAbilityId(id), name, max, prereqIds.Select(x => new RealmAbilityId(x)).ToList());
}
