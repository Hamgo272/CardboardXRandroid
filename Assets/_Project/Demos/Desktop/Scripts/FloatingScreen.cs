using System;
using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.UI;
using TMPro;
using UnityEngine;

namespace CardboardXR.Demos.Desktop
{
    // A flat screen anchored in the room, like a visionOS window, for whatever a demo shows on it.
    // A closed-fist vertical swipe scrolls the page; pinch is reserved for tapping and holding.
    // The bar below moves the screen (it follows the gaze; moving the pinched hand forward or back
    // pushes it away or pulls it closer); the corner resizes it to where you look. Toolbar buttons
    // run the demo's commands. Positions in the picture are 0..1, y down.
    public class FloatingScreen
    {
        const float k_StartWidth = 1.2f;
        const float k_StartDistance = 1.8f;
        const float k_MinWidth = 0.3f;
        const float k_MaxWidth = 12f;
        const float k_MinDistance = 0.5f;
        const float k_MaxDistance = 10f;
        // Extend index + middle fingers (a two-finger/peace gesture) and swipe vertically.
        // This is deliberately distinct from thumb-index pinch-to-click and open-palm Settings.
        const float k_TwoFingerScrollReach = 0.28f;
        const float k_TwoFingerScrollStart = 0.003f;
        // A pinch held this long without scrolling becomes a drag.
        const float k_HoldTime = 0.35f;
        // Moving the pinched hand forward by 1 m pushes the screen this many meters away.
        const float k_PushGain = 3f;
        // Gaze slack around handles and buttons, in meters.
        const float k_Pad = 0.03f;

        static readonly Color k_Handle = new(0.42f, 0.47f, 0.62f);
        static readonly Color k_Hot = new(0.72f, 0.62f, 1f);
        static readonly Color k_Backing = new(0.035f, 0.043f, 0.075f);
        static readonly Color k_Toolbar = new(0.075f, 0.09f, 0.15f);
        static readonly Color k_Button = new(0.15f, 0.17f, 0.27f);

        // Tap at a point; a held pinch (down, moves, up); scroll by hand travel in meters (up > 0).
        public Action<Vector2> Tap;
        public Action<Vector2> HoldDown;
        public Action<Vector2> HoldMove;
        public Action<Vector2> HoldUp;
        public Action<Vector2, float> Scroll;
        // A pinch on the picture while it isn't Interactive (nothing to tap yet).
        public Action Idle;
        public bool Interactive;

        public Transform Root => m_Root;
        public float Width => m_Width;

        class Button
        {
            public Transform Part;
            public Material Material;
            public TextMeshPro Icon;
            public TextMeshPro Label;
            public Action Click;
            public bool HasIcon;
        }

        enum Drag
        {
            None,
            Pending,
            Hold,
            Move,
            Resize,
            ScrollGesture,
        }

        readonly Material m_Template;
        readonly Transform m_Root;
        readonly Transform m_Content;
        readonly Transform m_Backing;
        readonly Transform m_Toolbar;
        readonly Transform m_Bar;
        readonly Transform m_Corner;
        readonly Material m_ContentMaterial;
        readonly Material m_BarMaterial;
        readonly Material m_CornerMaterial;
        readonly TextMeshPro m_Label;
        readonly Transform m_Cursor;
        readonly List<Button> m_Buttons = new();
        float m_Width = k_StartWidth;
        float m_Aspect = 9f / 16f;

        Drag m_Drag;
        PinchSource m_Source;
        float m_PinchStart;
        Vector3 m_HandStart;
        Vector3 m_LastHand;
        Vector2 m_PinchPoint;
        float m_MoveDistance;
        Vector3 m_GrabOffset;
        bool m_TwoFingerScrollActive;
        Vector3 m_LastTwoFingerScrollPoint;
        float m_ResizeStartWidth;
        Vector3 m_ResizeStartHand;
        Vector3 m_ResizeRight;

        public FloatingScreen(string name, Transform room, Material material)
        {
            m_Template = material;
            m_Root = new GameObject(name).transform;
            m_Root.SetParent(room, false);
            m_Backing = Part("Backing", k_Backing, out _);
            m_Content = Part("Content", k_Backing, out m_ContentMaterial);
            m_Toolbar = Part("Toolbar", k_Toolbar, out _);
            m_Bar = Part("Move Bar", k_Handle, out m_BarMaterial);
            m_Corner = Part("Resize Handle", k_Handle, out m_CornerMaterial);
            m_Cursor = Part("Cursor", Color.white, out _);
            m_Cursor.gameObject.SetActive(false);
            m_Label = Text(m_Root, "Status");
            m_Label.enableAutoSizing = true;
            m_Label.fontSizeMin = 0.05f;
            m_Label.fontSizeMax = 1.2f;
            Layout();
        }

