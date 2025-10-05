/*
* DAWN OF LIGHT - The first free open source DAoC server emulator
*
* This program is free software; you can redistribute it and/or
* modify it under the terms of the GNU General Public License
* as published by the Free Software Foundation; either version 2
* of the License, or (at your option) any later version.
*
* This program is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU General Public License for more details.
*
* You should have received a copy of the GNU General Public License
* along with this program; if not, write to the Free Software
* Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
*
*/
using DOL.Database;
using DOL.Events;
using DOL.GS;
using DOL.GS.PacketHandler;
using DOL.GS.Quests;
using DOL.GS.Scripts;          // falls ihr ScriptMgr nutzt (je nach Core)
using log4net;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DOL.GS
{
    /// <summary>
    /// The artifact manager.
    /// </summary>
    /// <author>Aredhel</author>
    public sealed class ArtifactMgr
    {
        /// <summary>
        /// Defines a logger for this class.
        /// </summary>
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static Dictionary<String, DbArtifact> m_artifacts;
        private static Dictionary<String, List<DbArtifactXItem>> m_artifactVersions;
        private static List<DbArtifactBonus> m_artifactBonuses;
        private static volatile bool _loaded = false;

        // ========= Reuse-Timer-Cache (Delve/Info) =========
        private static readonly Dictionary<string, DateTime> _reuseTimers = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _reuseLock = new object();

        private static string GetArtifactKey(InventoryArtifact item)
        {
            if (item == null) return null;
            // bevorzugt Template-Id_nb, Fallback auf Item-Id_nb
            var key = item.Template?.Id_nb;
            if (string.IsNullOrEmpty(key))
                key = item.Id_nb;
            return string.IsNullOrEmpty(key) ? null : key;
        }

        /// <summary>Startet/verlängert den Reuse-Timer für ein Artefakt.</summary>
        public static void StartReuseTimer(InventoryArtifact item, TimeSpan cooldown)
        {
            var key = GetArtifactKey(item);
            if (key == null) return;
            lock (_reuseLock)
                _reuseTimers[key] = DateTime.UtcNow + cooldown;
        }

        /// <summary>Entfernt einen Reuse-Timer (Reset).</summary>
        public static void ClearReuseTimer(InventoryArtifact item)
        {
            var key = GetArtifactKey(item);
            if (key == null) return;
            lock (_reuseLock)
                _reuseTimers.Remove(key);
        }

        // interner Helfer: liefert den verbleibenden Cooldown als TimeSpan
        private static TimeSpan GetReuseTimerSpan(InventoryArtifact item)
        {
            var key = GetArtifactKey(item);
            if (key == null) return TimeSpan.Zero;

            DateTime expiresAt;
            lock (_reuseLock)
            {
                if (!_reuseTimers.TryGetValue(key, out expiresAt))
                    return TimeSpan.Zero;
            }

            var remaining = expiresAt - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                // abgelaufen -> austragen
                lock (_reuseLock) _reuseTimers.Remove(key);
                return TimeSpan.Zero;
            }
            return remaining;
        }

        // öffentliche API wie zuvor erwartet: int Sekunden Rest-Cooldown
        public static int GetReuseTimer(InventoryArtifact item)
        {
            var span = GetReuseTimerSpan(item);
            if (span <= TimeSpan.Zero) return 0;
            // Aufrunden, damit z.B. 0.8s als 1s angezeigt werden kann
            return (int)Math.Ceiling(span.TotalSeconds);
        }

        // ========= Buchname -> ArtifactID Resolver (robust) =========
        private static readonly object _nameIndexLock = new object();
        private static bool _nameIndexBuilt = false;
        // normalisierte Bezeichner -> ArtifactID
        private static readonly Dictionary<string, string> _nameToArtifactId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Normalisiert: Kleinbuchstaben, Ziffern/Buchstaben behalten, alles andere raus; "the " am Anfang streichen
        private static string NormalizeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Trim();
            if (s.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(4);
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch) || ch == ' ')
                    sb.Append(char.ToLowerInvariant(ch));
            }
            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        private static void EnsureNameIndex()
        {
            lock (_nameIndexLock)
            {
                if (_nameIndexBuilt) return;

                if (m_artifacts == null || m_artifacts.Count == 0)
                {
                    // nichts zu indexieren (wird nach LoadArtifacts() beim nächsten Aufruf gebaut)
                    _nameIndexBuilt = true;
                    return;
                }

                // Basierend auf unseren bekannten Artefakten Synonyme erzeugen
                foreach (var kv in m_artifacts) // m_artifacts: ArtifactID -> DbArtifact
                {
                    var artId = kv.Key; // z.B. "Maddening Scalars"
                    var normFull = NormalizeName(artId);
                    if (!_nameToArtifactId.ContainsKey(normFull))
                        _nameToArtifactId[normFull] = artId;

                    // Letztes Wort als Synonym (z. B. "Scalars" -> "Maddening Scalars"), nur wenn eindeutig
                    var parts = normFull.Split(' ');
                    if (parts.Length >= 2)
                    {
                        var last = parts[parts.Length - 1]; // "scalars", "oracle", "gift" etc.
                        if (!_nameToArtifactId.ContainsKey(last))
                        {
                            bool unique = true;
                            foreach (var other in m_artifacts.Keys)
                            {
                                if (other == artId) continue;
                                var o = NormalizeName(other).Split(' ');
                                if (o.Length > 0 && o[o.Length - 1] == last) { unique = false; break; }
                            }
                            if (unique)
                                _nameToArtifactId[last] = artId;
                        }
                    }

                    // Häufige Varianten ohne Apostroph etc. (Tartaros' Gift -> tartaros gift)
                    var noApos = NormalizeName(artId.Replace("’", "").Replace("'", ""));
                    if (!_nameToArtifactId.ContainsKey(noApos))
                        _nameToArtifactId[noApos] = artId;

                    // Spezialfälle – nur setzen, wenn frei
                    void MapIfFree(string key, string value)
                    {
                        key = NormalizeName(key);
                        if (!_nameToArtifactId.ContainsKey(key))
                            _nameToArtifactId[key] = value;
                    }

                    MapIfFree("tartaros gift", "Tartaros' Gift");
                    MapIfFree("traldors oracle", "Traldor's Oracle");
                    MapIfFree("winged helm", "The Winged Helm");
                    MapIfFree("arms of the winds", "Arms of the Winds");
                }

                _nameIndexBuilt = true;
            }
        }

        /// <summary>
        /// Versucht aus einem Buch-/Item-Namen die ArtifactID herzuleiten (z. B. "Scalars" -> "Maddening Scalars").
        /// </summary>
        public static string GetArtifactIDFromBookName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return null;
            EnsureNameIndex();

            // 1) exakter Normalisierungs-Treffer
            var norm = NormalizeName(rawName);
            if (_nameToArtifactId.TryGetValue(norm, out var direct))
                return direct;

            // 2) "enthält"
            foreach (var kv in _nameToArtifactId)
                if (norm.Contains(kv.Key))
                    return kv.Value;

            // 3) Token-Subset
            var tokens = norm.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (m_artifacts != null)
            {
                foreach (var art in m_artifacts.Keys)
                {
                    var aTok = NormalizeName(art).Split(' ');
                    bool subset = tokens.All(t => aTok.Contains(t));
                    if (subset) return art;
                }
            }

            return null;
        }

        public enum Book { NoPage = 0x0, Page1 = 0x1, Page2 = 0x2, Page3 = 0x4, AllPages = 0x7 };

        public static bool Init()
        {
            EnsureLoaded();
            return true;
        }

        /// <summary>Stellt sicher, dass die Artefakt-Daten einmalig geladen sind.</summary>
        public static void EnsureLoaded()
        {
            if (_loaded) return;
            LoadArtifacts();
            _loaded = true;
        }

        // Liefert alle geladenen Artefakte; bricht nie mit null.
        public static IEnumerable<DOL.Database.DbArtifact> GetAllArtifacts()
        {
            if (m_artifacts != null)
                return m_artifacts.Values;
            return System.Linq.Enumerable.Empty<DOL.Database.DbArtifact>();
        }

        /// <summary>
        /// Load artifacts from the DB.
        /// </summary>
        public static int LoadArtifacts()
        {
            // Load artifacts and books.
            var artifactDbos = GameServer.Database.SelectAllObjects<DbArtifact>();
            m_artifacts = new Dictionary<String, DbArtifact>();
            foreach (DbArtifact artifact in artifactDbos)
                m_artifacts.Add(artifact.ArtifactID, artifact);

            // Load artifact versions.
            var artifactItemDbos = GameServer.Database.SelectAllObjects<DbArtifactXItem>();
            m_artifactVersions = new Dictionary<String, List<DbArtifactXItem>>();
            List<DbArtifactXItem> versionList;
            foreach (DbArtifactXItem artifactVersion in artifactItemDbos)
            {
                if (m_artifactVersions.ContainsKey(artifactVersion.ArtifactID))
                    versionList = m_artifactVersions[artifactVersion.ArtifactID];
                else
                {
                    versionList = new List<DbArtifactXItem>();
                    m_artifactVersions.Add(artifactVersion.ArtifactID, versionList);
                }
                versionList.Add(artifactVersion);
            }

            // Load artifact bonuses.
            var artifactBonusDbos = GameServer.Database.SelectAllObjects<DbArtifactBonus>();
            m_artifactBonuses = new List<DbArtifactBonus>();
            foreach (DbArtifactBonus artifactBonus in artifactBonusDbos)
                m_artifactBonuses.Add(artifactBonus);

            Logging.LoggerManager.Create(typeof(ArtifactMgr)).Info($"{m_artifacts.Count} artifacts loaded.");

            // Install event handlers.
            GameEventMgr.AddHandler(GamePlayerEvent.GainedExperience, new DOLEventHandler(PlayerGainedExperience));

            log.Info(String.Format("{0} artifacts loaded", m_artifacts.Count));
            _loaded = true;
            return m_artifacts.Count;
        }

        /// <summary>
        /// Find the matching artifact for the item
        /// </summary>
        public static String GetArtifactIDFromItemID(String itemID)
        {
            if (itemID == null)
                return null;

            String artifactID = null;
            lock (m_artifactVersions)
            {
                foreach (List<DbArtifactXItem> list in m_artifactVersions.Values)
                {
                    foreach (DbArtifactXItem AxI in list)
                    {
                        if (AxI.ItemID == itemID)
                        {
                            artifactID = AxI.ArtifactID;
                            break;
                        }
                    }
                }
            }

            return artifactID;
        }

        /// <summary>
        /// Get all artifacts
        /// </summary>
        public static List<DbArtifact> GetArtifacts()
        {
            List<DbArtifact> artifacts = new List<DbArtifact>();
            lock (m_artifacts)
            {
                foreach (DbArtifact artifact in m_artifacts.Values)
                    artifacts.Add(artifact);
            }
            return artifacts;
        }

        /// <summary>
        /// Find all artifacts from a particular zone.
        /// </summary>
        public static List<DbArtifact> GetArtifacts(String zone)
        {
            List<DbArtifact> artifacts = new List<DbArtifact>();
            if (zone != null)
            {
                lock (m_artifacts)
                {
                    foreach (DbArtifact artifact in m_artifacts.Values)
                    {
                        if (artifact.Zone == zone)
                            artifacts.Add(artifact);
                    }
                }
            }
            return artifacts;
        }

        /// <summary>
        /// Get a list of all scholars studying this artifact.
        /// </summary>
        public static String[] GetScholars(String artifactID)
        {
            if (artifactID != null)
                lock (m_artifacts)
                    if (m_artifacts.ContainsKey(artifactID))
                        return Util.SplitCSV(m_artifacts[artifactID].ScholarID).ToArray();

            return null;
        }

        /// <summary>
        /// Checks whether or not the item is an artifact.
        /// </summary>
        public static bool IsArtifact(DbInventoryItem item)
        {
            if (item == null)
                return false;

            if (item is InventoryArtifact)
                return true;

            lock (m_artifactVersions)
                foreach (List<DbArtifactXItem> versions in m_artifactVersions.Values)
                    foreach (DbArtifactXItem version in versions)
                        if (version.ItemID == item.Id_nb)
                            return true;
            return false;
        }

        // Falls du keinen öffentlichen Zugriff hast, gib eine readonly-View zurück:
        public static List<DbArtifact> GetArtifactsList()
            => m_artifacts?.Values?.ToList() ?? new List<DbArtifact>(0);

        /// <summary>
        /// Liefert den Quest-Type für ein Artefakt (aus DbArtifact.QuestID/QuestType*).
        /// </summary>
        public static Type GetArtifactQuestType(DbArtifact dbo)
        {
            if (dbo == null) return null;

            var questClassName = !string.IsNullOrWhiteSpace(dbo.QuestID)
                ? dbo.QuestID.Trim()
                : dbo.GetType().GetProperty("QuestType") != null
                    ? (dbo.GetType().GetProperty("QuestType").GetValue(dbo) as string)?.Trim()
                    : null;

            if (string.IsNullOrWhiteSpace(questClassName))
                return null;

            // 2) Über ScriptMgr (je nach Core verfügbar)
            var t2 = ScriptMgr.GetType(questClassName);
            if (t2 != null) return t2;

            // 3) Fallback: CLR-Auflösung
            return Type.GetType(questClassName, throwOnError: false, ignoreCase: false);
        }

        #region Experience/Level

        private static readonly long[] m_xpForLevel =
        {
            0,				// xp to level 0
			50000000,		// xp to level 1
			100000000,		// xp to level 2
			150000000,		// xp to level 3
			200000000,		// xp to level 4
			250000000,		// xp to level 5
			300000000,		// xp to level 6
			350000000,		// xp to level 7
			400000000,		// xp to level 8
			450000000,		// xp to level 9
			500000000		// xp to level 10
		};

        /// <summary>
        /// Determine artifact level from total XP.
        /// </summary>
        public static int GetCurrentLevel(InventoryArtifact item)
        {
            if (item != null)
            {
                for (int level = 10; level >= 0; --level)
                    if (item.Experience >= m_xpForLevel[level])
                        return level;
            }
            return 0;
        }

        /// <summary>
        /// Calculate the XP gained towards the next level (in percent).
        /// </summary>
        public static int GetXPGainedForLevel(InventoryArtifact item)
        {
            if (item != null)
            {
                int level = GetCurrentLevel(item);
                if (level < 10)
                {
                    double xpGained = item.Experience - m_xpForLevel[level];
                    double xpNeeded = m_xpForLevel[level + 1] - m_xpForLevel[level];
                    return (int)(xpGained * 100 / xpNeeded);
                }
            }
            return 0;
        }

        /// <summary>
        /// Get a list of all level requirements for this artifact.
        /// </summary>
        public static int[] GetLevelRequirements(String artifactID)
        {
            int[] requirements = new int[DbArtifactBonus.ID.Max - DbArtifactBonus.ID.Min + 1];

            lock (m_artifactBonuses)
                foreach (DbArtifactBonus bonus in m_artifactBonuses)
                    if (bonus.ArtifactID == artifactID)
                        requirements[bonus.BonusID] = bonus.Level;

            return requirements;
        }

        /// <summary>
        /// What this artifact gains XP from.
        /// </summary>
        public static String GetEarnsXP(InventoryArtifact item)
        {
            return "Slaying enemies and monsters found anywhere.";
        }

        /// <summary>
        /// Called from GameEventMgr when player has gained experience.
        /// </summary>
        public static void PlayerGainedExperience(DOLEvent e, object sender, EventArgs args)
        {
            GamePlayer player = sender as GamePlayer;
            GainedExperienceEventArgs xpArgs = args as GainedExperienceEventArgs;
            if (player == null || xpArgs == null)
                return;

            // Artifacts only gain XP on NPC and player kills
            if (xpArgs.XPSource != eXPSource.Player && xpArgs.XPSource != eXPSource.NPC)
                return;

            if (player.IsPraying)
                return;

            // Suffice to calculate total XP once for all artifacts.
            long xpAmount = xpArgs.ExpBase +
                xpArgs.ExpCampBonus +
                xpArgs.ExpGroupBonus +
                xpArgs.ExpOutpostBonus;

            // Only currently equipped artifacts can gain experience.
            lock (player.Inventory)
            {
                foreach (DbInventoryItem item in player.Inventory.EquippedItems)
                {
                    if (item != null && item is InventoryArtifact)
                    {
                        ArtifactGainedExperience(player, item as InventoryArtifact, xpAmount);
                    }
                }
            }
        }

        /// <summary>
        /// Called when an artifact has gained experience.
        /// </summary>
        private static void ArtifactGainedExperience(GamePlayer player, InventoryArtifact item, long xpAmount)
        {
            if (player == null || item == null)
                return;

            long artifactXPOld = item.Experience;

            // Can't go past level 10, but check to make sure we are level 10 if we have the XP
            if (artifactXPOld >= m_xpForLevel[10])
            {
                while (item.ArtifactLevel < 10)
                {
                    player.Out.SendMessage(String.Format("Your {0} has gained a level!", item.Name), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    item.OnLevelGained(player, item.ArtifactLevel + 1);
                }
                return;
            }

            if (player.Guild != null && player.Guild.BonusType == Guild.eBonusType.ArtifactXP)
            {
                long xpBonus = (long)(xpAmount * ServerProperties.Properties.GUILD_BUFF_ARTIFACT_XP * .01);
                xpAmount += xpBonus;
                player.Out.SendMessage(string.Format("Your {0} gains additional experience due to your guild's buff!", item.Name), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            }

            // All artifacts share the same XP table, we make them level
            // at different rates by tweaking the XP rate.
            int xpRate;

            lock (m_artifacts)
            {
                DbArtifact artifact = m_artifacts[item.ArtifactID];
                if (artifact == null)
                    return;
                xpRate = artifact.XPRate;
            }

            long artifactXPNew = (long)(artifactXPOld + (xpAmount * xpRate) / ServerProperties.Properties.ARTIFACT_XP_RATE);
            item.Experience = artifactXPNew;

            player.Out.SendMessage(String.Format("Your {0} has gained experience.", item.Name), eChatType.CT_Important, eChatLoc.CL_SystemWindow);

            // Now let's see if this artifact has gained a new level yet.
            for (int level = 1; level <= 10; ++level)
            {
                if (artifactXPNew < m_xpForLevel[level])
                    break;

                if (artifactXPOld > m_xpForLevel[level])
                    continue;

                player.Out.SendMessage(String.Format("Your {0} has gained a level!", item.Name), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                item.OnLevelGained(player, level);
            }
        }

        #endregion

        #region Artifact Versions

        /// <summary>
        /// Get a list of all versions for this artifact.
        /// </summary>
        private static List<DbArtifactXItem> GetArtifactVersions(String artifactID, eRealm realm)
        {
            List<DbArtifactXItem> versions = new List<DbArtifactXItem>();
            if (artifactID != null)
            {
                lock (m_artifactVersions)
                {
                    if (m_artifactVersions.ContainsKey(artifactID))
                    {
                        List<DbArtifactXItem> allVersions = m_artifactVersions[artifactID];
                        foreach (DbArtifactXItem version in allVersions)
                            if (version.Realm == 0 || version.Realm == (int)realm)
                                versions.Add(version);
                    }
                }
            }
            return versions;
        }

        /// <summary>
        /// Create a hashtable containing all item templates that are valid for this class.
        /// </summary>
        public static Dictionary<string, DbItemTemplate> GetArtifactVersions(string artifactID, eCharacterClass charClass, eRealm realm)
        {
            if (artifactID == null)
                return null;

            var allVersions = GetArtifactVersions(artifactID, realm);
            var classVersions = new Dictionary<string, DbItemTemplate>(StringComparer.OrdinalIgnoreCase);

            lock (allVersions)
            {
                foreach (var version in allVersions)
                {
                    var itemTemplate = GameServer.Database.FindObjectByKey<DbItemTemplate>(version.ItemID);
                    if (itemTemplate == null)
                    {
                        log.Warn(string.Format("Artifact item template '{0}' is missing", version.ItemID));
                        continue;
                    }

                    // Filter: nur Templates anzeigen, die zur gewählten Klasse passen
                    bool allowedForClass = false;
                    foreach (var classID in Util.SplitCSV(itemTemplate.AllowedClasses, true))
                    {
                        int parsed;
                        if (int.TryParse(classID, out parsed) && parsed == (int)charClass)
                        {
                            allowedForClass = true;
                            break;
                        }
                    }
                    if (!allowedForClass) continue;

                    // Key aus DB-Version oder heuristisch ableiten
                    var key = version.Version;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        key = InferVersionKeyFromTemplate(itemTemplate);
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            // Fallbacks:
                            // 1) Klammerinhalt aus Name (Maddening Scalars (Cloth))
                            var m = Regex.Match(itemTemplate.Name ?? "", @"\(([^)]+)\)");
                            if (m.Success) key = m.Groups[1].Value.Trim();
                        }
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            // 2) Heuristik über Id_nb-Suffix
                            var id = itemTemplate.Id_nb ?? "";
                            var parts = id.Split('_');
                            if (parts.Length > 1) key = parts[parts.Length - 1];
                        }
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            // 3) Letzter Fallback: Name selbst
                            key = itemTemplate.Name ?? "Version";
                        }
                    }

                    // Kollisionen vermeiden (z. B. 2 Templates würden denselben Key ergeben)
                    var uniqueKey = key;
                    int idx = 2;
                    while (classVersions.ContainsKey(uniqueKey))
                        uniqueKey = $"{key} #{idx++}";

                    classVersions[uniqueKey] = itemTemplate;
                }
            }

            return classVersions;
        }

        // ---- Helfer: baut sinnvolle Keys wie "Cloth", "Leather", "Studded", "Chain", "Plate", "Caster", "Melee" etc.
        private static string InferVersionKeyFromTemplate(DbItemTemplate t)
        {
            if (t == null) return null;

            var name = t.Name ?? "";

            // 1) Klammerinhalt im Namen
            var m = Regex.Match(name, @"\(([^)]+)\)");
            if (m.Success) return m.Groups[1].Value.Trim();

            // 2) Keywords im Namen
            string[] armor = { "Cloth", "Leather", "Studded", "Reinforced", "Scale", "Chain", "Plate" };
            foreach (var k in armor)
                if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return k;

            string[] role = { "Caster", "Melee" };
            foreach (var k in role)
                if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return k;

            // 3) Id_nb-Suffix (z. B. ..._cloth)
            var id = t.Id_nb ?? "";
            var parts = id.Split('_');
            if (parts.Length > 1) return parts[parts.Length - 1];

            return null;
        }

        #endregion

        #region Encounters & Quests

        /// <summary>
        /// Get the quest type from the quest type string.
        /// </summary>
        public static Type GetQuestType(String questTypeString)
        {
            Type questType = null;
            foreach (Assembly asm in ScriptMgr.Scripts)
            {
                questType = asm.GetType(questTypeString);
                if (questType != null)
                    break;
            }

            if (questType == null)
                questType = Assembly.GetAssembly(typeof(GameServer)).GetType(questTypeString);

            return questType;
        }

        /// <summary>
        /// Get the quest type for the encounter from the artifact ID.
        /// </summary>
        public static Type GetEncounterType(String artifactID)
        {
            if (artifactID != null)
                lock (m_artifacts)
                    if (m_artifacts.ContainsKey(artifactID))
                        return GetQuestType(m_artifacts[artifactID].EncounterID);

            return null;
        }

        /// <summary>
        /// Grant bounty point credit for an artifact.
        /// </summary>
        public static bool GrantArtifactBountyCredit(GamePlayer player, String bountyCredit)
        {
            lock (m_artifacts)
                foreach (DbArtifact artifact in m_artifacts.Values)
                    if (artifact.Credit == bountyCredit)
                        return GrantArtifactCredit(player, artifact.ArtifactID);

            return false;
        }

        /// <summary>
        /// Grant credit for an artifact.
        /// </summary>
        public static bool GrantArtifactCredit(GamePlayer player, String artifactID)
        {
            if (player == null || artifactID == null)
                return false;

            if (!player.CanReceiveArtifact(artifactID))
                return false;

            DbArtifact artifact;
            lock (m_artifacts)
            {
                if (!m_artifacts.ContainsKey(artifactID))
                    return false;
                artifact = m_artifacts[artifactID];
            }

            if (artifact == null)
                return false;

            Type encounterType = GetQuestType(artifact.EncounterID);
            if (encounterType == null)
                return false;

            Type artifactQuestType = GetQuestType(artifact.QuestID);
            if (artifactQuestType == null)
                return false;

            if (player.HasFinishedQuest(encounterType) > 0 ||
                player.HasFinishedQuest(artifactQuestType) > 0)
                return false;

            AbstractQuest quest = (AbstractQuest)(System.Activator.CreateInstance(encounterType,
                                                                                  new object[] { player }));

            if (quest == null)
                return false;

            quest.FinishQuest();
            return true;
        }

        #endregion

        #region Scrolls & Books

        /// <summary>
        /// Find the matching artifact for this book.
        /// </summary>
        public static String GetArtifactID(String bookID)
        {
            if (bookID != null)
                lock (m_artifacts)
                    foreach (DbArtifact artifact in m_artifacts.Values)
                        if (artifact.BookID == bookID)
                            return artifact.ArtifactID;

            return null;
        }

        /// <summary>
        /// Check whether these 2 items can be combined.
        /// </summary>
        public static bool CanCombine(DbInventoryItem item1, DbInventoryItem item2)
        {
            String artifactID1 = null;
            Book pageNumbers1 = GetPageNumbers(item1, ref artifactID1);
            if (pageNumbers1 == Book.NoPage || pageNumbers1 == Book.AllPages)
                return false;

            String artifactID2 = null;
            Book pageNumbers2 = GetPageNumbers(item2, ref artifactID2);
            if (pageNumbers2 == Book.NoPage || pageNumbers2 == Book.AllPages)
                return false;

            if (artifactID1 != artifactID2 ||
                (Book)((int)pageNumbers1 & (int)pageNumbers2) != Book.NoPage)
                return false;

            return true;
        }

        /// <summary>
        /// Check which scroll pages are in this item.
        /// </summary>
        public static Book GetPageNumbers(DbInventoryItem item, ref String artifactID)
        {
            if (item == null || item.Object_Type != (int)eObjectType.Magical
                || item.Item_Type != (int)eInventorySlot.FirstBackpack)
                return Book.NoPage;

            lock (m_artifacts)
            {
                foreach (DbArtifact artifact in m_artifacts.Values)
                {
                    artifactID = artifact.ArtifactID;
                    if (item.Name == artifact.Scroll1)
                        return Book.Page1;
                    else if (item.Name == artifact.Scroll2)
                        return Book.Page2;
                    else if (item.Name == artifact.Scroll3)
                        return Book.Page3;
                    else if (item.Name == artifact.Scroll12)
                        return (Book)((int)Book.Page1 | (int)Book.Page2);
                    else if (item.Name == artifact.Scroll13)
                        return (Book)((int)Book.Page1 | (int)Book.Page3);
                    else if (item.Name == artifact.Scroll23)
                        return (Book)((int)Book.Page2 | (int)Book.Page3);
                    else if (!string.IsNullOrEmpty(artifact.BookID) &&
                             !string.IsNullOrEmpty(item.Name) &&
                             item.Name.Equals(artifact.BookID, StringComparison.OrdinalIgnoreCase))
                        return Book.AllPages;
                }
            }

            return Book.NoPage;
        }

        // --- Helpers for robust book-name mapping ---
        private static string NormalizeKey(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string BaseFromScroll(string scrollName)
        {
            if (string.IsNullOrWhiteSpace(scrollName)) return null;
            // typische Formate: "Scalars, Page 1 of 3", "Scalars - Page 2", etc.
            var s = scrollName;
            int cut = s.IndexOf(',');
            if (cut > 0) s = s.Substring(0, cut);
            cut = s.IndexOf(" - Page", StringComparison.OrdinalIgnoreCase);
            if (cut > 0) s = s.Substring(0, cut);
            cut = s.IndexOf(" Page", StringComparison.OrdinalIgnoreCase);
            if (cut > 0) s = s.Substring(0, cut);
            return s.Trim();
        }

        /// <summary>
        /// Find all artifacts that this player carries.
        /// </summary>
        public static List<String> GetArtifacts(GamePlayer player)
        {
            List<String> artifacts = new List<String>();
            lock (player.Inventory.AllItems)
            {
                foreach (DbInventoryItem item in player.Inventory.AllItems)
                {
                    String artifactID = GetArtifactIDFromItemID(item.Id_nb);
                    if (artifactID != null)
                        artifacts.Add(artifactID);
                }
            }
            return artifacts;
        }

        /// <summary>
        /// Get the artifact for this scroll/book item.
        /// </summary>
        public static DbArtifact GetArtifact(DbInventoryItem item)
        {
            String artifactID = "";
            if (GetPageNumbers(item, ref artifactID) == Book.NoPage)
                return null;

            lock (m_artifacts)
                return m_artifacts[artifactID];
        }

        /// <summary>
        /// Whether or not the player has the complete book for this artifact in his backpack.
        /// </summary>
        public static bool HasBook(GamePlayer player, String artifactID)
        {
            if (player == null || artifactID == null)
                return false;

            // Find out which book is needed.
            String bookID;
            lock (m_artifacts)
            {
                DbArtifact artifact = m_artifacts[artifactID];
                if (artifact == null)
                {
                    log.Warn(String.Format("Can't find book for artifact \"{0}\"", artifactID));
                    return false;
                }
                bookID = artifact.BookID;
            }

            // Now check if the player has got it.
            var backpack = player.Inventory.GetItemRange(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);
            foreach (DbInventoryItem item in backpack)
            {
                if (item.Object_Type == (int)eObjectType.Magical &&
                    item.Item_Type == (int)eInventorySlot.FirstBackpack &&
                    item.Name == bookID)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether or not the item is an artifact scroll.
        /// </summary>
        public static bool IsArtifactScroll(DbInventoryItem item)
        {
            String artifactID = null;
            Book pageNumbers = GetPageNumbers(item, ref artifactID);
            return (pageNumbers != Book.NoPage && pageNumbers != Book.AllPages);
        }

        /// <summary>
        /// Combine 2 scrolls.
        /// </summary>
        public static WorldInventoryItem CombineScrolls(DbInventoryItem scroll1, DbInventoryItem scroll2,
                                                       ref bool combinesToBook)
        {
            if (!CanCombine(scroll1, scroll2))
                return null;

            String artifactID = null;
            Book combinedPages = (Book)((int)GetPageNumbers(scroll1, ref artifactID) |
                                        (int)GetPageNumbers(scroll2, ref artifactID));

            combinesToBook = (combinedPages == Book.AllPages);
            return CreatePages(artifactID, combinedPages);
        }

        /// <summary>
        /// Create a scroll or book containing the given page numbers.
        /// </summary>
        private static WorldInventoryItem CreatePages(String artifactID, Book pageNumbers)
        {
            if (artifactID == null || pageNumbers == Book.NoPage)
                return null;

            DbArtifact artifact;
            lock (m_artifacts)
                artifact = m_artifacts[artifactID];

            if (artifact == null)
                return null;

            WorldInventoryItem scroll = WorldInventoryItem.CreateUniqueFromTemplate("artifact_scroll");
            if (scroll == null)
                return null;

            String scrollTitle = null;
            int scrollModel = 499;
            short gold = 4;
            switch (pageNumbers)
            {
                case Book.Page1:
                    scrollTitle = artifact.Scroll1;
                    scrollModel = artifact.ScrollModel1;
                    gold = 2;
                    break;
                case Book.Page2:
                    scrollTitle = artifact.Scroll2;
                    scrollModel = artifact.ScrollModel1;
                    gold = 3;
                    break;
                case Book.Page3:
                    scrollTitle = artifact.Scroll3;
                    scrollModel = artifact.ScrollModel1;
                    break;
                case (Book)((int)Book.Page1 | (int)Book.Page2):
                    scrollTitle = artifact.Scroll12;
                    scrollModel = artifact.ScrollModel2;
                    break;
                case (Book)((int)Book.Page1 | (int)Book.Page3):
                    scrollTitle = artifact.Scroll13;
                    scrollModel = artifact.ScrollModel2;
                    break;
                case (Book)((int)Book.Page2 | (int)Book.Page3):
                    scrollTitle = artifact.Scroll23;
                    scrollModel = artifact.ScrollModel2;
                    break;
                case Book.AllPages:
                    scrollTitle = artifact.BookID;
                    scrollModel = artifact.BookModel;
                    gold = 5;
                    break;
            }

            scroll.Name = scrollTitle;
            scroll.Item.Name = scrollTitle;
            scroll.Model = (ushort)scrollModel;
            scroll.Item.Model = (ushort)scrollModel;

            // Correct for possible errors in generic scroll template (artifact_scroll)
            scroll.Item.Price = Money.GetMoney(0, 0, gold, 0, 0);
            scroll.Item.IsDropable = true;
            scroll.Item.IsPickable = true;
            scroll.Item.IsTradable = true;

            return scroll;
        }

        /// <summary>
        /// Create a scroll from a particular book.
        /// </summary>
        public static WorldInventoryItem CreateScroll(String artifactID, int pageNumber)
        {
            if (pageNumber < 1 || pageNumber > 3)
                return null;

            switch (pageNumber)
            {
                case 1: return CreatePages(artifactID, Book.Page1);
                case 2: return CreatePages(artifactID, Book.Page2);
                case 3: return CreatePages(artifactID, Book.Page3);
            }
            return null;
        }

        #endregion
    }
}
