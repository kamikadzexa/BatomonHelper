using System.Text.RegularExpressions;

namespace BatomonHelper.Core;

/// <summary>Displayed game names can differ from stable wiki identifiers.</summary>
public static class CreatureNames
{
    private static readonly IReadOnlyDictionary<string, string[]> Aliases = new Dictionary<string, string[]>
    {
        ["galvanade"] = ["Electranade"], ["infernade"] = ["Pyronade"],
        ["brawlmantis"] = ["Braulmantis"], ["dragonarch"] = ["Draconarch"]
    };

    public static bool Matches(MonsterDefinition monster, string name)
    {
        var normalized = Normalize(name);
        return normalized.Length > 0 && (Normalize(monster.Name) == normalized || Normalize(monster.Id) == normalized ||
            Aliases.GetValueOrDefault(monster.Id, []).Any(alias => Normalize(alias) == normalized));
    }
    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", "");
}
