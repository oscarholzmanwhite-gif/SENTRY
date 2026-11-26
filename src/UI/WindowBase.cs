using ClickThroughFix;
using UnityEngine;

namespace Sentry.UI
{
    // A minimal draggable, resizable, closable IMGUI window: GUILayout content inside a
    // ClickThroughBlocker-wrapped GUILayout.Window (so clicks don't fall through to the game
    // behind it), a title-bar drag area, and a bottom-right resize grip. This is the standard
    // shape nearly every KSP UI mod's window ends up with - not modeled on any one mod's actual
    // code.
    public abstract class WindowBase
    {
        private const float MinWidth = 220f;
        private const float MinHeight = 80f;
        private const float TitleBarHeight = 20f;
        private const float ResizeHandleSize = 14f;

        private readonly int windowId;
        private readonly string title;
        private bool resizing;
        private int resizeControlId = -1;
        private bool visible;
        private Rect lastNotifiedRect;

        protected Rect windowRect;

        protected WindowBase(string title, float defaultWidth, float defaultHeight)
        {
            this.title = title;
            windowId = title.GetHashCode() ^ (GetType().GetHashCode() << 8) ^ Random.Range(0, 1 << 16);
            windowRect = new Rect(
                (Screen.width - defaultWidth) / 2f,
                (Screen.height - defaultHeight) / 3f,
                defaultWidth, defaultHeight);
            lastNotifiedRect = windowRect;
        }

        // Exposed so a subclass can restore a saved position/size (e.g. from UiPrefs) and read the
        // current one to persist it - see OnRectChanged below for the "when to persist" half.
        public Rect WindowRect { get { return windowRect; } set { windowRect = value; lastNotifiedRect = value; } }

        public void Show() { visible = true; }
        public void Hide() { visible = false; OnHidden(); }

        public void Draw()
        {
            if (!visible) return;

            // HighLogic.Skin is the game's own semi-transparent grey dialog skin - the "KSP look"
            // shared by stock windows and most UI mods (including [x] Science!, which does the
            // same skin swap). Using the game's own stock asset this way isn't copying anything
            // from any one mod's code; it's the standard way to make a window look like it belongs
            // in KSP instead of a bare Unity default grey box.
            GUISkin previousSkin = GUI.skin;
            if (HighLogic.Skin != null) GUI.skin = HighLogic.Skin;
            try
            {
                windowRect = ClickThruBlocker.GUILayoutWindow(windowId, windowRect, DrawWindowInternal, title,
                    GUILayout.Width(windowRect.width), GUILayout.Height(windowRect.height));
                DrawTooltip();
            }
            finally
            {
                GUI.skin = previousSkin;
            }
            windowRect = ClampToScreen(windowRect);

            // Backstop for a MouseUp event that never reached DrawResizeHandle (alt-tab, focus
            // loss, or the event being consumed by something else first) - without this, a missed
            // MouseUp would leave `resizing` stuck true forever, silently blocking any further
            // drag from starting. Checked every frame regardless of which GUI event is current.
            if (resizing && !Input.GetMouseButton(0))
            {
                resizing = false;
                if (GUIUtility.hotControl == resizeControlId) GUIUtility.hotControl = 0;
                resizeControlId = -1;
            }

            // Notify once a move/resize has actually settled (mouse released, not mid-resize) -
            // avoids hammering a subclass's persistence (e.g. disk I/O) every frame of a drag.
            if (!resizing && !Input.GetMouseButton(0) && (windowRect.x != lastNotifiedRect.x
                || windowRect.y != lastNotifiedRect.y || windowRect.width != lastNotifiedRect.width
                || windowRect.height != lastNotifiedRect.height))
            {
                lastNotifiedRect = windowRect;
                OnRectChanged(windowRect);
            }
        }

        // Hooks for a subclass that wants to persist window state outside the save file (see
        // AlertWindow + UiPrefs). No-ops by default so WindowBase stays usable standalone.
        protected virtual void OnHidden() { }
        protected virtual void OnRectChanged(Rect rect) { }

