using System;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace CardboardXR.Demos.Desktop
{
    // Unity-side adapter for the native browser. iOS uses WKWebView; Android captures a
    // hardware-rendered WebView display. Other platforms keep the browser demo unavailable.
    public sealed class WebView : IDisposable
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int GlassesWeb_Create(int width, int height);
        [DllImport("__Internal")] static extern void GlassesWeb_Load(string url);
        [DllImport("__Internal")] static extern void GlassesWeb_Eval(string script);
        [DllImport("__Internal")] static extern void GlassesWeb_Back();
        [DllImport("__Internal")] static extern void GlassesWeb_Reload();
        [DllImport("__Internal")] static extern int GlassesWeb_IsLoading();
        [DllImport("__Internal")] static extern int GlassesWeb_CopyFrame(byte[] buffer, int length, int lastFrame);
        [DllImport("__Internal")] static extern void GlassesWeb_Destroy();
        static void GlassesWeb_StartVoiceSearch() { }
        static string GlassesWeb_ConsumeVoiceResult() => null;
        public static bool Supported => true;
#elif UNITY_ANDROID && !UNITY_EDITOR
        readonly AndroidJavaClass m_AndroidBridge;

        int GlassesWeb_Create(int width, int height) => m_AndroidBridge.CallStatic<int>("create", width, height);
        void GlassesWeb_Load(string url) => m_AndroidBridge.CallStatic("load", url);
        void GlassesWeb_Eval(string script) => m_AndroidBridge.CallStatic("evaluate", script);
        void GlassesWeb_Scroll(float x, float y, float pixels) => m_AndroidBridge.CallStatic("scroll", x, y, pixels);
        void GlassesWeb_Tap(float x, float y) => m_AndroidBridge.CallStatic("tap", x, y);
        void GlassesWeb_Back() => m_AndroidBridge.CallStatic("back");
        void GlassesWeb_Reload() => m_AndroidBridge.CallStatic("reload");
        void GlassesWeb_StartVoiceSearch() => m_AndroidBridge.CallStatic("startVoiceSearch");
        string GlassesWeb_ConsumeVoiceResult() => m_AndroidBridge.CallStatic<string>("consumeVoiceResult");
        int GlassesWeb_IsLoading() => m_AndroidBridge.CallStatic<bool>("isLoading") ? 1 : 0;
        byte[] GlassesWeb_CopyFrame() => m_AndroidBridge.CallStatic<byte[]>("copyFrame");
        void GlassesWeb_Destroy()
        {
            if (m_AndroidBridge == null)
                return;
            m_AndroidBridge.CallStatic("destroy");
            m_AndroidBridge.Dispose();
        }
        public static bool Supported => true;
#else
        static int GlassesWeb_Create(int width, int height) => 0;
        static void GlassesWeb_Load(string url) { }
        static void GlassesWeb_Eval(string script) { }
        static void GlassesWeb_Back() { }
        static void GlassesWeb_Reload() { }
        static void GlassesWeb_StartVoiceSearch() { }
        static string GlassesWeb_ConsumeVoiceResult() => null;
        static int GlassesWeb_IsLoading() => 0;
        static int GlassesWeb_CopyFrame(byte[] buffer, int length, int lastFrame) => -1;
        static byte[] GlassesWeb_CopyFrame() => null;
        static void GlassesWeb_Destroy() { }
        public static bool Supported => false;
#endif

        const string k_Tap =
            "(function(x,y){var e=document.elementFromPoint(x,y);if(!e)return;" +
            "var o={bubbles:true,cancelable:true,view:window,clientX:x,clientY:y};" +
            "['pointerdown','mousedown','pointerup','mouseup'].forEach(function(t){try{e.dispatchEvent(" +
            "t.indexOf('pointer')==0?new PointerEvent(t,o):new MouseEvent(t,o));}catch(_){}});" +
            "if(e.focus)e.focus();e.click();})({0},{1})";
        const string k_Scroll =
            "(function(x,y,d){var e=document.elementFromPoint(x,y),root=document.scrollingElement||document.documentElement;" +
            "while(e&&e!==document.documentElement){var s=getComputedStyle(e),o=s.overflowY;" +
            "if((o=='auto'||o=='scroll'||o=='overlay')&&e.scrollHeight>e.clientHeight+1){" +
            "var max=e.scrollHeight-e.clientHeight,next=Math.max(0,Math.min(max,e.scrollTop+d));" +
            "if(next!==e.scrollTop){e.scrollTop=next;return;}" +
            "if(s.overscrollBehaviorY=='contain'||s.overscrollBehaviorY=='none')return;}e=e.parentElement;}" +
            "root.scrollTop=Math.max(0,root.scrollTop+d);})({0},{1},{2})";
        const string k_Type =
            "(function(t){var e=document.activeElement;if(!e||!('value' in e))return;e.value=t;" +
            "e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));" +
            "if(e.form){if(e.form.requestSubmit)e.form.requestSubmit();else e.form.submit();}})({0})";
        const string k_Insert = "document.execCommand('insertText',false,{0})";
        const string k_Delete = "document.execCommand('delete')";
        const string k_Enter =
            "(function(){var e=document.activeElement;if(!e)return;var o={key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true};" +
            "e.dispatchEvent(new KeyboardEvent('keydown',o));e.dispatchEvent(new KeyboardEvent('keyup',o));" +
            "if(e.tagName=='TEXTAREA'||e.isContentEditable){document.execCommand('insertLineBreak');return;}" +
            "if(e.form){if(e.form.requestSubmit)e.form.requestSubmit();else e.form.submit();}})()";
        const string k_FullVideo =
            "(function(){var v=document.querySelector('video');if(!v)return;var s='position:fixed!important;inset:0!important;width:100vw!important;height:100vh!important;max-width:none!important;max-height:none!important;z-index:2147483647!important;background:#000!important;object-fit:contain!important;';" +
            "if(v.dataset.cardboardFill){v.style.cssText=v.dataset.cardboardFill;delete v.dataset.cardboardFill;document.documentElement.style.overflow='';document.body.style.overflow='';return;}" +
            "v.dataset.cardboardFill=v.style.cssText;v.style.cssText+=s;document.documentElement.style.overflow='hidden';document.body.style.overflow='hidden';" +
            "var f=v.requestFullscreen||v.webkitRequestFullscreen;if(f)f.call(v).catch(function(){});if(v.play)v.play().catch(function(){});})()";

        readonly int m_Width;
        readonly int m_Height;
        readonly byte[] m_Buffer;
        int m_Frame = -1;
        Vector2 m_ScrollPoint;
        float m_PendingScroll;
        float m_ScrollRemainder;
        float m_LastScrollSent = -1f;

        public Texture2D Texture { get; }
        public bool HasFrame => m_Frame > 0;
        public bool IsLoading => GlassesWeb_IsLoading() != 0;
        public float Aspect => (float)m_Height / m_Width;
        public bool HasMicrophonePermission
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return Permission.HasUserAuthorizedPermission(Permission.Microphone);
#else
                return false;
#endif
            }
        }

        public WebView(int width, int height, string androidBridgeClass = "com.glassesxr.cardboardxr.GlassesWebViewBridge")
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_AndroidBridge = new AndroidJavaClass(androidBridgeClass);
#endif
            m_Width = width;
            m_Height = height;
            m_Buffer = new byte[width * height * 4];
            Texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = "Browser" };
            if (Supported && GlassesWeb_Create(width, height) == 0)
                Debug.LogWarning("[Web] Couldn't create the browser view");
        }

        public void Load(string url)
        {
            Debug.Log($"[Web] Open {url}");
            GlassesWeb_Load(url);
        }

        public void Back() => GlassesWeb_Back();
        public void Reload() => GlassesWeb_Reload();

        // Normalized picture coordinates use a top-left origin, matching the browser and page pixels.
        public void Tap(Vector2 point)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            GlassesWeb_Tap(Mathf.Clamp01(point.x) * m_Width, Mathf.Clamp01(point.y) * m_Height);
