using System.Collections.Generic;   // <— wichtig
namespace DOL.Abstractions;
public interface IRealmAbilityService
{
    RealmAbility Get(RealmAbilityId id);
    IReadOnlyList<RealmAbility> GetByClass(ClassId classId);
    int GetCost(RealmAbilityId id, int level);
    bool MeetsPrerequisites(RealmAbilityId id, IReadOnlyDictionary<RealmAbilityId,int> owned);
}
public readonly record struct RealmAbilityId(int Value);
public record RealmAbility(RealmAbilityId Id, string Name, int MaxLevel, IReadOnlyList<RealmAbilityId> Prerequisites);