        // Subclasses lay out their content with normal GUILayout calls; the close button, resize
        // grip and drag handling are added around it automatically.
        protected abstract void DrawContents();

        private void DrawWindowInternal(int id)
        {
            DrawContents();
            DrawCloseButton();
            DrawResizeHandle();
            GUI.DragWindow(new Rect(0f, 0f, windowRect.width, TitleBarHeight));
        }

        private void DrawCloseButton()
        {
            if (GUI.Button(new Rect(windowRect.width - 20f, 2f, 18f, 16f), new GUIContent("x", "Close")))
            {
                Hide();
            }
        }

        // Claims GUIUtility.hotControl for the duration of the drag, the same mechanism
        // GUI.DragWindow uses internally for the title bar. Without this, Unity's IMGUI routes
        // MouseDrag/MouseUp events by which window rect the cursor currently sits over - so
        // moving the mouse fast enough to jump outside the window's (not-yet-grown) rect between
        // frames stops delivering events to this control entirely, leaving `resizing` stuck true
        // with no more input reaching it.
        // Hot-control capture makes every subsequent mouse event reach this control regardless of
        // cursor position, exactly like the title-bar drag already relies on.
        private void DrawResizeHandle()
        {
            Rect handleRect = new Rect(windowRect.width - ResizeHandleSize, windowRect.height - ResizeHandleSize, ResizeHandleSize, ResizeHandleSize);
            GUI.Label(handleRect, new GUIContent("//", "Drag to resize"));

            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;

            if (!resizing)
            {
                if (e.type == EventType.MouseDown && e.button == 0 && handleRect.Contains(e.mousePosition))
                {
                    resizing = true;
                    resizeControlId = controlId;
                    GUIUtility.hotControl = controlId;
                    e.Use();
                }
            }
            else if (GUIUtility.hotControl == resizeControlId)
            {
                if (e.type == EventType.MouseDrag)
                {
                    windowRect.width = Mathf.Max(MinWidth, windowRect.width + e.delta.x);
                    windowRect.height = Mathf.Max(MinHeight, windowRect.height + e.delta.y);
                    e.Use();
                }
                else if (e.type == EventType.MouseUp)
                {
                    resizing = false;
                    GUIUtility.hotControl = 0;
                    resizeControlId = -1;
                    e.Use();
                }
            }
        }

        // Any control drawn with a GUIContent that has a non-empty tooltip (buttons, toggles,
        // labels...) sets the global GUI.tooltip string while the mouse hovers it during Repaint.
        // Nothing draws it on screen by default - this renders a small floating box near the
        // cursor, shared by every WindowBase subclass rather than each reimplementing it. Runs
        // after the window content so it's read post-hover, and only on Repaint (the only event
        // that actually paints pixels) to avoid mid-layout hiccups.
        private static GUIStyle tooltipStyle;

        private static void DrawTooltip()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            string tooltip = GUI.tooltip;
            if (string.IsNullOrEmpty(tooltip)) return;

            if (tooltipStyle == null)
            {
                tooltipStyle = new GUIStyle(GUI.skin.box) { wordWrap = true, alignment = TextAnchor.UpperLeft };
                tooltipStyle.padding = new RectOffset(6, 6, 4, 4);
            }

            const float width = 220f;
            GUIContent content = new GUIContent(tooltip);
            float height = tooltipStyle.CalcHeight(content, width);

            Vector2 mouse = Event.current.mousePosition;
            Rect r = new Rect(mouse.x + 16f, mouse.y + 16f, width, height);
            if (r.xMax > Screen.width) r.x = Screen.width - r.width - 4f;
            if (r.yMax > Screen.height) r.y = mouse.y - height - 8f;

            GUI.Box(r, content, tooltipStyle);
        }

        private static Rect ClampToScreen(Rect r)
        {
            r.x = Mathf.Clamp(r.x, -r.width + 60f, Screen.width - 60f);
            r.y = Mathf.Clamp(r.y, 0f, Screen.height - 40f);
            return r;
        }
    }
}
