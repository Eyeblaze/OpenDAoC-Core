using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DOL.GS.Atlantis
{
    /// <summary>
    /// Fester Index: BuchNAME (fertiges Buch) -> ArtifactID.
    /// Wichtig: Key = exakter Buch-Titel aus der DB/aus dem Item.Name (nicht Id_nb),
    /// Value = DbArtifact.ArtifactID (genau so, wie in deiner "artifact"-Tabelle).
    /// </summary>
    public static class ArtifactBookMap
    {
        // Kern-Map nach "weicher" Normalisierung (siehe NormalizeTitle)
        private static readonly Dictionary<string, string> _byTitle =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // ==== BookID (BookName) -> ArtifactID (ArtifactName) ====
                // Example -> "Alvarus' Bundled Letters" = "Alvarus's Leggings"
                [NormalizeTitle("Alvarus' Bundled Letters")] = "Alvarus' Leggings",
                [NormalizeTitle("Anthos' Fish Skin")] = "Arms of the Winds",
                [NormalizeTitle("Remus' Story")] = "Aten's Shield",
                [NormalizeTitle("King's Vase")] = "Band of Stars",
                [NormalizeTitle("Battler")] = "Battler",
                [NormalizeTitle("Oglidarsh the Half-Giant's Story")] = "Belt of Oglidarsh",
                [NormalizeTitle("Belt of the Moon")] = "Belt of the Moon",
                [NormalizeTitle("Belt of the Sun")] = "Belt of the Sun",
                [NormalizeTitle("An Apprentice's Works")] = "Bracelet of Zo'arkat",
                [NormalizeTitle("Carved Stone Tablet")] = "Braggart's Bow",
                [NormalizeTitle("Bruiser")] = "Bruiser",
                [NormalizeTitle("Arbiter's Personal Papers")] = "Ceremonial Bracers",
                [NormalizeTitle("Cloudsong")] = "Cloudsong",
                [NormalizeTitle("Tyrus' Epic Poem")] = "Crocodile Tear Ring",
                [NormalizeTitle("Marricus' Journal")] = "Crocodile's Tooth Dagger",
                [NormalizeTitle("Advisor's Personal Log")] = "Crown of Zahur",
                [NormalizeTitle("Damyon's Journal")] = "Cyclops Eye Shield",
                [NormalizeTitle("Loukas' Journal")] = "Dream Sphere",
                [NormalizeTitle("Crafter's Pages on Lightstones")] = "Eerie Darkness Stone",
                [NormalizeTitle("Complete Egg of Youth Scroll")] = "Egg of Youth",
                [NormalizeTitle("Eirene's Journal")] = "Eirene's Hauberk",
                [NormalizeTitle("Enyalios' Boots")] = "Enyalio's Boots",
                [NormalizeTitle("Erinys' Charm")] = "Erinys Charm",
                [NormalizeTitle("Eternal Plant Guide")] = "Eternal Plant",
                [NormalizeTitle("King Kiron's Notes to Cyrell")] = "Flamedancer's Boots",
                [NormalizeTitle("Flask")] = "A Flask",
                [NormalizeTitle("Fool's Bow Tale")] = "Fool's Bow",
                [NormalizeTitle("Foppish Sleeves")] = "Foppish Sleeves",
                [NormalizeTitle("Book of Lost Memories, complete story")] = "Gem of Lost Memories",
                [NormalizeTitle("Dianna's Tragic Tale")] = "Goddess Necklace",
                [NormalizeTitle("Bence's Letters to Helenia")] = "Golden Scarab Vest",
                [NormalizeTitle("A Love Story")] = "Guard of Valor",
                [NormalizeTitle("Bellona's Diary")] = "Harpy Feather Cloak",
                [NormalizeTitle("Vara's Medical Log")] = "Healer's Embrace",
                [NormalizeTitle("Tarin's Animal Skin")] = "Jacina's Sash",
                [NormalizeTitle("Kalare's Memoirs")] = "Kalare's Necklace",
                [NormalizeTitle("Scalars")] = "Maddening Scalars",
                [NormalizeTitle("Malice's Axe")] = "Malice's Axe",
                [NormalizeTitle("Mariasha's Wall Section")] = "Mariasha's Sharkskin Gloves",
                [NormalizeTitle("Nailah's Diary")] = "Nailah's Robes",
                [NormalizeTitle("Dysis' Tablet")] = "Night's Shroud Bracelet",
                [NormalizeTitle("Great Hunt, complete story")] = "Orion's Belt",
                [NormalizeTitle("Phoebus' Harp Tale")] = "Phoebus Harp Necklace",
                [NormalizeTitle("Journal of Public Notices")] = "Ring of Dances",
                [NormalizeTitle("Ring of fire")] = "Ring of Fire",
                [NormalizeTitle("Tribute to Adauron, complete story")] = "Ring of Unyielding Will",
                [NormalizeTitle("Adnes's Bundled Letters")] = "Scepter of the Meritorious",
                [NormalizeTitle("Shades of Mist")] = "Shades of Mist",
                [NormalizeTitle("Shield of Khaos")] = "Shield of Khaos",
                [NormalizeTitle("Snatcher Tales")] = "Snatcher",
                [NormalizeTitle("Spear of Kings Tale")] = "Spear of Kings",
                [NormalizeTitle("Staff of the Gods Tale")] = "Staff of the Gods",
                [NormalizeTitle("Helenia's Letters to Bence")] = "Stone of Atlantis",
                [NormalizeTitle("Atlantis' Magic Tablets")] = "Tablet of Atlantis",
                [NormalizeTitle("Tartaros' Gift")] = "Tartaros' Gift",
                [NormalizeTitle("History of the Golden Spear")] = "The Golden Spear",
                [NormalizeTitle("Complete Wooden Triptych")] = "Scorpion's Tail Ring",
                [NormalizeTitle("Julea's Story")] = "Snakecharmer's Weapon",
                [NormalizeTitle("Complete Thoughts of Hermes")] = "Winged Helm",
                [NormalizeTitle("Complete Book of Glyphs")] = "Traitor's Dagger",
                [NormalizeTitle("Completed Dichotory's Dissertation")] = "Traldor's Oracle",
                [NormalizeTitle("Wing's Dive")] = "Wing's Dive",
            };

        /// <summary>
        /// Versucht, nur anhand des Buchtitels (Item.Name) die ArtifactID zu finden.
        /// </summary>
        public static bool TryResolveByName(string rawBookTitle, out string artifactId)
        {
            artifactId = null;
            if (string.IsNullOrWhiteSpace(rawBookTitle)) return false;

            var key = NormalizeTitle(rawBookTitle);
            return _byTitle.TryGetValue(key, out artifactId);
        }

        /// <summary>
        /// Leichtgewichtige Normalisierung:
        /// - Unicode-Diakritika entfernen (FormD)
        /// - „fancy“ Apostroph (’ U+2019) auf einfachen ' mappen
        /// - alles in Kleinbuchstaben
        /// - Mehrfach-Spaces auf einen Space
        /// </summary>
        public static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;

            // Unicode-Normalisierung (Diakritika trennen)
            string formD = title.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);

            foreach (var ch in formD)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc == UnicodeCategory.NonSpacingMark) continue; // Diakritika droppen

                // Smart quotes vereinheitlichen
                if (ch == '’') { sb.Append('\''); continue; }

                sb.Append(ch);
            }

            // zurück zu "kompakter" Form
            string s = sb.ToString().Normalize(NormalizationForm.FormC);

            // Whitespace komprimieren & trimmen
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

            // Kleinschreibung
            s = s.ToLowerInvariant();

            return s;
        }
    }
}
