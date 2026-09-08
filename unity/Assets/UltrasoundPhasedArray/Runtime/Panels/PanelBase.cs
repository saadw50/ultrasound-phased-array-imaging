using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// A text label positioned in normalised panel space, drawn by the console with IMGUI.
    /// Panels rasterise graphics only, which keeps them free of fonts and materials.
    /// </summary>
    public struct PanelLabel
    {
        /// <summary>Position in the panel, (0,0) bottom left and (1,1) top right.</summary>
        public Vector2 normalised;
        public string text;
        public Color color;
        public TextAnchor anchor;
        public int fontSize;
    }

    /// <summary>Shared palette and buffer handling for the instrument panels.</summary>
    public abstract class PanelBase : IDisposable
    {
        public static readonly Color32 Background = new Color32(0x0B, 0x0F, 0x14, 0xFF);
        public static readonly Color32 Grid = new Color32(0x18, 0x23, 0x2E, 0xFF);
        public static readonly Color32 GridStrong = new Color32(0x27, 0x36, 0x45, 0xFF);
        public static readonly Color32 Axis = new Color32(0x45, 0x59, 0x6C, 0xFF);
        public static readonly Color32 Border = new Color32(0x1E, 0x2A, 0x36, 0xFF);
        public static readonly Color32 Trace = new Color32(0x6F, 0xD6, 0xFF, 0xFF);
        public static readonly Color32 TraceFaint = new Color32(0x35, 0x5C, 0x74, 0xFF);
        public static readonly Color32 Accent = new Color32(0x46, 0xD3, 0xA0, 0xFF);
        public static readonly Color32 Warn = new Color32(0xFF, 0x7A, 0x45, 0xFF);
        public static readonly Color32 Sector = new Color32(0x14, 0x2A, 0x38, 0xFF);
        public static readonly Color TextColor = new Color(0.78f, 0.83f, 0.88f, 1f);
        public static readonly Color TextDim = new Color(0.48f, 0.55f, 0.62f, 1f);
        public static readonly Color TextWarn = new Color(1f, 0.48f, 0.27f, 1f);
        public static readonly Color TextAccent = new Color(0.27f, 0.83f, 0.63f, 1f);

        protected TexturePainter Painter;

        public readonly List<PanelLabel> Labels = new List<PanelLabel>();

        public string Title = "";

        /// <summary>One line under the title explaining what the panel shows.</summary>
        public string Subtitle = "";

        public Texture2D Texture { get { return Painter != null ? Painter.Texture : null; } }
        public int Width { get { return Painter != null ? Painter.Width : 0; } }
        public int Height { get { return Painter != null ? Painter.Height : 0; } }

        /// <summary>Allocates or resizes the buffer. Returns true when the size changed.</summary>
        protected bool EnsureSize(int width, int height)
        {
            if (Painter == null)
            {
                Painter = new TexturePainter(width, height);
                return true;
            }
            return Painter.Resize(width, height);
        }

        protected void AddLabel(float normalisedX, float normalisedY, string text,
                                Color color, TextAnchor anchor = TextAnchor.MiddleLeft,
                                int fontSize = 0)
        {
            if (string.IsNullOrEmpty(text)) return;
            Labels.Add(new PanelLabel
            {
                normalised = new Vector2(normalisedX, normalisedY),
                text = text,
                color = color,
                anchor = anchor,
                fontSize = fontSize,
            });
        }

        protected void AddPixelLabel(float pixelX, float pixelY, string text, Color color,
                                     TextAnchor anchor = TextAnchor.MiddleLeft, int fontSize = 0)
        {
            if (Width <= 0 || Height <= 0) return;
            AddLabel(pixelX / Width, pixelY / Height, text, color, anchor, fontSize);
        }

        protected void DrawBorder()
        {
            Painter.Rect(0, 0, Width, Height, Border);
        }

        public void Dispose()
        {
            if (Painter != null) Painter.Release();
            Painter = null;
        }
    }
}
