using CardboardXR.Hands;
using UnityEngine;

namespace CardboardXR.Demos.Desktop
{
    // Browser pages are drawn into a texture. Android's WebView cannot capture video layers, so video
    // URLs are handed to a native app instead of showing a permanently black player rectangle.
    public class WebDemo : Demo, IPinchHandler
    {
        [Tooltip("Unlit material the screen's parts are made from.")]
        [SerializeField] Material m_Material;

        const string k_Home = "https://www.google.com";
        const string k_YouTube = "https://www.youtube.com";
        const string k_LastKey = "CardboardXR.WebLast";
        const float k_PixelsPerMeter = 3000f;
        const int k_PageWidth = 1280;
        const int k_PageHeight = 720;

        CoreServices m_Core;
        FloatingScreen m_Screen;
        WebView m_Web;
        TouchScreenKeyboard m_Keyboard;
        SurfaceKeyboard m_Keys;
        Vector2 m_Cursor = new(0.5f, 0.5f);
        string m_StatusMessage;
        float m_StatusUntil;

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
            m_Screen.AddButton("Google", () => Open(k_Home));
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
            Open(PlayerPrefs.GetString(k_LastKey, k_Home));
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
            m_Web.Update();
            m_Screen.Interactive = m_Web.HasFrame;
            var status = Time.unscaledTime < m_StatusUntil ? m_StatusMessage : "Loading...";
            m_Screen.Show(m_Web.HasFrame ? m_Web.Texture : null, m_Web.Aspect, status);
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
            if (IsVideoPage(url) && m_Web.OpenExternal(url))
            {
                PlayerPrefs.SetString(k_LastKey, url);
                m_StatusMessage = "Video opened in the installed app/browser. Return to CardboardXR to continue.";
                m_StatusUntil = Time.unscaledTime + 12f;
                return;
            }

            PlayerPrefs.SetString(k_LastKey, url);
            m_Web.Load(url);
            if (IsVideoPage(url))
            {
                m_StatusMessage = "This Android WebView cannot capture video playback. Install a video app, or use Computer Screen.";
                m_StatusUntil = Time.unscaledTime + 12f;
            }
            else
            {
                m_StatusMessage = "";
                m_StatusUntil = 0f;
            }
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
