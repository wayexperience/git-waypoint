using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    // Design tokens for the Git for Unity UI Toolkit window, taken straight from the design mockups so every
    // tab stays visually consistent. Colours are the exact hexes from the foundation sheet; a couple of
    // helpers build the reusable bits (status badge, chip) the same way everywhere.
    static class GitForUnityTheme
    {
        // Surfaces
        public static readonly Color Window   = Hex(0x0E0E10);
        public static readonly Color Panel    = Hex(0x1D1D1F);
        public static readonly Color Elevated = Hex(0x262628);
        public static readonly Color Hover    = Hex(0x2F2F33);
        public static readonly Color Border   = Hex(0x34343A);

        // Text inputs: a fill darker than any surface plus a clearly visible border, so fields read
        // as editable on every background (window, panel, banner).
        public static readonly Color Field       = Hex(0x121214);
        public static readonly Color FieldBorder = Hex(0x4A4A52);

        // Accent + semantic states
        public static readonly Color Accent   = Hex(0x4DA3FF); // Unity primary blue (brand kit)
        public static readonly Color UpToDate = Hex(0x46C68A);
        public static readonly Color Outdated = Hex(0xE0A33B);
        public static readonly Color Conflict = Hex(0xE25C54);
        public static readonly Color Renamed  = Hex(0x8B9BFF);

        // Text
        public static readonly Color Text     = Hex(0xE6E6E8);
        public static readonly Color Subdued  = Hex(0x8A8A90);

        static Font monoFont;
        static bool monoTried;

        // A real monospace face for path/hash/branch, per the mockups, without bundling a font: pull one
        // from the OS. Falls back to the default editor font if the OS font can't be created.
        public static Font Mono
        {
            get
            {
                if (!monoTried)
                {
                    monoTried = true;
                    try { monoFont = Font.CreateDynamicFontFromOSFont(new[] { "Menlo", "Consolas", "Courier New" }, 12); }
                    catch { monoFont = null; }
                }
                return monoFont;
            }
        }

        public static Color Hex(int rgb)
        {
            return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        }

        // The little square diff badge (M/A/D/R/?) with its semantic colour.
        public static void DiffBadge(GitFileStatus status, out string letter, out Color color)
        {
            switch (status)
            {
                case GitFileStatus.Added: case GitFileStatus.Untracked: letter = "A"; color = UpToDate; break;
                case GitFileStatus.Modified: case GitFileStatus.TypeChange: letter = "M"; color = Outdated; break;
                case GitFileStatus.Deleted: letter = "D"; color = Conflict; break;
                case GitFileStatus.Renamed: case GitFileStatus.Copied: letter = "R"; color = Renamed; break;
                case GitFileStatus.Unmerged: letter = "C"; color = Conflict; break;
                default: letter = "?"; color = Subdued; break;
            }
        }

        // ---- reusable element builders ----

        public static Label BadgeSquare(string text, Color color)
        {
            var l = new Label(text);
            l.style.width = 18; l.style.height = 18;
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            l.style.fontSize = 10;
            l.style.color = Color.white;
            l.style.backgroundColor = color;
            Round(l, 4);
            return l;
        }

        // A pill chip with a coloured outline + tinted text (e.g. "Locked by you", "Outdated").
        public static VisualElement Chip(string text, Color color)
        {
            var chip = new VisualElement();
            chip.style.flexDirection = FlexDirection.Row;
            chip.style.alignItems = Align.Center;
            chip.style.paddingLeft = 6; chip.style.paddingRight = 6;
            chip.style.paddingTop = 1; chip.style.paddingBottom = 1;
            chip.style.backgroundColor = new Color(color.r, color.g, color.b, 0.12f);
            Round(chip, 4);
            chip.style.borderTopWidth = chip.style.borderBottomWidth = chip.style.borderLeftWidth = chip.style.borderRightWidth = 1;
            chip.style.borderTopColor = chip.style.borderBottomColor = chip.style.borderLeftColor = chip.style.borderRightColor = new Color(color.r, color.g, color.b, 0.5f);
            var l = new Label(text) { style = { color = color, fontSize = 10 } };
            chip.Add(l);
            return chip;
        }

        public static void Round(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        public static void ApplyMono(Label l)
        {
            var m = Mono;
            if (m != null) l.style.unityFont = m;
            l.style.color = Subdued;
            l.style.fontSize = 11;
        }

        // ---- IMGUI letter badge (project/hierarchy overlays) ----
        // The project and hierarchy overlays are IMGUI, not UI Toolkit, so they can't reuse BadgeSquare.
        // These draw the same rounded coloured square + white letter live (same colours, no baked PNG),
        // so a file's overlay matches its row in the Changes list.

        static Texture2D roundedMask;
        static GUIStyle letterStyle;

        // A white rounded-square alpha mask, tinted per status at draw time. One texture for every colour.
        static Texture2D RoundedMask()
        {
            if (roundedMask != null)
                return roundedMask;

            const int N = 32;
            const float r = 7f;           // corner radius, matching BadgeSquare's 4px at its 18px size
            const float half = N / 2f;
            const float inner = half - r; // half-extent of the straight (non-rounded) region
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                // Signed distance to a rounded box, then 1px anti-aliasing across the edge.
                float qx = Mathf.Abs(x + 0.5f - half) - inner;
                float qy = Mathf.Abs(y + 0.5f - half) - inner;
                float d = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f))
                          + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(0.5f - d) * 255f);
                px[y * N + x] = new Color32(255, 255, 255, a);
            }
            t.SetPixels32(px);
            t.Apply();
            roundedMask = t;
            return t;
        }

        // Draws a status letter badge (M/A/D/R/C/?) into rect: a coloured rounded square with a centred
        // white letter. Call only during a Repaint event.
        public static void DrawLetterBadge(Rect rect, string letter, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, RoundedMask(), ScaleMode.StretchToFill);
            GUI.color = prev;

            if (letterStyle == null)
            {
                letterStyle = new GUIStyle
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                };
                letterStyle.normal.textColor = Color.white;
            }
            letterStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(rect.height * 0.6f));
            GUI.Label(rect, letter, letterStyle);
        }
    }
}
