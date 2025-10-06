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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.Quests;
using DOL.GS.Quests.Atlantis;
using log4net;

namespace DOL.GS
{
    /// <summary>
    /// The scholars handing out the artifacts.
    /// </summary>
    /// <author>Aredhel</author>
    public class Scholar : Researcher
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// DEBUG/Utility: zeigt alle Artefakte ungefiltert an, wenn true.
        /// </summary>
        public static bool ShowAll = true;

        public Scholar() : base() { }

        // ===== Helpers =====

        /// <summary>
        /// Prüft, ob ein Spieler die Quest annehmen darf.
        /// Sucht statische Methoden CheckQuestQualification(GamePlayer) oder CanGiveQuest(GamePlayer).
        /// </summary>
        private static bool IsEligibleForQuest(Type questType, GamePlayer player)
        {
            if (questType == null || player == null) return false;

            var m1 = questType.GetMethod("CheckQuestQualification", BindingFlags.Public | BindingFlags.Static);
            if (m1 != null)
            {
                try { return (bool)m1.Invoke(null, new object[] { player }); }
                catch { /* ignore */ }
            }

            var m2 = questType.GetMethod("CanGiveQuest", BindingFlags.Public | BindingFlags.Static);
            if (m2 != null)
            {
                try { return (bool)m2.Invoke(null, new object[] { player }); }
                catch { /* ignore */ }
            }

            // Fallback: wenn keine Prüfmethode existiert, für die Anzeige erlauben
            return true;
        }

