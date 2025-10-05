using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Abstractions;
namespace DOL.Modules.Classes;
public sealed class InMemoryClassRuleset : IClassRuleset
{
    private readonly Dictionary<ClassId, ClassInfo> _info = new();
    private readonly Dictionary<ClassId, List<SpecLine>> _specs = new();
    private readonly Func<ClassId, int, BaseStats> _base;
    public InMemoryClassRuleset(Func<ClassId,int,BaseStats> baseStatsFactory){ _base = baseStatsFactory; }
    public void AddClass(ClassInfo info, IEnumerable<SpecLine> specs){ _info[info.Id] = info; _specs[info.Id] = specs.ToList(); }
    public ClassInfo GetClassInfo(ClassId id) => _info[id];
    public IReadOnlyList<SpecLine> GetSpecLines(ClassId id) => _specs[id];
    public BaseStats GetBaseStats(ClassId id, int level) => _base(id, level);
}
