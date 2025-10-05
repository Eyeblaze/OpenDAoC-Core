using System;
using System.Collections.Generic;   // <— wichtig
namespace DOL.Abstractions;
public interface IClassRuleset
{
    ClassInfo GetClassInfo(ClassId id);
    IReadOnlyList<SpecLine> GetSpecLines(ClassId id);
    BaseStats GetBaseStats(ClassId id, int level);
}
public readonly record struct ClassId(int Value);
public record ClassInfo(ClassId Id, string Name, Realm Realm);
public enum Realm { Albion, Midgard, Hibernia }
public record SpecLine(string Name, int MaxLevel);
public record BaseStats(int Strength, int Constitution, int Dexterity, int Quickness, int Intelligence, int Piety, int Empathy, int Charisma);
