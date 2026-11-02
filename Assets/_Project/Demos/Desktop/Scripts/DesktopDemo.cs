using CardboardXR.Hands;
using UnityEngine;

namespace CardboardXR.Demos.Desktop
{
    // The computer's screen (Tools/screen-server) on a FloatingScreen in the room, at any size:
    // taps click the real mouse, scrolling turns the wheel, a held pinch drags. When the computer
    // can't be found, a pinch on the screen offers to type its address.
    public class DesktopDemo : Demo, IPinchHandler
    {
        [Tooltip("Unlit material the screen's parts are made from.")]
        [SerializeField] Material m_Material;

        // Wheel units (120 a notch) per meter of hand travel while scrolling.
        const float k_WheelPerMeter = 6000f;

        CoreServices m_Core;
        DesktopLink m_Link;
        FloatingScreen m_Screen;
        float m_Wheel;
        TouchScreenKeyboard m_Keyboard;
        SurfaceKeyboard m_Keys;
        Vector2 m_Cursor = new(0.5f, 0.5f);

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            // A pinch here never places a cube on a surface.
            core.Placer.ObjectPrefab = null;
            PinchRouter.Handler = this;
            m_Link = gameObject.AddComponent<DesktopLink>();
            m_Screen = new FloatingScreen("Computer Screen", core.Boundary.transform, m_Material)
            {
                Tap = p => m_Link.Send("click", p),
                HoldDown = p => m_Link.Send("down", p),
                HoldMove = p => m_Link.Send("move", p),
                HoldUp = p => m_Link.Send("up", p),
                Scroll = Wheel,
                Idle = AskAddress,
            };
            m_Screen.AddButton("Keyboard", () =>
            {
                if (m_Keys.Visible)
                    m_Keys.Hide();
                else
                    m_Keys.Show(m_Core.Head);
            });
            m_Keys = new SurfaceKeyboard(core.Boundary.transform, m_Material)
            {
                Text = t => m_Link.SendText(t),
                Backspace = () => m_Link.SendKey("back"),
                Enter = () => m_Link.SendKey("enter"),
                PadMove = p => { m_Cursor = p; m_Screen.SetCursor(p); m_Link.Send("move", p); },
                PadTap = () => m_Link.Send("click", m_Cursor),
            };
            m_Screen.PlaceInFront(core.Head);
            Debug.Log("[Desktop] Ready: looking for the computer");
        }

        public override void OnDemoExit(CoreServices core)
        {
            if (ReferenceEquals(PinchRouter.Handler, this))
                PinchRouter.Clear();
            if (m_Link != null)
                Destroy(m_Link);
            m_Screen?.Destroy();
            m_Screen = null;
            m_Keys?.Destroy();
            m_Keys = null;
        }

        void Update()
        {
            if (m_Screen == null)
                return;
            var streaming = m_Link.State == DesktopLink.Status.Streaming && m_Link.Frame != null;
            m_Screen.Interactive = streaming;
            m_Screen.Show(streaming ? m_Link.Frame : null,
                streaming ? (float)m_Link.Frame.height / m_Link.Frame.width : 0f, Message());
            m_Screen.Update(m_Core.Head, m_Core.HandTracker);
            m_Keys.Update(m_Core.Head, m_Core.HandTracker);
            if (!m_Keys.Visible)
                m_Screen.SetCursor(null);

            if (m_Keyboard != null)
            {
                if (m_Keyboard.status == TouchScreenKeyboard.Status.Done)
                    m_Link.SetHost(m_Keyboard.text);
                if (m_Keyboard.status != TouchScreenKeyboard.Status.Visible)
                    m_Keyboard = null;
            }
        }

        string Message() => m_Link.State switch
        {
            DesktopLink.Status.WaitingApproval =>
                $"Found your computer ({m_Link.Host}).\nPress Approve on the computer's sharing page.",
            DesktopLink.Status.WaitingShare => "Connected.\nPress Share on the computer and pick the entire screen.",
            _ => "Looking for your computer...\nOn the computer, run Tools/screen-server/start.bat.\n" +
                 "The phone and the computer must be on the same Wi-Fi.\nPinch here to type its address.",
        };

        // Like a touchscreen: the hand moving up moves the page up, which is the wheel turning down.
        void Wheel(Vector2 point, float meters)
        {
            m_Wheel -= meters * k_WheelPerMeter;
            var notches = (int)(m_Wheel / 120f);
            if (notches == 0)
                return;
            m_Link.Send("wheel", point, notches * 120f);
            m_Wheel -= notches * 120f;
        }

        void AskAddress()
        {
            if (m_Link.State == DesktopLink.Status.Searching && m_Keyboard == null)
                m_Keyboard = TouchScreenKeyboard.Open(m_Link.Host ?? "", TouchScreenKeyboardType.URL, false, false, false, false,
                    "Computer address, e.g. 192.168.1.20");
        }

        public bool TryBeginPinch(GameObject gazeTarget, PinchSource source) =>
            m_Screen != null && (m_Keys.TryBeginPinch(m_Core.Head) || m_Screen.TryBeginPinch(m_Core.Head, source, m_Core.HandTracker));

        public void EndPinch() => m_Screen?.EndPinch(m_Core.Head);
    }
}
