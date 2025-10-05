using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Abstractions;
namespace DOL.Adapters.DOL.Classes;
public sealed class DolClassMapper
{
    public ClassInfo MapClass(int dolClassId, string name, int realm)
        => new(new ClassId(dolClassId), name, (Realm)realm);
    public IReadOnlyList<SpecLine> MapSpecs(IEnumerable<(string name,int max)> specs)
        => specs.Select(s => new SpecLine(s.name, s.max)).ToList();
    public BaseStats MapBase(int str,int con,int dex,int qui,int @int,int pie,int emp,int cha)
        => new(str, con, dex, qui, @int, pie, emp, cha);
}
