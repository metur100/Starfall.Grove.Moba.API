using System.Text;

namespace Starfall.Grove.Moba.Api.Game;

/// <summary>
/// Player names are seen by everyone in a match, so they are cleaned: no control characters, at most 16 characters,
/// and names containing slurs or crude words become "Wanderer". The check looks through spacing, punctuation and
/// look-alike digits (5h1t), so the simplest tricks don't get past it.
/// </summary>
public static class Names
{
    public const string Default = "Wanderer";

    private static readonly string[] Blocked =
    [
        // Only words that don't hide inside ordinary names (no "rape" for grape, "cock" for peacock, "anal" for canal).
        "fuck", "shit", "cunt", "bitch", "whore", "slut", "pussy", "penis", "vagina", "porn",
        "nigger", "nigga", "faggot", "retard", "nazi", "hitler", "killyourself", "asshole",
        "wichser", "fotze", "hurensohn", "schlampe", "arschloch", "kanake",
    ];

    public static string Clean(string? name)
    {
        var n = new string((name ?? "").Where(c => !char.IsControl(c) && c is not ('​' or '‍' or '﻿')).ToArray()).Trim();
        if (n.Length > 16) n = n[..16].Trim();
        if (n.Length == 0 || Offensive(n)) return Default;
        return n;
    }

    /// <summary>A chat message with every rude word replaced by stars (words are checked one at a time, so "grape"
    /// stays, while "f.u.c.k" and "5h1t" don't).</summary>
    public static string Censor(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\S+", m => Offensive(m.Value) ? new string('*', m.Value.Length) : m.Value);

    public static bool Offensive(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            var c = ch switch { '0' => 'o', '1' => 'i', '!' => 'i', '3' => 'e', '4' => 'a', '@' => 'a', '5' => 's', '$' => 's', '7' => 't', '8' => 'b', _ => ch };
            if (char.IsLetter(c)) sb.Append(c);
        }
        var flat = sb.ToString();
        return Blocked.Any(flat.Contains);
    }
}
