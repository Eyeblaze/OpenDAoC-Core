using System;
using Xunit;
using FluentAssertions;

using DOL.Abstractions;
using DOL.Modules.Classes;
using DOL.Modules.RA;
using DOL.Adapters.DOL.Classes;
using DOL.Adapters.DOL.RA;

namespace Gameplay.Tests;

public class ClassesAndRaTests
{
    [Fact]
    public void Class_and_RA_basic_mapping_works()
    {
        var mapper = new DolClassMapper();
        var raMapper = new DolRaMapper();

        var paladin = mapper.MapClass(6, "Paladin", (int)Realm.Albion);
        var specs = mapper.MapSpecs(new [] { ("Chants", 50), ("Two Handed", 50) });
        var cls = new InMemoryClassRuleset((id, lvl) => new BaseStats(60,60,60,60,60,60,60,60));
        cls.AddClass(paladin, specs);

        var ra = raMapper.Map(101, "Purge", 3, Array.Empty<int>());
        var raSvc = new InMemoryRealmAbilityService();
        raSvc.Add(ra);
        raSvc.SetCost(ra.Id, 1, 10);

        cls.GetClassInfo(paladin.Id).Name.Should().Be("Paladin");
        cls.GetSpecLines(paladin.Id).Should().Contain(s => s.Name == "Chants");
        raSvc.Get(ra.Id).Name.Should().Be("Purge");
        raSvc.GetCost(ra.Id, 1).Should().Be(10);
    }
}