        public void Destroy() => UnityEngine.Object.Destroy(m_Root.gameObject);

        public void AddButton(string label, Action click, string icon = null)
        {
            var part = Part("Button " + label, k_Button, out var material);
            var iconText = Text(part, "Icon");
            var labelText = Text(part, "Label");
            var assets = M3Assets.Instance;
            var glyph = M3Icons.Glyph(icon);
            var hasIcon = assets != null && assets.Icons != null && !string.IsNullOrEmpty(glyph) &&
                          M3Icons.Codepoints.TryGetValue(icon, out var codepoint) &&
                          assets.Icons.characterLookupTable.ContainsKey((uint)codepoint);
            if (assets != null)
            {
                if (assets.Icons != null)
                    iconText.font = assets.Icons;
                if (assets.Medium != null)
                    labelText.font = assets.Medium;
            }
            m_Buttons.Add(new Button
            {
                Part = part,
                Material = material,
                Icon = iconText,
                Label = labelText,
                Click = click,
                HasIcon = hasIcon,
            });
            iconText.text = glyph;
            labelText.text = label;
            Layout();
        }

        public void PlaceInFront(Transform head)
        {
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
            forward.Normalize();
            m_Root.position = head.position + forward * k_StartDistance;
            m_Root.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        // The picture, and its height over width; null shows the message instead.
        public void Show(Texture picture, float aspect, string message)
        {
            if (picture != null)
            {
                if (m_ContentMaterial.mainTexture != picture)
                {
                    m_ContentMaterial.mainTexture = picture;
                    m_ContentMaterial.color = Color.white;
                }
                if (aspect > 0f && Mathf.Abs(aspect - m_Aspect) > 0.005f)
                {
                    m_Aspect = aspect;
                    Layout();
                }
            }
            else if (m_ContentMaterial.mainTexture != null || m_ContentMaterial.color != k_Backing)
            {
                m_ContentMaterial.mainTexture = null;
                m_ContentMaterial.color = k_Backing;
            }
            m_Label.text = picture != null ? "" : message;
        }

        // The touchpad's cursor at a point in the picture (0..1, y down), or hidden.
        public void SetCursor(Vector2? point)
        {
            m_Cursor.gameObject.SetActive(point.HasValue);
            if (!point.HasValue)
                return;
            var size = Mathf.Clamp(m_Width * 0.012f, 0.008f, 0.06f);
            m_Cursor.localScale = new Vector3(size, size, 1f);
            m_Cursor.localPosition = new Vector3((point.Value.x - 0.5f) * m_Width,
                (0.5f - point.Value.y) * m_Width * m_Aspect, -0.004f);
        }

        // ---------- every frame ----------

        public void Update(Transform head, HandTracker hands)
        {
            var gaze = Hit(new Ray(head.position, head.forward));
            var idle = m_Drag == Drag.None && gaze.HasValue;
            var hoveredButton = idle ? NearestButton(gaze.Value) : null;
            m_BarMaterial.color = m_Drag == Drag.Move || (idle && Over(m_Bar, gaze.Value)) ? k_Hot : k_Handle;
            m_CornerMaterial.color = m_Drag == Drag.Resize || (idle && Over(m_Corner, gaze.Value)) ? k_Hot : k_Handle;
            foreach (var button in m_Buttons)
            {
                var hot = button == hoveredButton;
                button.Material.color = hot ? k_Hot : k_Button;
                button.Icon.color = button.Label.color = hot ? k_Backing : Color.white;
            }

            var hand = HandPoint(hands);
            // If a scroll pose begins during a pending pinch, cancel the pending click before
            // dispatching scroll. This prevents a scroll-to-click race on gesture transitions.
            if (m_Drag == Drag.Pending && hands != null && IsTwoFingerScroll(hands.Joints))
                m_Drag = Drag.None;
            UpdateTwoFingerScroll(hands);
            switch (m_Drag)
            {
                case Drag.Pending:
                    // Pinch is exclusively for clicking/holding. It never transitions into scrolling.
                    if (Time.unscaledTime - m_PinchStart > k_HoldTime)
                    {
                        m_Drag = Drag.Hold;
                        HoldDown?.Invoke(m_PinchPoint);
                    }
                    break;
                case Drag.Hold:
                    var point = PicturePoint(gaze);
                    if (point.HasValue)
                        HoldMove?.Invoke(point.Value);
                    break;
                case Drag.Move:
                    if (m_Source == PinchSource.Hand && hands.IsTracking)
                    {
                        m_MoveDistance = Mathf.Clamp(m_MoveDistance + Vector3.Dot(hand - m_LastHand, head.forward) * k_PushGain,
                            k_MinDistance, k_MaxDistance);
                        m_LastHand = hand;
                    }
                    // The point grabbed on the bar stays under the gaze, and the screen faces you.
                    var target = head.position + head.forward * m_MoveDistance - m_Root.TransformVector(m_GrabOffset);
                    m_Root.position = Vector3.Lerp(m_Root.position, target, 0.5f);
                    var facing = Vector3.ProjectOnPlane(m_Root.position - head.position, Vector3.up);
                    if (facing.sqrMagnitude > 1e-4f)
                        m_Root.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up);
                    break;
                case Drag.Resize:
                    if (m_Source == PinchSource.Hand && hands != null && hands.IsTracking)
                    {
                        // Resize from the hand's horizontal travel relative to the width at pinch start.
                        // The handle stays fixed for the duration of the gesture, avoiding gaze feedback jitter.
                        var delta = Vector3.Dot(hand - m_ResizeStartHand, m_ResizeRight);
                        m_Width = Mathf.Clamp(m_ResizeStartWidth + delta * 2f, k_MinWidth, k_MaxWidth);
                        Layout();
                    }
                    break;
            }
        }

