using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticPathFinding.Systems
{
    /// <summary>
    /// Forces lane specification rebuild when bus lane penalty setting changes.
    /// This makes BusLanePatches effective immediately without touching roads.
    /// </summary>
    public sealed partial class BusLanePenaltyRefreshSystem : GameSystemBase
    {
        private EntityQuery _carLaneQ;
        private EntityQuery _carLaneNeedsPathfindUpdatedQ;
        private float _lastPenaltySec;
        private bool _initialized;
        private bool _pendingRefresh;

        protected override void OnCreate()
        {
            base.OnCreate();

            _carLaneQ = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Lane>(),
                    ComponentType.ReadOnly<CarLane>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<SlaveLane>()
                }
            });

            _carLaneNeedsPathfindUpdatedQ = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Lane>(),
                    ComponentType.ReadOnly<CarLane>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<SlaveLane>(),
                    ComponentType.ReadOnly<PathfindUpdated>()
                }
            });

            RequireForUpdate(_carLaneQ);

            if (Mod.m_Setting != null)
                Mod.m_Setting.onSettingsApplied += OnSettingsApplied;

            // One initial pass so current save/game state picks up the current slider value.
            _pendingRefresh = true;
            Enabled = true;
        }

        protected override void OnDestroy()
        {
            if (Mod.m_Setting != null)
                Mod.m_Setting.onSettingsApplied -= OnSettingsApplied;

            base.OnDestroy();
        }

        private void OnSettingsApplied(Game.Settings.Setting _)
        {
            float sec = math.max(0f, Mod.m_Setting?.nonbus_buslane_penalty_sec ?? 0f);

            if (!_initialized || math.abs(sec - _lastPenaltySec) >= 1e-4f)
            {
                _pendingRefresh = true;
                Enabled = true;
            }

            _lastPenaltySec = sec;
            _initialized = true;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            // Event-driven: run on next simulation tick after Enabled is set by onSettingsApplied.
            return 1;
        }

        protected override void OnUpdate()
        {
            if (!_pendingRefresh)
            {
                Enabled = false;
                return;
            }

            if (!_initialized)
            {
                _lastPenaltySec = math.max(0f, Mod.m_Setting?.nonbus_buslane_penalty_sec ?? 0f);
                _initialized = true;
            }

            int marked = _carLaneNeedsPathfindUpdatedQ.CalculateEntityCount();
            if (marked > 0)
            {
                EntityManager.AddComponent<PathfindUpdated>(_carLaneNeedsPathfindUpdatedQ);
                Mod.log.Info($"[RPF] BusLanePenaltyRefreshSystem: marked {marked} car lanes for pathfind refresh (penalty={_lastPenaltySec:F1}s).");
            }

            _pendingRefresh = false;
            Enabled = false;
        }
    }
}
