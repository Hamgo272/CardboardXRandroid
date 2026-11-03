using UnityEngine;

namespace CardboardXR.Interaction
{
    /// <summary>Short, low-intensity confirmation feedback for gaze and hand-driven controls.</summary>
    public static class AndroidHaptics
    {
        static float s_LastPulse = -1f;

        public static void Confirm()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Time.unscaledTime - s_LastPulse < 0.06f)
                return;
            s_LastPulse = Time.unscaledTime;
            try
            {
                using (var bridge = new AndroidJavaClass("com.glassesxr.cardboardxr.GlassesWebViewBridge"))
                    bridge.CallStatic("vibrate", 18);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Haptics] Vibration unavailable: " + e.Message);
            }
#endif
        }
    }
}