        // Two-finger swipe: extend index and middle fingers, curl ring and little fingers,
        // then move vertically over the page. Thumb position is ignored, so it cannot resemble pinch.
        void UpdateTwoFingerScroll(HandTracker hands)
        {
            if ((m_Drag != Drag.None && m_Drag != Drag.ScrollGesture) || hands == null || !hands.isActiveAndEnabled ||
                !hands.IsTracking || !IsTwoFingerScroll(hands.Joints))
            {
                m_TwoFingerScrollActive = false;
                return;
            }

            var joints = hands.Joints;
            var gesturePoint = (joints[HandGeometry.IndexTip] + joints[12]) * 0.5f;
            var local = m_Root.InverseTransformPoint(gesturePoint);
            var height = m_Width * m_Aspect;
            var overPage = Mathf.Abs(local.z) <= k_TwoFingerScrollReach &&
                           Mathf.Abs(local.x) <= m_Width * 0.5f &&
                           Mathf.Abs(local.y) <= height * 0.5f;
            if (!overPage)
            {
                m_TwoFingerScrollActive = false;
                return;
            }

            if (!m_TwoFingerScrollActive)
            {
                m_TwoFingerScrollActive = true;
                m_LastTwoFingerScrollPoint = local;
                return;
            }

            var delta = local.y - m_LastTwoFingerScrollPoint.y;
            m_LastTwoFingerScrollPoint = local;
            if (Mathf.Abs(delta) < k_TwoFingerScrollStart)
                return;
            var point = new Vector2(local.x / m_Width + 0.5f, 0.5f - local.y / height);
            Scroll?.Invoke(point, delta);
        }

        static bool IsTwoFingerScroll(Vector3[] joints)
        {
            if (joints == null || joints.Length < HandGeometry.JointCount)
                return false;
            var wrist = joints[0];
            bool Extended(int tip, int pip) =>
                Vector3.Distance(joints[tip], wrist) > Vector3.Distance(joints[pip], wrist) * 1.12f;
            bool Curled(int tip, int pip) =>
                Vector3.Distance(joints[tip], wrist) < Vector3.Distance(joints[pip], wrist) * 1.08f;
            return Extended(HandGeometry.IndexTip, 6) &&
                   Extended(12, 10) &&
                   Curled(16, 14) &&
                   Curled(20, 18);
        }

        // ---------- pinches ----------

