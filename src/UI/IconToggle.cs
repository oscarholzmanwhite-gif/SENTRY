using UnityEngine;

namespace Sentry.UI
{
    // A square icon button that overlays a second texture (typically a red X) on top of a base
    // icon when some state is true - the "fast-forward icon with a red X" pattern the owner asked
    // for, generalised so both toggle buttons in AlertWindow can share it. Deliberately just a
    // GUILayout.Button drawn blank plus two GUI.DrawTexture calls into its rect, rather than baked
    // combined textures per state - that way the base icon and the X overlay are separate files
    // and either can be swapped later (the owner plans to replace the placeholder art) without
    // touching the other or any code here.
    public static class IconToggle
    {
        // Returns true the frame the button is clicked. Draws baseIcon always, and overlay on top
        // of it only while overlayOn is true.
        public static bool Draw(Texture2D baseIcon, Texture2D overlay, bool overlayOn, string tooltip, float size)
        {
            bool clicked = GUILayout.Button(new GUIContent(string.Empty, tooltip),
                GUILayout.Width(size), GUILayout.Height(size));
            Rect buttonRect = GUILayoutUtility.GetLastRect();

            float pad = size * 0.16f;
            Rect iconRect = new Rect(buttonRect.x + pad, buttonRect.y + pad,
                buttonRect.width - 2f * pad, buttonRect.height - 2f * pad);

            if (baseIcon != null) GUI.DrawTexture(iconRect, baseIcon, ScaleMode.ScaleToFit);
            if (overlayOn && overlay != null) GUI.DrawTexture(iconRect, overlay, ScaleMode.ScaleToFit);

            return clicked;
        }
    }
}
