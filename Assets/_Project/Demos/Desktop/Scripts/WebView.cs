using System;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CardboardXR.Demos.Desktop
{
    // Unity's side of the native browser (Assets/Plugins/iOS/GlassesWebView.mm): a texture that
    // follows the page, and input as JavaScript. iPhone only; elsewhere Supported is false.
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
#else
        static int GlassesWeb_Create(int width, int height) => 0;
        static void GlassesWeb_Load(string url) { }
        static void GlassesWeb_Eval(string script) { }
        static void GlassesWeb_Back() { }
        static void GlassesWeb_Reload() { }
        static int GlassesWeb_IsLoading() => 0;
        static int GlassesWeb_CopyFrame(byte[] buffer, int length, int lastFrame) => -1;
        static void GlassesWeb_Destroy() { }
        public static bool Supported => false;
#endif

        // Taps go to the element under the point: pointer and mouse events, then its click().
        const string k_Tap =
            "(function(x,y){var e=document.elementFromPoint(x,y);if(!e)return;" +
            "var o={bubbles:true,cancelable:true,view:window,clientX:x,clientY:y};" +
            "['pointerdown','mousedown','pointerup','mouseup'].forEach(function(t){try{e.dispatchEvent(" +
            "t.indexOf('pointer')==0?new PointerEvent(t,o):new MouseEvent(t,o));}catch(_){}});" +
            "if(e.focus)e.focus();e.click();})({0},{1})";

        // Scrolls the nearest scrollable container under the point, or the page.
        const string k_Scroll =
            "(function(x,y,d){var e=document.elementFromPoint(x,y);while(e&&e!==document.body){" +
            "var s=getComputedStyle(e);if((s.overflowY=='auto'||s.overflowY=='scroll')&&e.scrollHeight>e.clientHeight)" +
            "{e.scrollBy(0,d);return;}e=e.parentElement;}window.scrollBy(0,d);})({0},{1},{2})";

        // Types into the focused field and submits its form, as pressing Go would.
        const string k_Type =
            "(function(t){var e=document.activeElement;if(!e||!('value' in e))return;e.value=t;" +
            "e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));" +
            "if(e.form){if(e.form.requestSubmit)e.form.requestSubmit();else e.form.submit();}})({0})";

        // Typing into the focused field, as a real keyboard would.
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

        // Point in the picture: 0..1, y down.
        public void Tap(Vector2 point) =>
            GlassesWeb_Eval(Fill(k_Tap, Number(point.x * m_Width), Number(point.y * m_Height)));

        public void Scroll(Vector2 point, float pixels) =>
            GlassesWeb_Eval(Fill(k_Scroll, Number(point.x * m_Width), Number(point.y * m_Height), Number(pixels)));

        public void Type(string text) =>
            GlassesWeb_Eval(Fill(k_Type, "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));

        public void InsertText(string text) => GlassesWeb_Eval(Fill(k_Insert, Quote(text)));
        public void Backspace() => GlassesWeb_Eval(k_Delete);
        public void Enter() => GlassesWeb_Eval(k_Enter);

        static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // The scripts are full of braces, so placeholders are filled by plain replacement.
        static string Fill(string script, params string[] values)
        {
            for (var i = 0; i < values.Length; i++)
                script = script.Replace("{" + i + "}", values[i]);
            return script;
        }

        static string Number(float value) => value.ToString("F1", CultureInfo.InvariantCulture);

        // Copies a newer picture into Texture; true if there was one.
        public bool Update()
        {
            var frame = GlassesWeb_CopyFrame(m_Buffer, m_Buffer.Length, m_Frame);
            if (frame < 0)
                return false;
            m_Frame = frame;
            Texture.LoadRawTextureData(m_Buffer);
            Texture.Apply(false);
            return true;
        }

        public void Dispose()
        {
            GlassesWeb_Destroy();
            UnityEngine.Object.Destroy(Texture);
        }
    }
}
