using System;
using System.Collections.Generic;
using DOL.Events;
using DOL.GS;

namespace DOL.GS.Atlantis
{
    /// <summary>
    /// Hört auf Boss-Tode und vergibt Encounter-Credit an Spieler in Reichweite.
    /// Mapping: NPC-Name(n) -> ArtifactID (DB-Key).
    /// </summary>
    public static class EncounterCreditRouter
    {
        private static bool _started;

        // Initiales Beispiel-Mapping – erweitere nach Bedarf.
        // ArtifactID MUSS dem DB-Key entsprechen (z. B. "Maddening_Scalars").
        private static readonly List<Matcher> _map = new()
        {
            new Matcher("Maddening_Scalars", "Maddening Scalars"),
            // new Matcher("Cloudsong", "Cloudsong"),
            // new Matcher("Traldors_Oracle", "Traldor"),
            // …
        };

        public static void EnsureStarted()
        {
            if (_started) return;
            _started = true;

            // Falls dein Core statt Dying -> Die verwendet, die Zeile unten entsprechend anpassen.
            GameEventMgr.AddHandler(GameLivingEvent.Dying, OnLivingDying);
        }

        private static void OnLivingDying(DOLEvent e, object sender, EventArgs args)
        {
            if (sender is not GameNPC npc) return;

            var artifactId = ResolveArtifactId(npc);
            if (string.IsNullOrEmpty(artifactId)) return;

            const int radius = 3500;
            foreach (var player in npc.GetPlayersInRadius(radius))
            {
                ArtifactMgr.GrantArtifactCredit(player, artifactId);
            }
        }

        private static string ResolveArtifactId(GameNPC npc)
        {
            string name = npc?.Name ?? string.Empty;
            ushort region = npc?.CurrentRegionID ?? (ushort)0;

            foreach (var m in _map)
            {
                if (m.IsMatch(name, region))
                    return m.ArtifactID;
            }
            return null;
        }

        private sealed class Matcher
        {
            public string ArtifactID { get; }
            public string[] Names { get; }
            public ushort? Region { get; }
            public bool Fuzzy { get; }

            public Matcher(string artifactId, params string[] exactNames)
            {
                ArtifactID = artifactId;
                Names = exactNames ?? Array.Empty<string>();
                Fuzzy = false;
            }

            public Matcher(string artifactId, bool fuzzy, ushort? region, params string[] names)
            {
                ArtifactID = artifactId;
                Fuzzy = fuzzy;
                Region = region;
                Names = names ?? Array.Empty<string>();
            }

            public bool IsMatch(string npcName, ushort region)
            {
                if (Region.HasValue && Region.Value != region) return false;
                if (Names == null || Names.Length == 0) return false;

                foreach (var n in Names)
                {
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    if (Fuzzy)
                    {
                        if (npcName?.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                    else
                    {
                        if (string.Equals(npcName, n, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                return false;
            }
        }
    }
}
