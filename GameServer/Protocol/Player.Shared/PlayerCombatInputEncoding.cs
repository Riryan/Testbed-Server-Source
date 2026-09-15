using System;

namespace Player.Shared
{
    /// <summary>
    /// Packs combat hold state plus aim pitch into the existing one-byte movement combat field.
    /// Low two bits are PlayerCombatInputFlags; upper six bits are pitch quantization.
    /// </summary>
    public static class PlayerCombatInputEncoding
    {
        public const float MinimumAimPitchDegrees = -60f;
        public const float MaximumAimPitchDegrees = 60f;
        public const int PitchBits = 6;
        public const int PitchLevels = 1 << PitchBits;
        public const int PitchSteps = PitchLevels - 1;

        private const byte FlagsMask = 0x03;
        private const int PitchShift = 2;

        public static byte Encode(PlayerCombatInputFlags flags, float aimPitchDegrees)
        {
            int pitch = QuantizePitch(aimPitchDegrees);
            return (byte)(((pitch & PitchSteps) << PitchShift) | ((byte)flags & FlagsMask));
        }

        public static PlayerCombatInputFlags DecodeFlags(byte packed) =>
            (PlayerCombatInputFlags)(packed & FlagsMask);

        public static float DecodeAimPitchDegrees(byte packed) =>
            DequantizePitch((packed >> PitchShift) & PitchSteps);

        private static int QuantizePitch(float pitchDegrees)
        {
            float clamped = Math.Max(MinimumAimPitchDegrees, Math.Min(MaximumAimPitchDegrees, pitchDegrees));
            float normalized = (clamped - MinimumAimPitchDegrees) /
                               (MaximumAimPitchDegrees - MinimumAimPitchDegrees);
            return Math.Max(0, Math.Min(PitchSteps,
                (int)Math.Round(normalized * PitchSteps, MidpointRounding.AwayFromZero)));
        }

        private static float DequantizePitch(int quantized)
        {
            int q = Math.Max(0, Math.Min(PitchSteps, quantized));
            return MinimumAimPitchDegrees +
                   (MaximumAimPitchDegrees - MinimumAimPitchDegrees) * (q / (float)PitchSteps);
        }
    }
}
