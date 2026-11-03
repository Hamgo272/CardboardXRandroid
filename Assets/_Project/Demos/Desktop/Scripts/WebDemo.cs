using System;
using CardboardXR.Hands;
using UnityEngine;
using Unity.Collections;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Desktop
{
    // Browser pages are drawn into a texture. Keep video URLs in the in-app browser so the page
    // remains on the floating VR screen instead of launching another app.
    public class WebDemo : Demo, IPinchHandler
    {
        [Tooltip("Unlit material the screen's parts are made from.")]
        [SerializeField] Material m_Material;

        const string k_Home = "https://www.google.com";
        const string k_YouTube = "https://www.youtube.com";
        const string k_Gemini = "https://gemini.google.com/app";
        const string k_LastKey = "CardboardXR.WebLast";
        const float k_PixelsPerMeter = 5000f;
        const int k_PageWidth = 960;
        const int k_PageHeight = 540;

        CoreServices m_Core;
        FloatingScreen m_Screen;
        WebView m_Web;
        WebView m_MapsWeb;
        FloatingScreen m_MapsScreen;
        TouchScreenKeyboard m_Keyboard;
        SurfaceKeyboard m_Keys;
        Vector2 m_Cursor = new(0.5f, 0.5f);
        string m_StatusMessage;
        float m_StatusUntil;
        bool m_WaitingForVoice;
        bool m_ListeningForVoice;

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
                Scroll = (p, pixels) => m_Web.Scroll(p, pixels),
            };
            m_Screen.AddButton("Back", () => m_Web.Back(), "chevron_left");
            m_Screen.AddButton("Home", () => Open(k_Home), "home");
            m_Screen.AddButton("Maps", OpenGoogleMaps, "map");
            m_Screen.AddButton("YouTube", () => Open(k_YouTube), "movie");
            m_Screen.AddButton("Gemini", () => Open(k_Gemini), "auto_awesome");
            m_Screen.AddButton("Lens", StartVisualSearch, "image_search");
            m_Screen.AddButton("Reload", () => m_Web.Reload(), "refresh");
            m_Screen.AddButton("Keys", ToggleKeys, "touch_app");
            m_Screen.AddButton("Find", OpenKeyboard, "edit");
            m_Screen.AddButton("Voice", StartVoiceSearch, "front_hand");
            m_Screen.AddButton("Video", () => m_Web.ToggleVideoFullscreen(), "aspect_ratio");
            m_Keys = new SurfaceKeyboard(core.Boundary.transform, m_Material)
            {
                Text = t => m_Web.InsertText(t),
                Backspace = () => m_Web.Backspace(),
                Enter = () => m_Web.Enter(),
                PadMove = p => { m_Cursor = p; m_Screen.SetCursor(p); },
                PadScroll = meters => m_Web.Scroll(m_Cursor, meters * k_PixelsPerMeter),
                PadTap = () => m_Web.Tap(m_Cursor),
            };
            m_Screen.PlaceInFront(core.Head);
            Open(PlayerPrefs.GetString(k_LastKey, k_Home));
        }

        public override void OnDemoExit(CoreServices core)
        {
            if (ReferenceEquals(PinchRouter.Handler, this))
                PinchRouter.Clear();
            CloseGoogleMaps();
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
            UpdateVoiceSearch();
            var showingStatus = Time.unscaledTime < m_StatusUntil && !string.IsNullOrEmpty(m_StatusMessage);
            m_Screen.Interactive = m_Web.HasFrame && !showingStatus;
            var status = showingStatus ? m_StatusMessage : "Loading...";
            m_Screen.Show(!showingStatus && m_Web.HasFrame ? m_Web.Texture : null, m_Web.Aspect, status);
            m_Screen.Update(m_Core.Head, m_Core.HandTracker);
            if (m_MapsWeb != null && m_MapsScreen != null)
            {
                m_MapsWeb.Update();
                m_MapsScreen.Interactive = m_MapsWeb.HasFrame;
                m_MapsScreen.Show(m_MapsWeb.HasFrame ? m_MapsWeb.Texture : null, m_MapsWeb.Aspect,
                    m_MapsWeb.IsLoading ? "Loading Google Maps..." : "Google Maps is opening...");
                m_MapsScreen.Update(m_Core.Head, m_Core.HandTracker);
            }
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

        // Maps is an independent VR app surface: its own Android WebView, frame stream,
        // input bridge and FloatingScreen. Opening it never navigates or replaces the browser.
        void OpenGoogleMaps()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_MapsWeb != null)
                return;

            const int mapsWidth = 800;
            const int mapsHeight = 450;
            m_MapsWeb = new WebView(mapsWidth, mapsHeight,
                "com.glassesxr.cardboardxr.GlassesMapsWebViewBridge");
            m_MapsScreen = new FloatingScreen("Google Maps", m_Core.Boundary.transform, m_Material)
            {
                Tap = point => m_MapsWeb.Tap(point),
                HoldUp = point => m_MapsWeb.Tap(point),
                Scroll = (point, pixels) => m_MapsWeb.Scroll(point, pixels),
            };
            m_MapsScreen.AddButton("Back", () => m_MapsWeb?.Back(), "chevron_left");
            m_MapsScreen.AddButton("Reload", () => m_MapsWeb?.Reload(), "refresh");
            m_MapsScreen.AddButton("Close", CloseGoogleMaps, "close");
            m_MapsScreen.PlaceInFront(m_Core.Head);
            m_MapsScreen.Root.position += m_Core.Head.right * 1.05f;
            m_MapsWeb.Load("https://www.google.com/maps/");