        /// <summary>
        /// Mappt einen Quest-Typ zu einer ArtifactID anhand der DB-Einträge (ArtifactMgr.GetAllArtifacts()).
        /// </summary>
        private static string ResolveArtifactIdForQuestType(Type questType)
        {
            if (questType == null) return null;

            string full = questType.FullName ?? string.Empty;
            string simple = questType.Name ?? string.Empty;
            string simpleNoQuest = simple.EndsWith("Quest", StringComparison.OrdinalIgnoreCase)
                ? simple.Substring(0, simple.Length - "Quest".Length)
                : simple;

            var all = ArtifactMgr.GetAllArtifacts(); // liefert IEnumerable<DbArtifact>
            if (all == null) return null;

            foreach (var art in all)
            {
                if (art == null || string.IsNullOrWhiteSpace(art.QuestID)) continue;

                if (string.Equals(art.QuestID, full, StringComparison.Ordinal)
                    || string.Equals(art.QuestID, simple, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(art.QuestID, simpleNoQuest, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(art.QuestID + "Quest", simple, StringComparison.OrdinalIgnoreCase))
                {
                    return art.ArtifactID;
                }
            }

            return null;
        }

        private void DenyArtifactQuest(GamePlayer player, string reason)
        {
            if (player == null) return;

            string reply = string.Format(
                "{0} I cannot activate that artifact for you. {1} {2} {3} {4} {5} \n\nHint: {6}",
                player.Name,
                "This could be because you have already activated it, or you are in the",
                "process of activating it, or you may not have completed everything",
                "you need to do. Remember that the activation process requires you to",
                "have credit for the artifact's encounter, as well as the artifact's",
                "complete book of scrolls.",
                reason ?? "Qualification check failed."
            );

            TurnTo(player);
            SayTo(player, eChatLoc.CL_PopupWindow, reply);
        }

        private void RefuseArtifact(GamePlayer player)
        {
            if (player == null) return;

            string reply = string.Format(
                "I'm sorry, but I shouldn't recreate this artifact for you, {0} {1} {2}",
                "as it wouldn't make proper use of your abilities. There are other artifacts",
                "in Atlantis better suited to your needs.",
                "\n\nIf you feel like your class qualifies for this artifact please /report this error to my superiors."
            );

            TurnTo(player);
            SayTo(player, eChatLoc.CL_PopupWindow, reply);
        }

        private void GiveArtifactQuest(GamePlayer player, Type questType)
        {
            if (player == null || questType == null) return;

            ArtifactQuest quest = (ArtifactQuest)Activator.CreateInstance(questType, new object[] { player });
            if (quest == null) return;

            player.AddQuest(quest);
            // üblichen Dialogfluss starten
            quest.WhisperReceive(player, this, quest.ArtifactID);
        }

        // ===== Interact / Listing =====

        public override bool Interact(GamePlayer player)
        {
            if (!base.Interact(player)) return false;

            IList toGive = this.QuestListToGive;
            int count = 0;
            var items = new List<string>();

            bool showAll = Scholar.ShowAll;

            if (toGive != null && toGive.Count > 0)
            {
                lock (toGive.SyncRoot)
                {
                    foreach (var entry in toGive)
                    {
                        Type qType = null;
                        string artifactId = null;

                        if (entry is ArtifactQuest aq)
                        {
                            qType = aq.GetType();
                            artifactId = aq.ArtifactID;
                        }
                        else if (entry is Type t)
                        {
                            qType = t;
                            artifactId = ResolveArtifactIdForQuestType(t);
                        }

                        if (qType == null || string.IsNullOrEmpty(artifactId))
                            continue;

                        // Falls Spieler die Quest gerade macht, die Quest interagieren lassen
                        var playerQuest = player.IsDoingQuest(qType) as ArtifactQuest;
                        if (playerQuest != null)
                        {
                            if (playerQuest.Interact(this, player))
                                return true;
                        }

                        if (showAll || player.CanReceiveArtifact(artifactId))
                        {
                            items.Add($"[{artifactId}]");
                            count++;
                        }
                    }
                }
            }

            string intro;
            if (count == 0)
            {
                intro = "I have no artifacts available for your class.";
            }
            else
            {
                string list = string.Join(", ", items);
                intro = $"Which artifact may I assist you with, {player.Name}? I study the lore and magic of the following artifacts: {list}.";
            }

            SayTo(player, eChatLoc.CL_PopupWindow, intro);

            string follow = $"{player.Name}, did you find any of the stories that chronicle the powers of the artifacts? " +
                            "We can unlock the powers of these artifacts by studying the stories. " +
                            "I can take the story and unlock the artifact's magic.";
            SayTo(player, eChatLoc.CL_PopupWindow, follow);

            return true;
        }

        // ===== Whisper / Start der ArtifactQuest =====

        public override bool WhisperReceive(GameLiving source, string text)
        {
            if (!base.WhisperReceive(source, text))
                return false;

            GamePlayer player = source as GamePlayer;
            if (player == null)
                return false;

            string lower = text?.Trim()?.ToLowerInvariant() ?? string.Empty;

            lock (QuestListToGive.SyncRoot)
            {
                // Variante 1: Instanzen
                foreach (var entry in QuestListToGive)
                {
                    if (entry is ArtifactQuest aq)
                    {
                        if (string.Equals(lower, aq.ArtifactID.ToLowerInvariant()))
                        {
                            if (aq.CheckQuestQualification(player))
                            {
                                if (Scholar.ShowAll || player.CanReceiveArtifact(aq.ArtifactID))
                                {
                                    GiveArtifactQuest(player, aq.GetType());
                                }
                                else
                                {
                                    RefuseArtifact(player);
                                }
                            }
                            else
                            {
                                DenyArtifactQuest(player, aq.ReasonFailQualification);
                            }
                            return false;
                        }
                    }
                }

                // Variante 2: Typen
                foreach (var entry in QuestListToGive)
                {
                    if (entry is Type t)
                    {
                        var artifactId = ResolveArtifactIdForQuestType(t);
                        if (string.IsNullOrEmpty(artifactId)) continue;

                        if (string.Equals(lower, artifactId.ToLowerInvariant()))
                        {
                            bool eligible = IsEligibleForQuest(t, player);
                            if (eligible)
                            {
                                if (Scholar.ShowAll || player.CanReceiveArtifact(artifactId))
                                {
                                    GiveArtifactQuest(player, t);
                                }
                                else
                                {
                                    RefuseArtifact(player);
                                }
                            }
                            else
                            {
                                DenyArtifactQuest(player, "You may be missing the prerequisites.");
                            }
                            return false;
                        }
                    }
                }

                // Laufende Quest weitermachen?
                foreach (AbstractQuest quest in player.DataQuestList)
                {
                    if (quest is ArtifactQuest && (HasQuest(quest.GetType()) != null))
                    {
                        if ((quest as ArtifactQuest).WhisperReceive(player, this, text))
                            return false;
                    }
                }
            }

            return true;
        }

        // ===== ReceiveItem: Encounter-Credit & Buch-Turn-In =====

        public override bool ReceiveItem(GameLiving source, DbInventoryItem item)
        {
            GamePlayer player = source as GamePlayer;
            if (player == null || item == null)
                return false;

            lock (QuestListToGive.SyncRoot)
            {
                try
                {
                    // 1) Erst laufende ArtifactQuest probieren
                    foreach (AbstractQuest quest in player.DataQuestList)
                    {
                        if (quest is ArtifactQuest && (HasQuest(quest.GetType()) != null))
                        {
                            if ((quest as ArtifactQuest).ReceiveItem(player, this, item))
                                return true;
                        }
                    }

                    // 2) Encounter-Credit-Token?
                    string creditName = item.Name ?? string.Empty;
                    if (ArtifactMgr.GrantArtifactBountyCredit(player, creditName))
                    {
                        player.Inventory.RemoveItem(item);
                        SayTo(player, eChatLoc.CL_SystemWindow, "Your encounter credit has been recorded.");
                        return true;
                    }

                    // 3) Buch-Turn-In (ArtifactTurnInQuest übernimmt Validierung/Entfernen)
                    if (ArtifactTurnInQuest.BeginTurnInFromBook(player, this, null, item))
                        return true;
                }
                catch (Exception ex)
                {
                    log.Error("Scholar ReceiveItem Error: ", ex);
                    SayTo(player, eChatLoc.CL_PopupWindow,
                        "I'm very sorry but I'm having trouble locating an artifact for you. Please /report this problem to my superiors.");
                }
            }

            return false;
        }
    }
}
