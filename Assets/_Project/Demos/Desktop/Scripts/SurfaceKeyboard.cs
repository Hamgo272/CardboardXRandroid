using System;
using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Demos.Desktop
{
    // A keyboard and a touchpad, like Meta Quest's surface keyboard: laid on a real table (or
    // floating, when no table is in view). The key under the index fingertip lights up; tapping the
    // finger down types it. Looking at a key and pinching types it too, which is the sure way.
    // The touchpad, right of the keys, moves a cursor as the fingertip moves over it. Tap to click;
    // swipe its labeled lower strip to scroll.
    //
    // There's no depth sensor, so "touching" is read from motion: the fingertip's distance along the
    // camera's ray drops toward the table and comes back. Where it lands is precise anyway, since it's
    // where the camera's ray through the fingertip meets the table.
    public class SurfaceKeyboard
    {
        const float k_Key = 0.034f;         // key pitch, meters: bigger than a real keyboard's, for camera taps
        const float k_Gap = 0.003f;
        const float k_PadWidth = 0.14f;
        const float k_PadHeight = 0.13f;
        const float k_PadScrollHeight = 0.032f;
        // A tap: the fingertip comes this much closer to the table, within k_TapWindow, then back up.
        const float k_TapDrop = 0.012f;
        const float k_TapRise = 0.006f;
        const float k_TapWindow = 0.25f;
        // Fingertip travel over the pad, in meters, that crosses the whole screen.
        const float k_PadSpan = 0.28f;

        static readonly Color k_KeyColor = new(0.16f, 0.16f, 0.2f);
        static readonly Color k_KeyHot = new(0.3f, 0.75f, 1f);
        static readonly Color k_KeyPressed = new(0.4f, 1f, 0.6f);
        static readonly Color k_PadColor = new(0.12f, 0.12f, 0.15f);
        static readonly Color k_BackplateColor = new(0.055f, 0.06f, 0.08f);
        static readonly Color k_PadScrollColor = new(0.2f, 0.22f, 0.28f);

        static readonly string[] k_Rows = { "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm,./;-" };

        public Action<string> Text;
        public Action Backspace;
        public Action Enter;
        // Cursor point on the screen (0..1, y down) as the finger moves over the pad, and a pad tap.
        public Action<Vector2> PadMove;
        public Action PadTap;
        // Vertical swipe in the pad's labeled lower strip, in meters (up > 0).
        public Action<float> PadScroll;

        public bool Visible => m_Root.gameObject.activeSelf;
        public bool OnTable { get; private set; }

        class Key
        {
            public string Value;        // a character, or "shift", "back", "enter", "space", "hide"
            public Transform Part;
            public Material Material;
            public TextMeshPro Label;
            public float Flash;
        }

        readonly Transform m_Root;
        readonly Material m_Template;
        readonly List<Key> m_Keys = new();
        readonly Transform m_Backplate;
        readonly Transform m_Pad;
        readonly Material m_PadMaterial;
        readonly Transform m_PadScrollZone;
        readonly Material m_PadScrollMaterial;
        Transform m_Camera;
        bool m_Shift;
        bool m_RepeatBackspace;
        float m_NextBackspace;

        // Tap detection.
        readonly Queue<(float time, float gap)> m_Gaps = new();
        float m_MinGap = float.MaxValue;
        Vector3 m_MinPoint;
        float m_MinTime;
        bool m_Armed;
        Vector3? m_LastPadPoint;
        bool m_LastPadWasScroll;
        Vector2 m_Cursor = new(0.5f, 0.5f);

        public SurfaceKeyboard(Transform room, Material material)
        {
            m_Template = material;
            m_Root = new GameObject("Keyboard").transform;
            m_Root.SetParent(room, false);
            m_Backplate = Quad("Keyboard Surface", k_BackplateColor, out _);

            var y = 0f;
            foreach (var row in k_Rows)
            {
                var x = -(row.Length - 1) * k_Key / 2f;
                foreach (var c in row)
                {
                    AddKey(c.ToString(), x, y, 1f);
                    x += k_Key;
                }
                y -= k_Key;
            }
            // Bottom row: each key has a visible gap and an independent hit target.
            AddKey("shift", -4.5f * k_Key, y, 1.8f);
            AddKey("space", -2.6f * k_Key, y, 4.4f);
            AddKey("back", 1.9f * k_Key, y, 1.8f);
            AddKey("enter", 3.8f * k_Key, y, 1.8f);
            AddKey("hide", -4.0f * k_Key, k_Key, 1.8f);

            m_Pad = Quad("Touchpad", k_PadColor, out m_PadMaterial);
            m_Pad.localScale = new Vector3(k_PadWidth, k_PadHeight, 1f);
            m_Pad.localPosition = new Vector3(5.5f * k_Key + k_PadWidth / 2f + 0.03f, -1.5f * k_Key, 0f);
            m_PadScrollZone = Quad("Pad Scroll Zone", k_PadScrollColor, out m_PadScrollMaterial);
            m_PadScrollZone.localScale = new Vector3(k_PadWidth, k_PadScrollHeight, 1f);
            m_PadScrollZone.localPosition = new Vector3(m_Pad.localPosition.x,
                m_Pad.localPosition.y - k_PadHeight / 2f + k_PadScrollHeight / 2f, -0.001f);
            var scrollLabel = new GameObject("Scroll Label");
            scrollLabel.transform.SetParent(m_PadScrollZone, false);
            scrollLabel.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            scrollLabel.transform.localScale = new Vector3(1f / k_PadWidth, 1f / k_PadScrollHeight, 1f);
            var scrollText = scrollLabel.AddComponent<TextMeshPro>();
            var assets = M3Assets.Instance;
            if (assets != null && assets.Medium != null)
                scrollText.font = assets.Medium;
            scrollText.text = "SCROLL";
            scrollText.fontSize = 0.075f;
            scrollText.enableAutoSizing = true;
            scrollText.fontSizeMin = 0.025f;
            scrollText.fontSizeMax = 0.06f;
            scrollText.alignment = TextAlignmentOptions.Center;
            scrollText.textWrappingMode = TextWrappingModes.NoWrap;
            scrollText.color = Color.white;
            scrollText.rectTransform.sizeDelta = new Vector2(k_PadWidth, k_PadScrollHeight);
            scrollText.raycastTarget = false;
            m_Root.gameObject.SetActive(false);
            LayoutBackplate();
        }

        public void Destroy() => UnityEngine.Object.Destroy(m_Root.gameObject);

        public void Hide()
        {
            m_RepeatBackspace = false;
            m_LastPadPoint = null;
            m_LastPadWasScroll = false;
            m_Root.gameObject.SetActive(false);
        }

        // On the table under the gaze if there is one, facing the user; otherwise floating, tilted,
        // below where the user looks.
        public void Show(Transform head)
        {
            m_Root.gameObject.SetActive(true);
            m_RepeatBackspace = false;
            m_LastPadPoint = null;
            m_LastPadWasScroll = false;
            m_PadMaterial.color = k_PadColor;
            m_PadScrollMaterial.color = k_PadScrollColor;
            var flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 1e-4f)
                flatForward = Vector3.ProjectOnPlane(head.up, Vector3.up);
            flatForward.Normalize();
            OnTable = Physics.Raycast(head.position, head.forward, out var hit, 1.5f) && hit.normal.y > 0.8f;
            if (OnTable)
            {
                // Lying on the table: the quads' faces (-z) point up.
                m_Root.position = hit.point + Vector3.up * 0.002f;
                m_Root.rotation = Quaternion.LookRotation(Vector3.down, flatForward);
            }
            else
            {
                m_Root.position = head.position + flatForward * 0.5f + Vector3.down * 0.35f;
                m_Root.rotation = Quaternion.LookRotation(Quaternion.AngleAxis(50f, Vector3.Cross(Vector3.up, flatForward)) * flatForward, Vector3.up);
            }
            Debug.Log($"[Keyboard] Shown {(OnTable ? "on a table" : "floating")}");
        }

        // ---------- every frame ----------

        public void Update(Transform head, HandTracker hands)
        {
            if (!Visible)
                return;
            foreach (var key in m_Keys)
            {
                key.Flash = Mathf.Max(0f, key.Flash - Time.unscaledDeltaTime);
                key.Material.color = key.Flash > 0f ? k_KeyPressed
                    : m_Shift && key.Value == "shift" ? k_KeyHot : k_KeyColor;
            }
            if (m_RepeatBackspace && Time.unscaledTime >= m_NextBackspace)
            {
                Backspace?.Invoke();
                m_NextBackspace = Time.unscaledTime + 0.075f;
            }

            // The gaze's key lights up for look-and-pinch.
            var gazeKey = KeyAt(Hit(new Ray(head.position, head.forward)));
            if (gazeKey != null && gazeKey.Flash <= 0f)
                gazeKey.Material.color = Color.Lerp(k_KeyColor, k_KeyHot, 0.5f);

            if (hands == null || !hands.IsTracking)
            {
                m_RepeatBackspace = false;
                ResetTap();
                m_LastPadPoint = null;
                m_LastPadWasScroll = false;
                m_PadMaterial.color = k_PadColor;
                m_PadScrollMaterial.color = k_PadScrollColor;
                return;
            }
            FingerUpdate(hands);
        }

        void FingerUpdate(HandTracker hands)
        {
            if (m_Camera == null)
                m_Camera = UnityEngine.Object.FindAnyObjectByType<ARCameraManager>()?.transform;
            if (m_Camera == null)
                return;
            var tip = hands.Joints[HandGeometry.IndexTip];
            var origin = m_Camera.position;
            var ray = new Ray(origin, tip - origin);
            var onPlane = Hit(ray);
            if (!onPlane.HasValue)
            {
                ResetTap();
                m_LastPadPoint = null;
                m_LastPadWasScroll = false;
                return;
            }
            var local = onPlane.Value;
            var key = KeyAt(local);
            if (key != null && key.Flash <= 0f)
                key.Material.color = k_KeyHot;

            // The touchpad: relative motion moves the cursor.
            var overPad = Inside(m_Pad, local, 0.01f);
            var overScroll = overPad && Inside(m_PadScrollZone, local, 0f);
            m_PadMaterial.color = overPad ? Color.Lerp(k_PadColor, k_KeyHot, 0.18f) : k_PadColor;
            m_PadScrollMaterial.color = overScroll ? Color.Lerp(k_PadScrollColor, k_KeyHot, 0.45f) : k_PadScrollColor;
            if (overPad)
            {
                if (m_LastPadPoint.HasValue && m_LastPadWasScroll == overScroll)
                {
                    var delta = local - m_LastPadPoint.Value;
                    if (overScroll)
                    {
                        PadScroll?.Invoke(delta.y);
                    }
                    else
                    {
                        m_Cursor = new Vector2(Mathf.Clamp01(m_Cursor.x + delta.x / k_PadSpan),
                            Mathf.Clamp01(m_Cursor.y - delta.y / k_PadSpan));
                        PadMove?.Invoke(m_Cursor);
                    }
                }
                m_LastPadPoint = local;
                m_LastPadWasScroll = overScroll;
            }
            else
            {
                m_LastPadPoint = null;
                m_LastPadWasScroll = false;
            }

            // How far the fingertip is above the table, along the camera's ray. Its absolute value is
            // off by the hand-depth estimate, but how it changes shows a tap.
            if (overScroll)
            {
                ResetTap();
                return;
            }
            var worldOnPlane = m_Root.TransformPoint(local);
            var gap = Vector3.Distance(origin, worldOnPlane) - Vector3.Distance(origin, tip);
            DetectTap(gap, local);
        }

        void DetectTap(float gap, Vector3 local)
        {
            var now = Time.unscaledTime;
            m_Gaps.Enqueue((now, gap));
            while (m_Gaps.Count > 0 && now - m_Gaps.Peek().time > k_TapWindow)
                m_Gaps.Dequeue();
            var highest = float.MinValue;
            foreach (var (_, g) in m_Gaps)
                highest = Mathf.Max(highest, g);

            if (!m_Armed && highest - gap > k_TapDrop)
            {
                // The finger dropped fast: follow it to its lowest point.
                m_Armed = true;
                m_MinGap = gap;
                m_MinPoint = local;
                m_MinTime = now;
            }
            if (!m_Armed)
                return;
            if (gap < m_MinGap)
            {
                m_MinGap = gap;
                m_MinPoint = local;
                m_MinTime = now;
            }
            if (gap - m_MinGap > k_TapRise)
            {
                Press(m_MinPoint, "finger");
                ResetTap();
            }
            else if (now - m_MinTime > k_TapWindow)
            {
                ResetTap();
            }
        }

        void ResetTap()
        {
            m_Armed = false;
            m_MinGap = float.MaxValue;
        }

        // ---------- look and pinch ----------

        public bool TryBeginPinch(Transform head)
        {
            if (!Visible)
                return false;
            var local = Hit(new Ray(head.position, head.forward));
            if (!local.HasValue || (KeyAt(local) == null && !Inside(m_Pad, local.Value, 0f)))
                return false;
            Press(local.Value, "pinch");
            return true;
        }

        public void EndPinch() => m_RepeatBackspace = false;

        void Press(Vector3 local, string how)
        {
            if (Inside(m_Pad, local, 0.01f))
            {
                if (Inside(m_PadScrollZone, local, 0f))
                {
                    PadScroll?.Invoke(0.035f);
                    Debug.Log($"[Keyboard] Pad scroll ({how})");
                }
                else
                {
                    PadTap?.Invoke();
                    Debug.Log($"[Keyboard] Pad tap ({how})");
                }
                return;
            }
            var key = KeyAt(local);
            if (key == null)
                return;
            key.Flash = 0.15f;
            if (key.Value != "back")
                m_RepeatBackspace = false;
            switch (key.Value)
            {
                case "shift":
                    m_Shift = !m_Shift;
                    foreach (var k in m_Keys)
                        if (k.Value.Length == 1)
                            k.Label.text = m_Shift ? ShiftedKey(k.Value) : k.Value;
                    break;
                case "back":
                    Backspace?.Invoke();
                    if (how == "pinch")
                    {
                        m_RepeatBackspace = true;
                        m_NextBackspace = Time.unscaledTime + 0.45f;
                    }
                    break;
                case "enter": Enter?.Invoke(); break;
                case "space": Text?.Invoke(" "); break;
                case "hide": Hide(); break;
                default:
                    Text?.Invoke(m_Shift ? ShiftedKey(key.Value) : key.Value);
                    if (m_Shift)
                        Press(KeyPosition("shift"), how);  // one capital, then back to lower case
                    break;
            }
        }

        // ---------- geometry ----------

        void AddKey(string value, float x, float y, float widthInKeys)
        {
            var part = Quad("Key " + value, k_KeyColor, out var material);
            var width = widthInKeys * k_Key - k_Gap;
            part.localScale = new Vector3(width, k_Key - k_Gap, 1f);
            part.localPosition = new Vector3(x + (widthInKeys - 1f) * k_Key / 2f, y, 0f);

            var label = new GameObject("Label");
            label.transform.SetParent(part, false);
            label.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            label.transform.localScale = new Vector3(1f / width, 1f / (k_Key - k_Gap), 1f);
            var text = label.AddComponent<TextMeshPro>();
            var assets = M3Assets.Instance;
            if (assets != null && assets.Medium != null)
                text.font = assets.Medium;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.fontSize = value.Length == 1 ? 0.16f : 0.1f;
            text.color = Color.white;
            text.text = value.Length == 1 ? value : value switch
            {
                "back" => "Del",
                "enter" => "Enter",
                "space" => "space",
                "shift" => "Shift",
                _ => "Hide",
            };
            text.rectTransform.sizeDelta = new Vector2(width, k_Key);
            m_Keys.Add(new Key { Value = value, Part = part, Material = material, Label = text });
        }

        Vector3 KeyPosition(string value) => m_Keys.Find(k => k.Value == value).Part.localPosition;

        static string ShiftedKey(string value) => value switch
        {
            "1" => "!", "2" => "@", "3" => "#", "4" => "$", "5" => "%",
            "6" => "^", "7" => "&", "8" => "*", "9" => "(", "0" => ")",
            "," => "<", "." => ">", "/" => "?", ";" => ":", "-" => "_",
            _ => value.ToUpperInvariant(),
        };

        void LayoutBackplate()
        {
            var minX = float.PositiveInfinity;
            var maxX = float.NegativeInfinity;
            var minY = float.PositiveInfinity;
            var maxY = float.NegativeInfinity;
            foreach (var key in m_Keys)
            {
                var center = key.Part.localPosition;
                var half = key.Part.localScale / 2f;
                minX = Mathf.Min(minX, center.x - half.x);
                maxX = Mathf.Max(maxX, center.x + half.x);
                minY = Mathf.Min(minY, center.y - half.y);
                maxY = Mathf.Max(maxY, center.y + half.y);
            }
            minX = Mathf.Min(minX, m_Pad.localPosition.x - k_PadWidth / 2f);
            maxX = Mathf.Max(maxX, m_Pad.localPosition.x + k_PadWidth / 2f);
            minY = Mathf.Min(minY, m_Pad.localPosition.y - k_PadHeight / 2f);
            maxY = Mathf.Max(maxY, m_Pad.localPosition.y + k_PadHeight / 2f);
            const float margin = 0.012f;
            m_Backplate.localScale = new Vector3(maxX - minX + margin * 2f, maxY - minY + margin * 2f, 1f);
            m_Backplate.localPosition = new Vector3((minX + maxX) / 2f, (minY + maxY) / 2f, 0.001f);
        }

        Transform Quad(string name, Color color, out Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Quad);
            part.name = name;
            UnityEngine.Object.Destroy(part.GetComponent<Collider>());
            part.transform.SetParent(m_Root, false);
            material = new Material(m_Template) { color = color };
            part.GetComponent<MeshRenderer>().sharedMaterial = material;
            return part.transform;
        }

        // Where the ray meets the keyboard's plane, in its own space, or null.
        Vector3? Hit(Ray ray)
        {
            var plane = new Plane(m_Root.forward, m_Root.position);
            if (!plane.Raycast(ray, out var distance) || distance <= 0f)
                return null;
            return m_Root.InverseTransformPoint(ray.GetPoint(distance));
        }

        Key KeyAt(Vector3? local)
        {
            if (!local.HasValue)
                return null;
            foreach (var key in m_Keys)
                if (Inside(key.Part, local.Value, 0f))
                    return key;
            return null;
        }

        static bool Inside(Transform part, Vector3 local, float pad)
        {
            var c = part.localPosition;
            var s = part.localScale;
            return Mathf.Abs(local.x - c.x) <= s.x / 2f + pad && Mathf.Abs(local.y - c.y) <= s.y / 2f + pad;
        }
    }
}
