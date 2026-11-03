using System;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;

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
        public static bool Supported => true;
#elif UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaClass s_Bridge;

        static AndroidJavaClass Bridge
        {
            get
            {
                if (s_Bridge == null)
                    s_Bridge = new AndroidJavaClass("com.glassesxr.cardboardxr.GlassesWebViewBridge");
                return s_Bridge;
            }
        }

        static int GlassesWeb_Create(int width, int height) => Bridge.CallStatic<int>("create", width, height);
        static void GlassesWeb_Load(string url) => Bridge.CallStatic("load", url);
        static void GlassesWeb_Eval(string script) => Bridge.CallStatic("evaluate", script);
        static void GlassesWeb_Tap(float x, float y) => Bridge.CallStatic("tap", x, y);
        static void GlassesWeb_Back() => Bridge.CallStatic("back");
        static void GlassesWeb_Reload() => Bridge.CallStatic("reload");
        static int GlassesWeb_IsLoading() => Bridge.CallStatic<bool>("isLoading") ? 1 : 0;
        static byte[] GlassesWeb_CopyFrame() => Bridge.CallStatic<byte[]>("copyFrame");
        static void GlassesWeb_Destroy()
        {
            if (s_Bridge == null)
                return;
            s_Bridge.CallStatic("destroy");
            s_Bridge.Dispose();
            s_Bridge = null;
        }
        public static bool Supported => true;
#else
        static int GlassesWeb_Create(int width, int height) => 0;
        static void GlassesWeb_Load(string url) { }
        static void GlassesWeb_Eval(string script) { }
        static void GlassesWeb_Back() { }
        static void GlassesWeb_Reload() { }
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
            "(function(x,y,d){var e=document.elementFromPoint(x,y);while(e&&e!==document.documentElement){" +
            "var s=getComputedStyle(e),o=s.overflowY,t=e.scrollTop,m=e.scrollHeight-e.clientHeight;" +
            "if((o=='auto'||o=='scroll'||o=='overlay')&&m>1&&((d>0&&t<m-1)||(d<0&&t>1)))" +
            "{e.scrollBy(0,d);return;}e=e.parentElement;}window.scrollBy(0,d);})({0},{1},{2})";
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

        readonly int m_Width;
        readonly int m_Height;
        readonly byte[] m_Buffer;
        int m_Frame = -1;
        Vector2 m_ScrollPoint;
        float m_PendingScroll;
        float m_LastScrollSent = -1f;

        public Texture2D Texture { get; }
        public bool HasFrame => m_Frame > 0;
        public bool IsLoading => GlassesWeb_IsLoading() != 0;
        public float Aspect => (float)m_Height / m_Width;

        public WebView(int width, int height)
        {
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
            m_PendingScroll += pixels;
        }
        public void Type(string text) => GlassesWeb_Eval(Fill(k_Type, Quote(text)));
        public void InsertText(string text) => GlassesWeb_Eval(Fill(k_Insert, Quote(text)));
        public void Backspace() => GlassesWeb_Eval(k_Delete);
        public void Enter() => GlassesWeb_Eval(k_Enter);

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
            Buffer.BlockCopy(frame, 0, m_Buffer, 0, m_Buffer.Length);
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
            if (Mathf.Abs(m_PendingScroll) < 0.5f)
                return;
            var now = Time.unscaledTime;
            if (m_LastScrollSent >= 0f && now - m_LastScrollSent < 0.025f)
                return;
            var pixels = m_PendingScroll;
            m_PendingScroll = 0f;
            m_LastScrollSent = now;
            GlassesWeb_Eval(Fill(k_Scroll, Number(m_ScrollPoint.x * m_Width), Number(m_ScrollPoint.y * m_Height), Number(pixels)));
        }

        public void Dispose()
        {
            m_PendingScroll = 0f;
            GlassesWeb_Destroy();
            UnityEngine.Object.Destroy(Texture);
        }
    }
}