#else
            Open("https://www.google.com/maps/");
#endif
        }

        void CloseGoogleMaps()
        {
            m_MapsWeb?.Dispose();
            m_MapsWeb = null;
            m_MapsScreen?.Destroy();
            m_MapsScreen = null;
        }

        void OpenKeyboard()
        {
            m_Keys?.Hide();
            if (m_Keyboard == null)
                m_Keyboard = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.URL, false, false, false, false,
                    "Address or search the web");
        }

        // Capture the live AR camera image and hand it to Android's image-share resolver.
        // The user can choose Google Lens (if installed), Google, or another visual-search app.
        void StartVisualSearch()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var camera = UnityEngine.Object.FindFirstObjectByType<ARCameraManager>();
            if (camera == null || !camera.TryAcquireLatestCpuImage(out var image))
            {
                m_StatusMessage = "Camera image unavailable. Enable 6DoF and camera access.";
                m_StatusUntil = Time.unscaledTime + 5f;
                return;
            }

            try
            {
                var scale = Mathf.Min(1f, 1280f / Mathf.Max(image.width, image.height));
                var dimensions = new Vector2Int(
                    Mathf.Max(1, Mathf.RoundToInt(image.width * scale)),
                    Mathf.Max(1, Mathf.RoundToInt(image.height * scale)));
                var conversion = new XRCpuImage.ConversionParams
                {
                    inputRect = new RectInt(0, 0, image.width, image.height),
                    outputDimensions = dimensions,
                    outputFormat = TextureFormat.RGBA32,
                    transformation = XRCpuImage.Transformation.MirrorY
                };
                var size = image.GetConvertedDataSize(conversion);
                using (var pixels = new NativeArray<byte>(size, Allocator.Temp))
                {
                    image.Convert(conversion, pixels);
                    var texture = new Texture2D(dimensions.x, dimensions.y, TextureFormat.RGBA32, false);
                    texture.LoadRawTextureData(pixels);
                    texture.Apply(false);
                    var jpeg = texture.EncodeToJPG(82);
                    UnityEngine.Object.Destroy(texture);
                    using (var bridge = new AndroidJavaClass("com.glassesxr.cardboardxr.GlassesWebViewBridge"))
                        bridge.CallStatic("shareImage", jpeg);
                }
                m_StatusMessage = "Choose Google Lens or another visual-search app.";
                m_StatusUntil = Time.unscaledTime + 6f;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VisualSearch] Camera capture/share failed: " + e.Message);
                m_StatusMessage = "Visual search couldn't capture the camera image.";
                m_StatusUntil = Time.unscaledTime + 5f;
            }
            finally
            {
                image.Dispose();
            }
#else
            m_StatusMessage = "Visual search is available in the Android build.";
            m_StatusUntil = Time.unscaledTime + 5f;
#endif
        }

        void StartVoiceSearch()
        {
            m_Keys?.Hide();
            m_WaitingForVoice = true;
            m_ListeningForVoice = m_Web.StartVoiceSearch();
            m_StatusMessage = m_ListeningForVoice ? "Listening..." : "Allow microphone access to use voice search";
            m_StatusUntil = Time.unscaledTime + 8f;
        }

        void UpdateVoiceSearch()
        {
            if (!m_WaitingForVoice)
                return;
            if (!m_ListeningForVoice && m_Web.HasMicrophonePermission)
            {
                m_ListeningForVoice = m_Web.StartVoiceSearch();
                if (m_ListeningForVoice)
                {
                    m_StatusMessage = "Listening...";
                    m_StatusUntil = Time.unscaledTime + 8f;
                }
            }
            var query = m_Web.ConsumeVoiceResult();
            if (string.IsNullOrWhiteSpace(query))
                return;
            m_WaitingForVoice = false;
            m_ListeningForVoice = false;
            Search(query.Trim());
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
                Search(text);
        }

        void Search(string query)
        {
            var encoded = Uri.EscapeDataString(query);
            var lastPage = PlayerPrefs.GetString(k_LastKey, k_Home);
            Open(IsYouTubePage(lastPage)
                ? "https://www.youtube.com/results?search_query=" + encoded
                : "https://www.google.com/search?q=" + encoded);
        }

        void Open(string url)
        {
            url = url?.Trim();
            if (string.IsNullOrEmpty(url))
                return;

            PlayerPrefs.SetString(k_LastKey, url);
            m_Web.Load(url);
            if (IsVideoPage(url))
            {
                m_StatusMessage = "Opening video in the VR browser...";
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

        static bool IsYouTubePage(string url) => !string.IsNullOrEmpty(url) &&
            (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
             url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase));

        public bool TryBeginPinch(GameObject gazeTarget, PinchSource source) =>
            m_Screen != null && (m_Keys.TryBeginPinch(m_Core.Head) ||
                (m_MapsScreen != null && m_MapsScreen.TryBeginPinch(m_Core.Head, source, m_Core.HandTracker)) ||
                m_Screen.TryBeginPinch(m_Core.Head, source, m_Core.HandTracker));

        public void EndPinch()
        {
            m_Keys?.EndPinch();
            m_MapsScreen?.EndPinch(m_Core.Head);
            m_Screen?.EndPinch(m_Core.Head);
        }
    }
}
