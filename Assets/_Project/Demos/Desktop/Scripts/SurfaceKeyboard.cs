using System;
using System.Collections.Generic;
using CardboardXR.Hands;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Demos.Desktop
{
    // A keyboard and a touchpad, like Meta Quest's surface keyboard: laid on a real table (or
    // floating, when no table is in view). The key under the index fingertip lights up; tapping the
    // finger down types it. Looking at a key and pinching types it too, which is the sure way.
    // The touchpad, right of the keys, moves a cursor on the screen as the fingertip moves over it;
    // a tap on it clicks.
    //
    // There's no depth sensor, so "touching" is read from motion: the fingertip's distance along the
    // camera's ray drops toward the table and comes back. Where it lands is precise anyway, since it's
    // where the camera's ray through the fingertip meets the table.
    public class SurfaceKeyboard
    {
        const float k_Key = 0.03f;          // key pitch, meters: bigger than a real keyboard's, for camera taps
        const float k_Gap = 0.003f;
        const float k_PadWidth = 0.13f;
        const float k_PadHeight = 0.1f;
        // A tap: the fingertip comes this much closer to the table, within k_TapWindow, then back up.
        const float k_TapDrop = 0.012f;
        const float k_TapRise = 0.006f;
        const float k_TapWindow = 0.25f;
        // Fingertip travel over the pad, in meters, that crosses the whole screen.
        const float k_PadSpan = 0.1f;

        static readonly Color k_KeyColor = new(0.16f, 0.16f, 0.2f);
        static readonly Color k_KeyHot = new(0.3f, 0.75f, 1f);
        static readonly Color k_KeyPressed = new(0.4f, 1f, 0.6f);
        static readonly Color k_PadColor = new(0.12f, 0.12f, 0.15f);

        static readonly string[] k_Rows = { "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm" };

        public Action<string> Text;
        public Action Backspace;
        public Action Enter;
        // Cursor point on the screen (0..1, y down) as the finger moves over the pad, and a pad tap.
        public Action<Vector2> PadMove;
        public Action PadTap;

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
        readonly Transform m_Pad;
        readonly Material m_PadMaterial;
        Transform m_Camera;
        bool m_Shift;

        // Tap detection.
        readonly Queue<(float time, float gap)> m_Gaps = new();
        float m_MinGap = float.MaxValue;
        Vector3 m_MinPoint;
        float m_MinTime;
        bool m_Armed;
        Vector3? m_LastPadPoint;
        Vector2 m_Cursor = new(0.5f, 0.5f);

        public SurfaceKeyboard(Transform room, Material material)
        {
            m_Template = material;
            m_Root = new GameObject("Keyboard").transform;
            m_Root.SetParent(room, false);

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
            // Bottom row: shift, space, backspace, enter, hide.
            AddKey("shift", -4.0f * k_Key, y, 1.8f);
            AddKey("space", -0.6f * k_Key, y, 4.6f);
            AddKey("back", 2.8f * k_Key, y, 1.8f);
            AddKey("enter", 4.6f * k_Key, y, 1.6f);
            AddKey("hide", -4.0f * k_Key, k_Key, 1.8f);

            m_Pad = Quad("Touchpad", k_PadColor, out m_PadMaterial);
            m_Pad.localScale = new Vector3(k_PadWidth, k_PadHeight, 1f);
            m_Pad.localPosition = new Vector3(5.5f * k_Key + k_PadWidth / 2f + 0.03f, -1.5f * k_Key, 0f);
            m_Root.gameObject.SetActive(false);
        }

        public void Destroy() => UnityEngine.Object.Destroy(m_Root.gameObject);

        public void Hide() => m_Root.gameObject.SetActive(false);

        // On the table under the gaze if there is one, facing the user; otherwise floating, tilted,
        // below where the user looks.
        public void Show(Transform head)
        {
            m_Root.gameObject.SetActive(true);
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
                key.Material.color = key.Flash > 0f ? k_KeyPressed : k_KeyColor;
            }

            // The gaze's key lights up for look-and-pinch.
            var gazeKey = KeyAt(Hit(new Ray(head.position, head.forward)));
            if (gazeKey != null && gazeKey.Flash <= 0f)
                gazeKey.Material.color = Color.Lerp(k_KeyColor, k_KeyHot, 0.5f);

            if (hands == null || !hands.IsTracking)
            {
                ResetTap();
                m_LastPadPoint = null;
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
                return;
            }
            var local = onPlane.Value;
            var key = KeyAt(local);
            if (key != null && key.Flash <= 0f)
                key.Material.color = k_KeyHot;

            // The touchpad: relative motion moves the cursor.
            var overPad = Inside(m_Pad, local, 0.01f);
            m_PadMaterial.color = overPad ? Color.Lerp(k_PadColor, k_KeyHot, 0.25f) : k_PadColor;
            if (overPad)
            {
                if (m_LastPadPoint.HasValue)
                {
                    var delta = local - m_LastPadPoint.Value;
                    m_Cursor = new Vector2(Mathf.Clamp01(m_Cursor.x + delta.x / k_PadSpan),
                        Mathf.Clamp01(m_Cursor.y - delta.y / k_PadSpan));
                    PadMove?.Invoke(m_Cursor);
                }
                m_LastPadPoint = local;
            }
            else
            {
                m_LastPadPoint = null;
            }

            // How far the fingertip is above the table, along the camera's ray. Its absolute value is
            // off by the hand-depth estimate, but how it changes shows a tap.
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

        void Press(Vector3 local, string how)
        {
            if (Inside(m_Pad, local, 0.01f))
            {
                PadTap?.Invoke();
                Debug.Log($"[Keyboard] Pad tap ({how})");
                return;
            }
            var key = KeyAt(local);
            if (key == null)
                return;
            key.Flash = 0.15f;
            switch (key.Value)
            {
                case "shift":
                    m_Shift = !m_Shift;
                    foreach (var k in m_Keys)
                        if (k.Value.Length == 1)
                            k.Label.text = m_Shift ? k.Value.ToUpperInvariant() : k.Value;
                    break;
                case "back": Backspace?.Invoke(); break;
                case "enter": Enter?.Invoke(); break;
                case "space": Text?.Invoke(" "); break;
                case "hide": Hide(); break;
                default:
                    Text?.Invoke(m_Shift ? key.Value.ToUpperInvariant() : key.Value);
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
