using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.UI
{
    // The floating windows and panels in the current scene, so the settings panel's "Arrange windows"
    // can gather them around the user, as Android XR's "reposition" does: side by side on an arc at
    // a comfortable distance, all at eye level and facing the user, the widest in the middle, the
    // whole row centered where the user is looking. A demo registers each of its windows (the Apps
    // window with its controls, a demo's panel) with how wide it is and how to put it at a pose, and
    // unregisters it when it goes. Logs [Windows].
    public static class FloatingWindows
    {
        // The gap between neighbors, in degrees, and the distance of the arc, in meters.
        public const float k_GapDegrees = 4f;
        public const float k_Distance = 1.8f;
        // Panels sit a little below eye level, windows at it.
        public const float k_PanelDrop = 12f;

        public sealed class Window
        {
            public string Name;
            // Its width in meters, as it is now.
            public Func<float> Width;
            // Puts it at this pose: centered there, facing along the rotation's forward (away from the user).
            public Action<Pose> Place;
            // A panel (controls) rather than content: it goes a little lower.
            public bool IsPanel;
            // Its distance, if it has one of its own (a window made for 2 m, say); else k_Distance.
            public float Distance;
        }

        static readonly List<Window> s_Windows = new();

        public static IReadOnlyList<Window> All => s_Windows;

        public static Window Register(string name, Func<float> width, Action<Pose> place, bool isPanel = false, float distance = 0f)
        {
            var window = new Window { Name = name, Width = width, Place = place, IsPanel = isPanel, Distance = distance };
            s_Windows.Add(window);
            return window;
        }

        public static void Unregister(Window window) => s_Windows.Remove(window);

        // Registers a plain transform, such as a demo's panel: it's placed as is.
        public static Window Register(string name, Transform transform, float widthMeters, bool isPanel = true, float distance = 0f) =>
            Register(name, () => widthMeters, pose =>
            {
                if (transform != null)
                    transform.SetPositionAndRotation(pose.position, pose.rotation);
            }, isPanel, distance);

        // Puts every window on an arc around the head.
        public static void Arrange(Transform head)
        {
            if (head == null || s_Windows.Count == 0)
                return;
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            var yaw = flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized).eulerAngles.y : 0f;
            var poses = Layout(s_Windows, head.position, yaw);
            for (var i = 0; i < s_Windows.Count; i++)
                s_Windows[i].Place(poses[i]);
            Debug.Log($"[Windows] Arranged {s_Windows.Count} around the user");
        }

        // The poses for the windows, in their order: content windows on one arc at eye level, the widest
        // in the middle and the rest alternating right and left of it; panels below the row, under the
        // middle. Pure, for testing.
        public static Pose[] Layout(IReadOnlyList<Window> windows, Vector3 head, float yawDegrees)
        {
            var poses = new Pose[windows.Count];
            var content = new List<int>();
            var panels = new List<int>();
            for (var i = 0; i < windows.Count; i++)
                (windows[i].IsPanel ? panels : content).Add(i);
            content.Sort((a, b) => windows[b].Width().CompareTo(windows[a].Width()));

            // Each window's angular width at its distance, and where it goes: widest at the middle,
            // then right, left, right, ...
            var angles = new float[windows.Count];
            for (var i = 0; i < windows.Count; i++)
            {
                var distance = windows[i].Distance > 0f ? windows[i].Distance : k_Distance;
                angles[i] = 2f * Mathf.Atan2(windows[i].Width() / 2f, distance) * Mathf.Rad2Deg;
            }
            var right = 0f;
            var left = 0f;
            for (var n = 0; n < content.Count; n++)
            {
                var i = content[n];
                float center;
                if (n == 0)
                {
                    center = 0f;
                    right = angles[i] / 2f;
                    left = -angles[i] / 2f;
                }
                else if (n % 2 == 1)
                {
                    center = right + k_GapDegrees + angles[i] / 2f;
                    right = center + angles[i] / 2f;
                }
                else
                {
                    center = left - k_GapDegrees - angles[i] / 2f;
                    left = center - angles[i] / 2f;
                }
                poses[i] = At(windows[i], head, yawDegrees + center, 0f);
            }
            // Panels in a row below, centered, side by side.
            var total = 0f;
            foreach (var i in panels)
                total += angles[i];
            total += k_GapDegrees * Mathf.Max(0, panels.Count - 1);
            var at = -total / 2f;
            foreach (var i in panels)
            {
                poses[i] = At(windows[i], head, yawDegrees + at + angles[i] / 2f, -(content.Count > 0 ? k_PanelDrop + 14f : k_PanelDrop));
                at += angles[i] + k_GapDegrees;
            }
            return poses;
        }

        static Pose At(Window window, Vector3 head, float yawDegrees, float pitchDegrees)
        {
            var distance = window.Distance > 0f ? window.Distance : k_Distance;
            var direction = Quaternion.Euler(-pitchDegrees, yawDegrees, 0f) * Vector3.forward;
            var position = head + direction * distance;
            // Upright, facing the user across the floor, like a screen.
            var facing = Quaternion.Euler(0f, yawDegrees, 0f);
            return new Pose(position, window.IsPanel ? Quaternion.LookRotation(direction, Vector3.up) : facing);
        }
    }
}