#else
            GlassesWeb_Eval(Fill(k_Tap, Number(point.x * m_Width), Number(point.y * m_Height)));
#endif
        }

        public void Scroll(Vector2 point, float pixels)
        {
            m_ScrollPoint = point;
            m_ScrollRemainder += pixels;
            if (Mathf.Abs(m_ScrollRemainder) < 5f)
                return;
            m_PendingScroll += m_ScrollRemainder;
            m_ScrollRemainder = 0f;
        }
        public void Type(string text) => GlassesWeb_Eval(Fill(k_Type, Quote(text)));
        public void InsertText(string text) => GlassesWeb_Eval(Fill(k_Insert, Quote(text)));
        public void Backspace() => GlassesWeb_Eval(k_Delete);
        public void Enter() => GlassesWeb_Eval(k_Enter);
        public void ToggleVideoFullscreen() => GlassesWeb_Eval(k_FullVideo);

        public bool StartVoiceSearch()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Permission.RequestUserPermission(Permission.Microphone);
                return false;
            }
            GlassesWeb_StartVoiceSearch();
            return true;
#else
            return false;
#endif
        }

        public string ConsumeVoiceResult() => GlassesWeb_ConsumeVoiceResult();

        static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        static string Fill(string script, params string[] values)
        {
            for (var i = 0; i < values.Length; i++)
                script = script.Replace("{" + i + "}", values[i]);
            return script;
        }
        static string Number(float value) => value.ToString("F1", CultureInfo.InvariantCulture);

        public bool Update()
        {
            FlushScroll();
#if UNITY_ANDROID && !UNITY_EDITOR
            var frame = GlassesWeb_CopyFrame();
            if (frame == null || frame.Length != m_Buffer.Length)
                return false;
            // The JNI bridge already returns a managed copy. Upload it directly instead of
            // copying the entire RGBA frame a second time every update.
            Texture.LoadRawTextureData(frame);
            m_Frame++;
#else
            var frame = GlassesWeb_CopyFrame(m_Buffer, m_Buffer.Length, m_Frame);
            if (frame < 0)
                return false;
            m_Frame = frame;
#endif
            Texture.LoadRawTextureData(m_Buffer);
            Texture.Apply(false);
            return true;
        }

        void FlushScroll()
        {
            if (Mathf.Abs(m_PendingScroll) < 1f)
                return;
            var now = Time.unscaledTime;
            if (m_LastScrollSent >= 0f && now - m_LastScrollSent < 0.016f)
                return;
            var pixels = Mathf.Clamp(m_PendingScroll, -320f, 320f);
            m_PendingScroll -= pixels;
            m_LastScrollSent = now;
#if UNITY_ANDROID && !UNITY_EDITOR
            GlassesWeb_Scroll(m_ScrollPoint.x * m_Width, m_ScrollPoint.y * m_Height, pixels);
#else
            GlassesWeb_Eval(Fill(k_Scroll, Number(m_ScrollPoint.x * m_Width), Number(m_ScrollPoint.y * m_Height), Number(pixels)));
#endif
        }

        public void Dispose()
        {
            m_PendingScroll = 0f;
            m_ScrollRemainder = 0f;
            GlassesWeb_Destroy();
            UnityEngine.Object.Destroy(Texture);
        }
    }
}
