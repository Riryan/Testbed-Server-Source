using System;
using Game.Server.Application.Content;
using Game.Server.Domain.Players;
using Game.Shared.Content;
using Game.Shared.Progression;

namespace Game.Server.Application.Progression
{
    public static class SkillUseGainPolicy
    {
        public static bool TryGain(
            GameplayContentCatalog content,
            ProgressionService progression,
            PlayerRuntime runtime,
            ushort trackDataId,
            Func<double> random01 = null)
        {
            if (content == null || progression == null || runtime == null || trackDataId == 0 ||
                !content.TryGetProgressTrack(trackDataId, out ProgressTrackDefinition definition))
                return false;

            CharacterProgressionState state = runtime.CaptureProgressionState();
            ProgressTrackState track = FindTrack(state?.tracks, trackDataId);
            if (track == null || track.mode != ProgressTrackMode.Gain || track.value >= definition.maximumValue)
                return false;

            int basisPoints = ResolveGainBasisPoints(track.value, definition.maximumValue);
            if (basisPoints <= 0) return false;

            double roll = random01 != null ? random01() : Random.Shared.NextDouble();
            if (double.IsNaN(roll) || double.IsInfinity(roll) || roll < 0d || roll >= 1d)
                return false;
            if (roll * 10000d >= basisPoints)
                return false;

            return progression.GainTrack(runtime, trackDataId, 1);
        }

        private static int ResolveGainBasisPoints(int value, int maximum)
        {
            if (maximum <= 0 || value >= maximum) return 0;
            double ratio = Math.Max(0d, Math.Min(1d, value / (double)maximum));
            if (ratio < 0.25d) return 500;
            if (ratio < 0.50d) return 250;
            if (ratio < 0.75d) return 125;
            if (ratio < 0.90d) return 50;
            return 20;
        }

        private static ProgressTrackState FindTrack(ProgressTrackState[] tracks, ushort dataId)
        {
            tracks = tracks ?? Array.Empty<ProgressTrackState>();
            for (int i = 0; i < tracks.Length; ++i)
            {
                ProgressTrackState track = tracks[i];
                if (track != null && track.dataId == dataId)
                    return track;
            }
            return null;
        }
    }
}
