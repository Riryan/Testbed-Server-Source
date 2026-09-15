using System;
using System.Collections.Generic;
using Game.Server.Application.World;
using Game.Shared.World;

namespace Game.Server.Application.Progression
{
    public interface IIncidentObservationProvider
    {
        IncidentObservation Observe(string mapId, string instanceId, WorldPosition position);
    }

    /// <summary>
    /// Event-driven world observation lookup. Baked cameras are indexed once at startup;
    /// no player/camera polling or periodic scan is performed. Dynamic witness systems can
    /// later compose their own observed flag with this camera result at the incident callsite.
    /// </summary>
    public sealed class WorldIncidentObservationService : IIncidentObservationProvider
    {
        private readonly struct Camera
        {
            public WorldPosition Position { get; }
            public float Radius { get; }
            public int Evidence { get; }

            public Camera(WorldPosition position, float radius, int evidence)
            {
                Position = position;
                Radius = radius;
                Evidence = evidence;
            }
        }

        private readonly Dictionary<string, Camera[]> _camerasByWorld = new Dictionary<string, Camera[]>(StringComparer.Ordinal);

        public WorldIncidentObservationService(ServerMapCatalog maps)
        {
            if (maps == null)
                return;

            var building = new Dictionary<string, List<Camera>>(StringComparer.Ordinal);
            foreach (ServerMapSnapshot map in maps.Snapshots)
            {
                if (map == null)
                    continue;
                string key = Key(map.mapId, map.instanceId);
                ServerWorldInteractableDefinition[] interactables = map.interactables ?? Array.Empty<ServerWorldInteractableDefinition>();
                for (int i = 0; i < interactables.Length; ++i)
                {
                    ServerWorldInteractableDefinition value = interactables[i];
                    ServerSurveillanceCameraDefinition camera = value?.surveillanceCamera;
                    if (value == null || camera == null || !camera.enabled || camera.radius <= 0f)
                        continue;
                    if (!building.TryGetValue(key, out List<Camera> list))
                    {
                        list = new List<Camera>();
                        building.Add(key, list);
                    }
                    list.Add(new Camera(value.pose.ToWorldPosition(), Math.Max(0.5f, camera.radius), Math.Max(0, camera.evidence)));
                }
            }

            foreach (KeyValuePair<string, List<Camera>> pair in building)
                _camerasByWorld[pair.Key] = pair.Value.ToArray();
        }

        public IncidentObservation Observe(string mapId, string instanceId, WorldPosition position)
        {
            if (!_camerasByWorld.TryGetValue(Key(mapId, instanceId), out Camera[] cameras) || cameras == null)
                return default;

            bool observed = false;
            int evidence = 0;
            for (int i = 0; i < cameras.Length; ++i)
            {
                Camera camera = cameras[i];
                double dx = position.X - camera.Position.X;
                double dy = position.Y - camera.Position.Y;
                double dz = position.Z - camera.Position.Z;
                double radius = Math.Max(0.5d, camera.Radius);
                if (dx * dx + dy * dy + dz * dz > radius * radius)
                    continue;
                observed = true;
                if (camera.Evidence > evidence)
                    evidence = camera.Evidence;
            }
            return observed ? new IncidentObservation(false, true, evidence) : default;
        }

        private static string Key(string mapId, string instanceId) =>
            (mapId ?? string.Empty).Trim() + "\n" + (instanceId ?? string.Empty).Trim();
    }
}
