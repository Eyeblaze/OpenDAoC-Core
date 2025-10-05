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
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.Quests;
using DOL.GS.Quests.Atlantis; // ArtifactQuest, ArtifactTurnInQuest
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

        public Scholar() : base() { }

        /// <summary>
        /// Interact with scholar.
        /// </summary>
        public override bool Interact(GamePlayer player)
        {
            if (!base.Interact(player)) return false;

            IList quests = QuestListToGive;
            int count = 0;
            string artifacts = "";

            if (quests != null && quests.Count > 0)
            {
                lock (quests.SyncRoot)
                {
                    int numQuests = quests.Count;
                    foreach (ArtifactQuest quest in quests)
                    {
                        // Wenn der Spieler diese Quest gerade macht und Input nötig ist:
                        ArtifactQuest playerQuest = (ArtifactQuest)player.IsDoingQuest(quest.GetType());
                        if (playerQuest != null)
                        {
                            if (playerQuest.Interact(this, player))
                                return true;
                        }

                        // Nur Artefakte auflisten, die der Spieler von der Klasse her annehmen kann
                        if (player.CanReceiveArtifact(quest.ArtifactID))
                        {
                            if (count > 0 && numQuests < quests.Count)
                                artifacts += (numQuests == 1) ? ", or " : ", ";

                            artifacts += $"[{quest.ArtifactID}]";
                            ++count;
                        }

                        --numQuests;
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
                intro = $"Which artifact may I assist you with, {player.Name}? " +
                        $"I study the lore and magic of the following artifacts: {artifacts}.";
            }

            SayTo(player, eChatLoc.CL_PopupWindow, intro);

            string follow = string.Format(
                "{0}, did you find any of the stories that chronicle the powers of the artifacts? " +
                "We can unlock the powers of these artifacts by studying the stories. " +
                "I can take the story and unlock the artifact's magic.",
                player.Name);

            SayTo(player, eChatLoc.CL_PopupWindow, follow);
            return true;
        }



        /// <summary>
        /// Talk to the scholar.
        /// </summary>
        public override bool WhisperReceive(GameLiving source, string text)
        {
            if (!base.WhisperReceive(source, text))
                return false;

            GamePlayer player = source as GamePlayer;
            if (player == null)
                return false;

            // NEU: wenn gerade eine ArtifactTurnInQuest auf Auswahl wartet, leite dorthin weiter
            var turnIn = player.IsDoingQuest(typeof(DOL.GS.Quests.Atlantis.ArtifactTurnInQuest)) as DOL.GS.Quests.Atlantis.ArtifactTurnInQuest;
            if (turnIn != null)
            {
                // Wenn die Turn-In-Quest die Eingabe verarbeitet hat, war's das.
                if (turnIn.WhisperReceive(player, this, text))
                    return false;
            }

            lock (QuestListToGive.SyncRoot)
            {
                // Start new quest...
                foreach (ArtifactQuest quest in QuestListToGive)
                {
                    if (text.Equals(quest.ArtifactID, StringComparison.OrdinalIgnoreCase))
                    {
                        if (quest.CheckQuestQualification(player))
                        {
                            if (player.CanReceiveArtifact(quest.ArtifactID))
                            {
                                GiveArtifactQuest(player, quest.GetType());
                            }
                            else
                            {
                                RefuseArtifact(player);
                            }
                        }
                        else
                        {
                            DenyArtifactQuest(player, quest.ReasonFailQualification);
                        }
                        return false;
                    }
                }

                // ...or continuing a quest?
                foreach (AbstractQuest quest in player.DataQuestList)
                {
                    if (quest is ArtifactQuest && (HasQuest(quest.GetType()) != null))
                        if ((quest as ArtifactQuest).WhisperReceive(player, this, text))
                            return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Deny a quest to a player.
        /// </summary>
        private void DenyArtifactQuest(GamePlayer player, string reason)
        {
            if (player != null)
            {
                string reply = string.Format(
                    "{0} I cannot activate that artifact for you. {1} {2} {3} {4} {5} \n\nHint: {6}",
                    player.Name,
                    "This could be because you have already activated it, or you are in the",
                    "process of activating it, or you may not have completed everything",
                    "you need to do. Remember that the activation process requires you to",
                    "have credit for the artifact's encounter, as well as the artifact's",
                    "complete book of scrolls.",
                    reason);

                TurnTo(player);
                SayTo(player, eChatLoc.CL_PopupWindow, reply);
            }
        }

        /// <summary>
        /// This is used when the player is ready to receive the artifact,
        /// but is not of a class who can accept this artifact.
        /// </summary>
        private void RefuseArtifact(GamePlayer player)
        {
            if (player != null)
            {
                string reply = "I'm sorry, but I shouldn't recreate this artifact for you, " +
                               "as it wouldn't make proper use of your abilities. There are other artifacts " +
                               "in Atlantis better suited to your needs.\n\n" +
                               "If you feel like your class qualifies for this artifact please /report this error to my superiors.";
                TurnTo(player);
                SayTo(player, eChatLoc.CL_PopupWindow, reply);
            }
        }

        /// <summary>
        /// Give the artifact quest to the player.
        /// </summary>
        private void GiveArtifactQuest(GamePlayer player, Type questType)
        {
            if (player == null || questType == null)
                return;

            ArtifactQuest quest = (ArtifactQuest)Activator.CreateInstance(questType, new object[] { player });
            if (quest == null)
                return;

            player.AddQuest(quest);
            quest.WhisperReceive(player, this, quest.ArtifactID);
        }

        /// <summary>
        /// Invoked when scholar receives an item (encounter credit token or finished book).
        /// </summary>
        public override bool ReceiveItem(GameLiving source, DbInventoryItem item)
        {
            var player = source as GamePlayer;
            if (player == null || item == null)
                return base.ReceiveItem(source, item);

            // === 1) Encounter-Credit-Token? (z. B. "Maddening Scalars Credit") =========================
            try
            {
                if (ArtifactMgr.GrantArtifactBountyCredit(player, item.Name))
                {
                    // Token entfernen + optional logging
                    player.Inventory.RemoveItem(item);
                    // InventoryLogging.LogInventoryAction(player, this, eInventoryActionType.Merchant, item.Template, item.Count);
                    SayTo(player, eChatLoc.CL_SystemWindow, "Your Artifact encounter credit has been granted & registered.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                log.Error("Scholar ReceiveItem: error while processing encounter credit token.", ex);
            }

            // === 2) Buch-Übergabe (fertiges Buch) ======================================================
            try
            {
                // 1) Erkennt das Buch: Id_nb oder Name (beides in ArtifactBookMap hinterlegt)
                if (DOL.GS.Atlantis.ArtifactBookMap.TryResolveByName(item?.Name, out var artifactId))
                {
                    bool handled = DOL.GS.Quests.Atlantis.ArtifactTurnInQuest.BeginTurnInFromBook(
                                       source as GamePlayer, this, artifactId, item);
                    if (handled) return true;
                }
                else
                {
                    // DBG-Hinweis hilft, Namen korrekt in die Map zu übernehmen
                    SayTo(source as GamePlayer, eChatLoc.CL_SystemWindow,
                        $"(DBG) BookName='{item?.Name}'. I couldn't link it to any artifact. Please /report this.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                log.Error("Scholar.ReceiveItem (book) failed", ex);
                SayTo(source as GamePlayer, eChatLoc.CL_PopupWindow, "Something went wrong with that book. Please try again.");
                return true;
            }


            // === 3) Standardfluss: laufende ArtifactQuest ihre ReceiveItem verarbeiten lassen ==========
            lock (QuestListToGive.SyncRoot)
            {
                try
                {
                    foreach (AbstractQuest q in player.DataQuestList)
                    {
                        if (q is ArtifactQuest && (HasQuest(q.GetType()) != null))
                        {
                            if ((q as ArtifactQuest).ReceiveItem(player, this, item))
                                return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Scholar ReceiveItem Error (delegating to ArtifactQuest): ", ex);
                    SayTo(player, eChatLoc.CL_PopupWindow, "I'm very sorry but I'm having trouble locating an artifact for you. Please /report this problem to my superiors.");
                    return true;
                }
            }

            // nichts davon hat gegriffen -> Basis
            return base.ReceiveItem(source, item);
        }
    }
}
