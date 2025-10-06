using System;
using DOL.Events;
using DOL.GS;
using log4net;
using System.Reflection;

namespace DOL.GS.Atlantis
{
    /// <summary>
    /// Optionaler Wrapper, damit alte Referenzen bestehen bleiben.
    /// Ruft einfach den eigentlichen Preloader auf – mit Guard gegen Doppel-Laden.
    /// </summary>
    public static class ArtifactScholarPreloader
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static bool _hooked = false;

        [ScriptLoadedEvent]
        public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)
        {
            if (_hooked) return;
            GameEventMgr.AddHandler(GameServerEvent.Started, OnServerStarted);
            _hooked = true;
        }

        [ScriptUnloadedEvent]
        public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
        {
            GameEventMgr.RemoveHandler(GameServerEvent.Started, OnServerStarted);
            _hooked = false;
        }

        private static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                ArtifactPreloader.BindAllArtifacts(); // idempotent
                var total = 0;
                try
                {
                    var all = ArtifactMgr.GetAllArtifacts();
                    if (all != null) total = System.Linq.Enumerable.Count(all);
                }
                catch { /* not fatal */ }

                log.Info($"ArtifactScholarPreloader: Wiring Complete: Bound {total} Artifacts to scholars");
            }
            catch (Exception ex)
            {
                log.Error("ArtifactScholarPreloader.OnServerStarted failed", ex);
            }
        }
    }
}
