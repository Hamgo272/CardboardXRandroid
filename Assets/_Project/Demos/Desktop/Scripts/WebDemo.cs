using CardboardXR.Hands;
using UnityEngine;

namespace CardboardXR.Demos.Desktop
{
    // A browser on a FloatingScreen in the room, at any size: Google, YouTube and any website.
    // Look and pinch to tap, pinch and move the hand to scroll; the buttons above go back, home,
    // to YouTube, reload, or open the keyboard to type into the field last tapped.
    public class WebDemo : Demo, IPinchHandler
    {
        [Tooltip("Unlit material the screen's parts are made from.")]
        [SerializeField] Material m_Material;

        const string k_Home = "https://www.google.com";
        const string k_YouTube = "https://m.youtube.com";
        const string k_LastKey = "CardboardXR.WebLast";
        // Page pixels per meter of hand travel while scrolling.
        const float k_PixelsPerMeter = 3000f;
        // Page size kept intentionally modest; we render pages by drawing a hidden Android WebView
        // into a bitmap, and sites with video surfaces can go black if they rely on hardware video
        // layers. Large, flat pages are stable; video-heavy pages are rejected for a clear message.
        const int k_PageWidth = 1280;
        const int k_PageHeight = 720;

        CoreServices m_Core;
        FloatingScreen m_Screen;
        WebView m_Web;
        TouchScreenKeyboard m_Keyboard;
        SurfaceKeyboard m_Keys;
        Vector2 m_Cursor = new(0.5f, 0.5f);
        bool m_BlockVideoViewport;
        string m_BlockMessage = "";

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            core.Placer.ObjectPrefab = null;
            PinchRouter.Handler = this;
            m_Web = new WebView(k_PageWidth, k_PageHeight);
            m_Screen = new FloatingScreen("Browser", core.Boundary.transform, m_Material)
            {
                Tap = p => m_Web.Tap(p),
                HoldUp = p => m_Web.Tap(p),
                Scroll = (p, meters) => m_Web.Scroll(p, meters * k_PixelsPerMeter),
            };
            m_Screen.AddButton("Back", () => m_Web.Back());
            m_Screen.AddButton("Google", () => m_Web.Load(k_Home));
            m_Screen.AddButton("YouTube", () => Open(k_YouTube));
            m_Screen.AddButton("Reload", () => m_Web.Reload());
            m_Screen.AddButton("Keyboard", ToggleKeys);
            m_Screen.AddButton("Address", OpenKeyboard);
            m_Keys = new SurfaceKeyboard(core.Boundary.transform, m_Material)
            {
                Text = t => m_Web.InsertText(t),
                Backspace = () => m_Web.Backspace(),
                Enter = () => m_Web.Enter(),
                PadMove = p => { m_Cursor = p; m_Screen.SetCursor(p); },
                PadTap = () => m_Web.Tap(m_Cursor),
            };
            m_Screen.PlaceInFront(core.Head);
            m_Web.Load(PlayerPrefs.GetString(k_LastKey, k_Home));
        }

        public override void OnDemoExit(CoreServices core)
        {
            if (ReferenceEquals(PinchRouter.Handler, this))
                PinchRouter.Clear();
            m_Web?.Dispose();
            m_Web = null;
            m_Screen?.Destroy();
            m_Screen = null;
            m_Keys?.Destroy();
            m_Keys = null;
        }

        void Update()
        {
            if (m_Screen == null)
                return;

            if (m_BlockVideoViewport)
            {
                m_Screen.Interactive = false;
                m_Screen.Show(null, 0f, m_BlockMessage);
                m_Screen.Update(m_Core.Head, m_Core.HandTracker);
                m_Keys.Update(m_Core.Head, m_Core.HandTracker);
                if (!m_Keys.Visible)
                    m_Screen.SetCursor(null);
                return;
            }

            m_Web.Update();
            m_Screen.Interactive = m_Web.HasFrame;
            m_Screen.Show(m_Web.HasFrame ? m_Web.Texture : null, m_Web.Aspect,
                WebView.Supported ? "Loading..." : "The browser runs on the iPhone.");
            m_Screen.Update(m_Core.Head, m_Core.HandTracker);
            m_Keys.Update(m_Core.Head, m_Core.HandTracker);
            if (!m_Keys.Visible)
                m_Screen.SetCursor(null);

            if (m_Keyboard == null)
                return;
            if (m_Keyboard.status == TouchScreenKeyboard.Status.Done)
                Submit(m_Keyboard.text);
            if (m_Keyboard.status != TouchScreenKeyboard.Status.Visible)
                m_Keyboard = null;
        }

        void ToggleKeys()
        {
            if (m_Keys.Visible)
                m_Keys.Hide();
            else
                m_Keys.Show(m_Core.Head);
        }

        void OpenKeyboard()
        {
            if (m_Keyboard == null)
                m_Keyboard = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.Default, true, false, false, false,
                    "Search or address");
        }

        // An address opens; anything else goes into the field last tapped (a search box, say).
        void Submit(string text)
        {
            text = text.Trim();
            if (text.Length == 0)
                return;
            if (text.StartsWith("http://") || text.StartsWith("https://"))
                Open(text);
            else if (!text.Contains(" ") && text.Contains(".") && !text.EndsWith("."))
                Open("https://" + text);
            else
                m_Web.Type(text);
        }

        void Open(string url)
        {
            url = url?.Trim();
            if (string.IsNullOrEmpty(url))
                return;

            if (IsVideoPage(url))
            {
                m_BlockVideoViewport = true;
                m_BlockMessage = "Android WebView cannot render YouTube / video pages without a black frame. Use Computer Screen instead.";
                m_Web.Load("about:blank");
                PlayerPrefs.SetString(k_LastKey, url);
                return;
            }

            m_BlockVideoViewport = false;
            m_BlockMessage = "";
            PlayerPrefs.SetString(k_LastKey, url);
            m_Web.Load(url);
        }

        static bool IsVideoPage(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;
            var lower = url.ToLowerInvariant();
            return lower.Contains("youtube.com") || lower.Contains("youtu.be") || lower.Contains("vimeo.com") || lower.Contains("twitch.tv");
        }

        public bool TryBeginPinch(GameObject gazeTarget, PinchSource source) =>
            m_Screen != null && (m_Keys.TryBeginPinch(m_Core.Head) || m_Screen.TryBeginPinch(m_Core.Head, source, m_Core.HandTracker));

        public void EndPinch() => m_Screen?.EndPinch(m_Core.Head);
    }
}