        public bool TryBeginPinch(Transform head, PinchSource source, HandTracker hands)
        {
            // Claim the pinch while the scroll pose is active. Returning false would let
            // PinchRouter fall through to a normal gaze click, which is the click/scroll conflict.
            if (source == PinchSource.Hand && hands != null && IsTwoFingerScroll(hands.Joints))
            {
                m_Source = source;
                m_Drag = Drag.ScrollGesture;
                return true;
            }

            var local = Hit(new Ray(head.position, head.forward));
            if (!local.HasValue)
                return false;
            m_Source = source;
            m_PinchStart = Time.unscaledTime;
            m_HandStart = m_LastHand = HandPoint(hands);

            var button = NearestButton(local.Value);
            if (button != null)
            {
                CardboardXR.Interaction.AndroidHaptics.Confirm();
                button.Click?.Invoke();
                m_Drag = Drag.None;
                return true;
            }
            if (Over(m_Bar, local.Value) || Over(m_Corner, local.Value))
            {
                m_Drag = Over(m_Corner, local.Value) ? Drag.Resize : Drag.Move;
                if (m_Drag == Drag.Resize)
                {
                    m_ResizeStartWidth = m_Width;
                    m_ResizeStartHand = HandPoint(hands);
                    m_ResizeRight = m_Root.right;
                }
                m_MoveDistance = Vector3.Distance(head.position, m_Root.TransformPoint(local.Value));
                m_GrabOffset = local.Value;
                return true;
            }
            var point = PicturePoint(local);
            if (!point.HasValue)
                return false;
            if (!Interactive)
            {
                Idle?.Invoke();
                return true;
            }
            m_PinchPoint = point.Value;
            m_Drag = Drag.Pending;
            return true;
        }

        public void EndPinch(Transform head)
        {
            switch (m_Drag)
            {
                case Drag.Pending:
                    Tap?.Invoke(m_PinchPoint);
                    break;
                case Drag.Hold:
                    HoldUp?.Invoke(PicturePoint(Hit(new Ray(head.position, head.forward))) ?? m_PinchPoint);
                    break;
                case Drag.Move:
                case Drag.Resize:
                    Debug.Log($"[Screen] {m_Root.name}: {m_Width:F2} m wide, {Vector3.Distance(head.position, m_Root.position):F2} m away");
                    break;
                case Drag.ScrollGesture:
                    // This pinch was claimed only to prevent PinchRouter's gaze-click fallback.
                    break;
            }
            m_Drag = Drag.None;
        }

        // ---------- geometry ----------

        // A flat part facing the user. Gaze is tested against the screen's plane, not colliders, so
        // parts have none: the app's own gaze and cube placement never see them.
        Transform Part(string name, Color color, out Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Quad);
            part.name = name;
            UnityEngine.Object.Destroy(part.GetComponent<Collider>());
            part.transform.SetParent(m_Root, false);
            material = new Material(m_Template) { color = color };
            part.GetComponent<MeshRenderer>().sharedMaterial = material;
            return part.transform;
        }

