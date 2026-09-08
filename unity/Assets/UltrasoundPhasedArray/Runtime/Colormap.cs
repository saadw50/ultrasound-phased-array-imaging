using UnityEngine;

namespace Ultrasound.PhasedArray
{
    public enum ColormapKind
    {
        Grayscale = 0,
        Inferno = 1,
        Viridis = 2,
        Amber = 3,
        /// <summary>Blue-white-red, for signed data such as instantaneous pressure.</summary>
        Diverging = 4,
    }

    /// <summary>256-entry colour lookup tables built from a handful of anchor colours.</summary>
    public sealed class Colormap
    {
        public const int Resolution = 256;

        readonly Color32[] _table = new Color32[Resolution];
        ColormapKind _kind = (ColormapKind)(-1);

        public ColormapKind Kind { get { return _kind; } }

        public Colormap(ColormapKind kind)
        {
            Rebuild(kind);
        }

        public void Rebuild(ColormapKind kind)
        {
            if (_kind == kind) return;
            _kind = kind;

            Color[] anchors = Anchors(kind);
            for (int i = 0; i < Resolution; i++)
            {
                float t = i / (float)(Resolution - 1) * (anchors.Length - 1);
                int index = Mathf.Min((int)t, anchors.Length - 2);
                Color color = Color.Lerp(anchors[index], anchors[index + 1], t - index);
                _table[i] = new Color32(
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f),
                    255);
            }
        }

        /// <summary>Maps a normalised 0..1 value to a colour.</summary>
        public Color32 Evaluate(float normalised)
        {
            int index = (int)(normalised * (Resolution - 1) + 0.5f);
            if (index < 0) index = 0;
            else if (index >= Resolution) index = Resolution - 1;
            return _table[index];
        }

        /// <summary>Maps a signed -1..1 value through a diverging table centred on zero.</summary>
        public Color32 EvaluateSigned(float signed)
        {
            return Evaluate(0.5f * (Mathf.Clamp(signed, -1f, 1f) + 1f));
        }

        public Color32[] Table { get { return _table; } }

        static Color[] Anchors(ColormapKind kind)
        {
            switch (kind)
            {
                case ColormapKind.Inferno:
                    return new[]
                    {
                        new Color(0.001f, 0.000f, 0.014f),
                        new Color(0.159f, 0.044f, 0.329f),
                        new Color(0.397f, 0.083f, 0.433f),
                        new Color(0.624f, 0.165f, 0.388f),
                        new Color(0.831f, 0.283f, 0.259f),
                        new Color(0.955f, 0.480f, 0.084f),
                        new Color(0.984f, 0.723f, 0.180f),
                        new Color(0.988f, 0.998f, 0.645f),
                    };

                case ColormapKind.Viridis:
                    return new[]
                    {
                        new Color(0.267f, 0.005f, 0.329f),
                        new Color(0.283f, 0.141f, 0.458f),
                        new Color(0.254f, 0.265f, 0.530f),
                        new Color(0.207f, 0.372f, 0.553f),
                        new Color(0.164f, 0.471f, 0.558f),
                        new Color(0.128f, 0.567f, 0.551f),
                        new Color(0.135f, 0.659f, 0.518f),
                        new Color(0.267f, 0.749f, 0.441f),
                        new Color(0.478f, 0.821f, 0.318f),
                        new Color(0.741f, 0.873f, 0.150f),
                        new Color(0.993f, 0.906f, 0.144f),
                    };

                case ColormapKind.Amber:
                    return new[]
                    {
                        new Color(0.020f, 0.020f, 0.031f),
                        new Color(0.180f, 0.090f, 0.020f),
                        new Color(0.450f, 0.220f, 0.030f),
                        new Color(0.780f, 0.450f, 0.070f),
                        new Color(0.980f, 0.760f, 0.300f),
                        new Color(1.000f, 0.980f, 0.850f),
                    };

                case ColormapKind.Diverging:
                    // Dark at zero so still water reads as background, bright at both
                    // extremes so compression and rarefaction are equally visible.
                    return new[]
                    {
                        new Color(0.60f, 0.85f, 1.00f),
                        new Color(0.20f, 0.48f, 0.88f),
                        new Color(0.07f, 0.15f, 0.34f),
                        new Color(0.03f, 0.03f, 0.05f),
                        new Color(0.34f, 0.12f, 0.07f),
                        new Color(0.88f, 0.38f, 0.12f),
                        new Color(1.00f, 0.88f, 0.60f),
                    };

                default:
                    return new[] { Color.black, Color.white };
            }
        }
    }
}
