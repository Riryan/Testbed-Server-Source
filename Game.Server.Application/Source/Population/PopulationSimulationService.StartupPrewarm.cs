using System;
using System.Collections.Generic;
using Game.Shared.Population;
using Game.Shared.World;

namespace Game.Server.Application.Population
{
    /// <summary>
    /// One-time GameServer startup preparation for already-created portal-backed Population.
    /// Uses the canonical portal release, organic route placement, and AOI hibernation paths.
    /// The host must not start accepting clients until the caller has completed this pass.
    /// Normal death/portal upkeep remains owned by PopulationSimulationService.
    /// </summary>
    public sealed partial class PopulationSimulationService
    {
        private readonly List<long> _startupPortalPrewarmIds = new List<long>();
        private int _startupPortalPrewarmCursor;
        private bool _startupPortalPrewarmPrepared;
        private int _startupPortalPrewarmSucceeded;
        private int _startupPortalPrewarmDeferred;

        public int StartupPortalPrewarmSucceeded => _startupPortalPrewarmSucceeded;
        public int StartupPortalPrewarmDeferred => _startupPortalPrewarmDeferred;
        public int StartupPortalPrewarmTotal => _startupPortalPrewarmIds.Count;
        public int StartupPortalPrewarmCompleted => _startupPortalPrewarmCursor;
        public bool StartupPortalPrewarmFinished =>
            _startupPortalPrewarmPrepared &&
            _startupPortalPrewarmCursor >= _startupPortalPrewarmIds.Count;

        /// <summary>
        /// Snapshot only portal-owned initial actors from the already-built runtime.
        /// This allocates a small startup-only list, not a polling service or runtime manager.
        /// </summary>
        public void PrepareStartupPortalPrewarm()
        {
            if (_startupPortalPrewarmPrepared)
                throw new InvalidOperationException("Startup portal preparation was already started.");

            _startupPortalPrewarmPrepared = true;
            _startupPortalPrewarmCursor = 0;
            _startupPortalPrewarmSucceeded = 0;
            _startupPortalPrewarmDeferred = 0;
            _startupPortalPrewarmIds.Clear();

            foreach (PopulationActorRuntime pop in _population.Values)
            {
                if (pop?.Actor == null || !pop.Actor.Alive ||
                    !pop.AwaitingPlayerActivation ||
                    pop.AiState != PopulationAiState.PortalDormant)
                    continue;

                if (!TryGetGraph(pop.Actor, out MapGraph graph) ||
                    !graph.Portals.TryGetValue(pop.LastPortalId, out ServerPopulationPortal portal) ||
                    portal.mode == PopulationPortalMode.DespawnOnly)
                    continue;

                _startupPortalPrewarmIds.Add(pop.Actor.Handle.actorId);
            }
        }

        /// <summary>
        /// Runs exactly one initial portal-actor release work unit. Return false once all
        /// initial portal actors have been processed. The caller controls startup pacing.
        /// Startup preparation deliberately bypasses player-proximity and runtime release
        /// cadence and original portal ownership, and uses the canonical ActivatePortalSpawn
        /// method. Authored ambient civilian placement uses its baked spawn-slot validation.
        /// </summary>
        public bool PrewarmNextStartupPortal(out long actorId)
        {
            if (!_startupPortalPrewarmPrepared)
                throw new InvalidOperationException("Call PrepareStartupPortalPrewarm first.");

            actorId = 0;
            if (StartupPortalPrewarmFinished)
                return false;

            actorId = _startupPortalPrewarmIds[_startupPortalPrewarmCursor++];
            if (!_population.TryGetValue(actorId, out PopulationActorRuntime pop) ||
                pop?.Actor == null || !pop.Actor.Alive ||
                !pop.AwaitingPlayerActivation ||
                pop.AiState != PopulationAiState.PortalDormant)
            {
                _startupPortalPrewarmDeferred++;
                Console.Error.WriteLine(
                    $"[PopulationStartup] Actor {actorId} changed before pre-warm; preserving canonical lifecycle.");
                return true;
            }

            if (!TryGetGraph(pop.Actor, out MapGraph graph) ||
                !graph.Portals.TryGetValue(pop.LastPortalId, out ServerPopulationPortal portal))
            {
                _startupPortalPrewarmDeferred++;
                Console.Error.WriteLine(
                    $"[PopulationStartup] Actor {actorId} has no valid source portal {pop.LastPortalId}; " +
                    "left dormant for existing recovery behavior.");
                return true;
            }

            // An authored closed door or dynamic blocker is not a startup-fatal error.
            // Never bypass the canonical safety gate merely to force readiness.
            if (portal.mode == PopulationPortalMode.DespawnOnly ||
                !PortalSpawnClear(graph, portal, pop, Array.Empty<PopulationPlayerView>()))
            {
                _startupPortalPrewarmDeferred++;
                Console.Error.WriteLine(
                    $"[PopulationStartup] Portal {portal.stableId} blocked initial release of actor {actorId}; " +
                    "retaining existing portal-dormant upkeep.");
                return true;
            }

            const double startupTime = 0d;
            if (!ActivatePortalSpawn(graph, pop, portal, startupTime, scheduleImmediately: false))
            {
                _startupPortalPrewarmDeferred++;
                Console.Error.WriteLine(
                    $"[PopulationStartup] Actor {actorId} failed initial portal activation at {portal.stableId}; " +
                    "retaining existing portal-dormant upkeep.");
                return true;
            }

            // Existing organic distribution places the initial actor on its authored route
            // rather than waiting for the first player to force it out of the doorway.
            if (pop.MovementBehavior == SharedAiMovementMode.Route)
                OrganicStartupDistribution(graph, pop, portalSpawn: true);

            // An initial spawn is not a permanently active simulation obligation. Keep the
            // same actor identity, route progress and current position in the existing AOI
            // hibernation index; player approach later uses ordinary WakeImmediately.
            EnterAoiHibernate(pop, startupTime);
            _startupPortalPrewarmSucceeded++;
            return true;
        }
    }
}