        static TextMeshPro Text(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, -0.003f);
            var text = go.AddComponent<TextMeshPro>();
            var assets = M3Assets.Instance;
            if (assets != null && assets.Regular != null)
                text.font = assets.Regular;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.color = Color.white;
            return text;
        }

        // Unity's quad faces -z, so the root's forward points away from the user.
        void Layout()
        {
            var height = m_Width * m_Aspect;
            m_Content.localScale = new Vector3(m_Width, height, 1f);
            // The backing includes the bottom control shelf, making the browser feel like one window.
            const float shelfGap = 0.035f;
            var barWidth = Mathf.Clamp(m_Width * 0.25f, 0.12f, 0.6f);
            m_Bar.localScale = new Vector3(barWidth, 0.035f, 1f);
            m_Bar.localPosition = new Vector3(0f, height / 2f + 0.055f, 0f);
            var corner = Mathf.Clamp(m_Width * 0.045f, 0.055f, 0.22f);
            m_Corner.localScale = new Vector3(corner, corner, 1f);
            m_Corner.localPosition = new Vector3(m_Width / 2f - corner * 0.35f, -height / 2f - corner * 0.35f, -0.002f);
            m_Label.rectTransform.sizeDelta = new Vector2(m_Width * 0.85f, height * 0.85f);

            // Large, gaze-friendly toolbar buttons. Keep at most four columns so adding
            // controls creates additional rows instead of squeezing every button smaller.
            const float buttonHeight = 0.19f, gap = 0.022f, edge = 0.035f, minButtonWidth = 0.22f;
            var available = Mathf.Max(0.20f, m_Width - edge * 2f);
            var columns = Mathf.Max(1, Mathf.FloorToInt((available + gap) / (minButtonWidth + gap)));
            columns = Mathf.Min(4, Mathf.Min(columns, Mathf.Max(1, m_Buttons.Count)));
            var buttonWidth = (available - gap * (columns - 1)) / columns;
            var rows = Mathf.Max(1, (m_Buttons.Count + columns - 1) / columns);
            var toolbarHeight = rows * buttonHeight + (rows - 1) * gap + 0.03f;
            var toolbarBottom = -height / 2f - shelfGap;
            var toolbarCenter = toolbarBottom - toolbarHeight / 2f;
            m_Toolbar.gameObject.SetActive(m_Buttons.Count > 0);
            m_Toolbar.localScale = new Vector3(m_Width + 0.04f, toolbarHeight, 1f);
            m_Toolbar.localPosition = new Vector3(0f, toolbarCenter, 0.006f);
            m_Backing.localScale = new Vector3(m_Width + 0.04f, height + toolbarHeight + shelfGap + 0.04f, 1f);
            m_Backing.localPosition = new Vector3(0f, -(toolbarHeight + shelfGap) / 2f, 0.005f);

            for (var i = 0; i < m_Buttons.Count; i++)
            {
                var button = m_Buttons[i];
                var row = i / columns;
                var column = i % columns;
                var rowCount = Mathf.Min(columns, m_Buttons.Count - row * columns);
                var rowWidth = rowCount * buttonWidth + (rowCount - 1) * gap;
                var x = -rowWidth / 2f + buttonWidth / 2f + column * (buttonWidth + gap);
                var y = toolbarBottom - 0.015f - buttonHeight / 2f - row * (buttonHeight + gap);
                button.Part.localScale = new Vector3(buttonWidth, buttonHeight, 1f);
                button.Part.localPosition = new Vector3(x, y, -0.002f);

                var textScale = new Vector3(1f / buttonWidth, 1f / buttonHeight, 1f);
                button.Icon.transform.localScale = textScale;
                button.Label.transform.localScale = textScale;
                button.Icon.rectTransform.sizeDelta = new Vector2(buttonWidth, 0.095f);
                button.Label.rectTransform.sizeDelta = new Vector2(buttonWidth, 0.06f);
                // The label and icon are inversely scaled with their button. TMP's font size is
                // therefore in the button's unscaled space, not meters.
                button.Icon.fontSize = 0.82f;
                button.Label.fontSize = 0.40f;
                button.Icon.textWrappingMode = TextWrappingModes.NoWrap;
                button.Label.textWrappingMode = TextWrappingModes.NoWrap;
                button.Icon.overflowMode = TextOverflowModes.Overflow;
                button.Label.overflowMode = TextOverflowModes.Ellipsis;
                button.Icon.gameObject.SetActive(button.HasIcon);
                button.Label.transform.localPosition = new Vector3(0f,
                    (button.HasIcon ? -0.048f : 0f) / buttonHeight, -0.01f);
                if (button.HasIcon)
                    button.Icon.transform.localPosition = new Vector3(0f, 0.035f / buttonHeight, -0.01f);
            }
        }

        static Vector3 HandPoint(HandTracker hands) => hands != null && hands.IsTracking ? hands.PinchPoint : Vector3.zero;

        // Where the ray meets the screen's plane, in the screen's own space (meters), or null.
        Vector3? Hit(Ray ray)
        {
            var plane = new Plane(m_Root.forward, m_Root.position);
            if (!plane.Raycast(ray, out var distance) || distance <= 0f)
                return null;
            return m_Root.InverseTransformPoint(ray.GetPoint(distance));
        }

        static bool Over(Transform part, Vector3 local)
        {
            var center = part.localPosition;
            var size = part.localScale;
            return Mathf.Abs(local.x - center.x) <= size.x / 2f + k_Pad &&
                   Mathf.Abs(local.y - center.y) <= size.y / 2f + k_Pad;
        }

        Button NearestButton(Vector3 local)
        {
            Button nearest = null;
            var nearestDistance = float.PositiveInfinity;
            foreach (var button in m_Buttons)
            {
                if (!Over(button.Part, local))
                    continue;
                var distance = (local - button.Part.localPosition).sqrMagnitude;
                if (distance >= nearestDistance)
                    continue;
                nearest = button;
                nearestDistance = distance;
            }
            return nearest;
        }

        // The point in the picture (0..1, y down), or null off the picture.
        Vector2? PicturePoint(Vector3? local)
        {
            if (!local.HasValue)
                return null;
            var u = local.Value.x / m_Width + 0.5f;
            var v = local.Value.y / (m_Width * m_Aspect) + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f)
                return null;
            return new Vector2(u, 1f - v);
        }
    }
}
