using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Events;
using DOL.GS;
using DOL.GS.Quests.Atlantis;
using log4net;

namespace DOL.GS.Atlantis
{
    /// <summary>
    /// Lädt/verkabelt alle Artifact-Quests beim Serverstart (nicht beim ersten Scholar-Click).
    /// </summary>
    public static class ArtifactPreloader
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static bool _boundOnce = false;
        private static readonly object _lock = new();

        // --- Type-Index für schnelle Auflösung von QuestIDs -> Type ---
        private static bool _typeIndexBuilt = false;
        private static readonly object _typeIdxLock = new();
        private static readonly Dictionary<string, Type> _byFull = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Type> _bySimple = new(StringComparer.OrdinalIgnoreCase);

        [ScriptLoadedEvent]
        public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)
        {
            // nach GameServer-Start binden (NPCs/Regionen sind dann da)
            GameEventMgr.AddHandler(GameServerEvent.Started, OnServerStarted);
        }

        [ScriptUnloadedEvent]
        public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
        {
            GameEventMgr.RemoveHandler(GameServerEvent.Started, OnServerStarted);
        }

        private static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                BindAllArtifacts(); // idempotent
            }
            catch (Exception ex)
            {
                log.Error("ArtifactPreloader.OnServerStarted failed", ex);
            }
        }

        /// <summary>
        /// Von außen aufrufbar (z. B. ArtifactScholarPreloader), aber intern idempotent.
        /// </summary>
        public static void BindAllArtifacts()
        {
            lock (_lock)
            {
                if (_boundOnce) return;

                BuildQuestTypeIndex();

                int wired = 0;
                var notFound = new List<string>();

                IEnumerable<DbArtifact> allArtifacts;
                try
                {
                    allArtifacts = ArtifactMgr.GetAllArtifacts() ?? Enumerable.Empty<DbArtifact>();
                }
                catch (Exception ex)
                {
                    log.Error("ArtifactPreloader: ArtifactMgr.GetAllArtifacts() failed.", ex);
                    return;
                }

                foreach (var art in allArtifacts)
                {
                    if (art == null) continue;
                    if (string.IsNullOrWhiteSpace(art.ArtifactID)) continue;

                    // Viele DBs haben in DbArtifact.QuestID entweder FullName, SimpleName oder SimpleName ohne 'Quest'
                    var questType = ResolveQuestType(art.QuestID);
                    if (questType == null)
                    {
                        notFound.Add($"{art.ArtifactID} :: {art.QuestID}");
                        continue;
                    }

                    try
                    {
                        // ArtifactQuest kennt die Scholar-Zuordnung und ruft intern AddQuestToGive(..) auf
                        ArtifactQuest.Init(art.ArtifactID, questType);
                        wired++;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"ArtifactPreloader: Init failed for {art.ArtifactID} ({art.QuestID}).", ex);
                    }
                }

                if (notFound.Count > 0)
                {
                    // Kurzes Sample loggen, damit man in der DB die QuestID-Strings korrigieren kann
                    var sample = string.Join(", ", notFound.Take(6));
                    log.Warn($"ArtifactPreloader: {notFound.Count} QuestIDs konnten nicht zu Typen aufgelöst werden. Beispiele: {sample}");
                }

                log.Info($"ArtifactPreloader: Wiring complete – {wired} ArtifactQuests gebunden (beim Serverstart).");
                _boundOnce = true;
            }
        }

        // ================= helpers =================

        private static void BuildQuestTypeIndex()
        {
            lock (_typeIdxLock)
            {
                if (_typeIndexBuilt) return;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null || !t.IsClass || t.IsAbstract) continue;
                        var ns = t.Namespace ?? "";

                        // Wir wollen nur Quest-Klassen, bevorzugt aus Atlantis-Namespace
                        if (!ns.Contains(".Quests.", StringComparison.Ordinal)) continue;

                        if (!string.IsNullOrEmpty(t.FullName))
                            _byFull[t.FullName] = t;
                        if (!_bySimple.ContainsKey(t.Name))
                            _bySimple[t.Name] = t;
                    }
                }

                _typeIndexBuilt = true;
            }
        }

        private static Type ResolveQuestType(string questIdFromDb)
        {
            if (string.IsNullOrWhiteSpace(questIdFromDb)) return null;

            // 1) full name
            if (_byFull.TryGetValue(questIdFromDb, out var t1))
                return t1;

            // 2) simple name
            if (_bySimple.TryGetValue(questIdFromDb, out var t2))
                return t2;

            // 3) evtl. fehlt das 'Quest'-Suffix
            var withQuest = questIdFromDb.EndsWith("Quest", StringComparison.OrdinalIgnoreCase)
                ? questIdFromDb
                : questIdFromDb + "Quest";

            if (_byFull.TryGetValue(withQuest, out var t3))
                return t3;
            if (_bySimple.TryGetValue(withQuest, out var t4))
                return t4;

            // 4) DB liefert evtl. FullName ohne passenden Namespace -> SimpleName extrahieren
            var simple = questIdFromDb.Split('.').Last();
            if (_bySimple.TryGetValue(simple, out var t5))
                return t5;

            if (!simple.EndsWith("Quest", StringComparison.OrdinalIgnoreCase))
            {
                var simpleQ = simple + "Quest";
                if (_bySimple.TryGetValue(simpleQ, out var t6))
                    return t6;
            }

            return null;
        }
    }
}
