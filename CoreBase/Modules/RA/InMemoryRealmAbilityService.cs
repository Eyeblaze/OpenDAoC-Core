using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Abstractions;
namespace DOL.Modules.RA;
public sealed class InMemoryRealmAbilityService : IRealmAbilityService
{
    private readonly Dictionary<RealmAbilityId, RealmAbility> _db = new();
    private readonly Dictionary<(RealmAbilityId,int), int> _cost = new();
    public void Add(RealmAbility ra) => _db[ra.Id] = ra;
    public void SetCost(RealmAbilityId id, int level, int cost) => _cost[(id, level)] = cost;
    public RealmAbility Get(RealmAbilityId id) => _db[id];
    public IReadOnlyList<RealmAbility> GetByClass(ClassId classId) => _db.Values.ToList();
    public int GetCost(RealmAbilityId id, int level) => _cost.TryGetValue((id, level), out var v) ? v : 0;
    public bool MeetsPrerequisites(RealmAbilityId id, IReadOnlyDictionary<RealmAbilityId,int> owned)
        => (_db[id].Prerequisites).All(p => owned.ContainsKey(p));
}
