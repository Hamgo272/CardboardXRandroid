using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CardboardXR.Demos.Desktop
{
    // The phone's side of screen sharing with Tools/screen-server: finds the computer on the local
    // network by itself, keeps the newest frame of its screen in Frame, and sends look-and-pinch
    // input back. The computer only answers a phone its owner approved on the sharing page.
    public class DesktopLink : MonoBehaviour
    {
        public enum Status
        {
            Searching,
            WaitingApproval,
            WaitingShare,
            Streaming,
        }

        const int k_Port = 8765;
        const string k_HostKey = "CardboardXR.DesktopHost";
        // Addresses asked at once while scanning the local network for the computer.
        const int k_ScanParallel = 64;

        public Status State { get; private set; } = Status.Searching;
        public Texture2D Frame { get; private set; }
        public string Host { get; private set; }
        public float FramesPerSecond { get; private set; }

        int m_FrameNo = -1;
        int m_IdlePolls;
        int m_FpsFrames;
        float m_FpsStart;
        readonly Queue<string> m_Input = new();
        string m_PendingMove;
        float m_LastStatusLog = float.NegativeInfinity;

        [Serializable]
        class Hello
        {
            public string app;
            public bool approved;
            public bool sharing;
        }

        void OnEnable()
        {
            StartCoroutine(Run());
            StartCoroutine(SendInput());
        }

        void OnDisable()
        {
            StopAllCoroutines();
            if (Frame != null)
                Destroy(Frame);
            Frame = null;
        }

        // An address typed by hand ("192.168.1.20" or "192.168.1.20:8765"), when the scan can't find it.
        public void SetHost(string host)
        {
            host = host.Trim();
            if (host.Length == 0)
                return;
            if (!host.Contains(":"))
                host += ":" + k_Port;
            PlayerPrefs.SetString(k_HostKey, host);
            PlayerPrefs.Save();
            Host = host;
            Debug.Log($"[Desktop] Address typed: {host}");
        }

        // Input in the shared picture's coordinates (0..1, y down): "move", "down", "up", "click",
        // or "wheel" with delta in wheel units (120 a notch). Moves are merged: only the newest is sent.
        public void Send(string type, Vector2 point, float delta = 0f)
        {
            var json = string.Format(CultureInfo.InvariantCulture,
                "{{\"t\":\"{0}\",\"x\":{1:F4},\"y\":{2:F4},\"d\":{3:F0}}}", type, point.x, point.y, delta);
            if (type == "move")
            {
                m_PendingMove = json;
                return;
            }
            if (m_PendingMove != null)
            {
                m_Input.Enqueue(m_PendingMove);
                m_PendingMove = null;
            }
            m_Input.Enqueue(json);
        }

        // Text typed on the virtual keyboard, and the Enter and Backspace keys.
        public void SendText(string text) => Enqueue(
            $"{{\"t\":\"text\",\"s\":\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\"}}");

        public void SendKey(string key) => Enqueue($"{{\"t\":\"key\",\"k\":\"{key}\"}}");

        void Enqueue(string json)
        {
            if (m_PendingMove != null)
            {
                m_Input.Enqueue(m_PendingMove);
                m_PendingMove = null;
            }
            m_Input.Enqueue(json);
        }

        IEnumerator Run()
        {
            while (true)
            {
                if (Host == null)
                {
                    State = Status.Searching;
                    yield return Discover();
                    if (Host == null)
                    {
                        yield return new WaitForSecondsRealtime(2f);
                        continue;
                    }
                }

                Hello hello = null;
                yield return AskHello(Host, reply => hello = reply);
                if (hello == null)
                {
                    Debug.Log($"[Desktop] Lost {Host}; searching again");
                    Host = null;
                    continue;
                }
                if (!hello.approved)
                {
                    State = Status.WaitingApproval;
                    yield return new WaitForSecondsRealtime(1f);
                    continue;
                }
                if (!hello.sharing)
                {
                    State = Status.WaitingShare;
                    yield return new WaitForSecondsRealtime(1f);
                    continue;
                }
                yield return Stream();
            }
        }

        // The saved address first, then every address on this phone's Wi-Fi network.
        IEnumerator Discover()
        {
            var saved = PlayerPrefs.GetString(k_HostKey, "");
            if (saved.Length > 0)
            {
                Hello hello = null;
                yield return AskHello(saved, reply => hello = reply);
                if (hello != null)
                {
                    Found(saved);
                    yield break;
                }
            }

            foreach (var prefix in LocalPrefixes())
            {
                for (var start = 1; start < 255 && Host == null; start += k_ScanParallel)
                {
                    var requests = new List<UnityWebRequest>();
                    var hosts = new List<string>();
                    for (var i = start; i < Mathf.Min(start + k_ScanParallel, 255); i++)
                    {
                        var host = $"{prefix}{i}:{k_Port}";
                        var request = UnityWebRequest.Get($"http://{host}/hello");
                        request.timeout = 2;
                        request.SendWebRequest();
                        requests.Add(request);
                        hosts.Add(host);
                    }
                    while (requests.Exists(r => !r.isDone))
                        yield return null;
                    for (var j = 0; j < requests.Count; j++)
                    {
                        if (Host == null && requests[j].result == UnityWebRequest.Result.Success &&
                            requests[j].downloadHandler.text.Contains("glasses-screen"))
                            Found(hosts[j]);
                        requests[j].Dispose();
                    }
                }
                if (Host != null)
                    yield break;
            }
        }

        void Found(string host)
        {
            Host = host;
            PlayerPrefs.SetString(k_HostKey, host);
            PlayerPrefs.Save();
            Debug.Log($"[Desktop] Found the computer at {host}");
        }

        IEnumerator AskHello(string host, Action<Hello> done)
        {
            var request = UnityWebRequest.Get($"http://{host}/hello");
            request.timeout = 2;
            yield return request.SendWebRequest();
            Hello reply = null;
            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    reply = JsonUtility.FromJson<Hello>(request.downloadHandler.text);
                    if (reply == null || reply.app != "glasses-screen")
                        reply = null;
                }
                catch (ArgumentException)
                {
                    reply = null;
                }
            }
            request.Dispose();
            done(reply);
        }

        // Long-polls the newest frame until the connection breaks or sharing stops.
        IEnumerator Stream()
        {
            var failures = 0;
            while (failures < 5)
            {
                var request = UnityWebRequestTexture.GetTexture($"http://{Host}/frame?after={m_FrameNo}", true);
                request.timeout = 4;
                yield return request.SendWebRequest();

                var code = request.responseCode;
                if (code == 403)
                {
                    request.Dispose();
                    yield break;
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    request.Dispose();
                    failures++;
                    yield return new WaitForSecondsRealtime(0.3f);
                    continue;
                }
                failures = 0;
                if (code == 204)
                {
                    // No new frame for a second: sharing paused or stopped.
                    request.Dispose();
                    if (++m_IdlePolls >= 2)
                        yield break;
                    continue;
                }

                var texture = DownloadHandlerTexture.GetContent(request);
                int.TryParse(request.GetResponseHeader("X-Frame"), out m_FrameNo);
                request.Dispose();
                if (Frame != null)
                    Destroy(Frame);
                Frame = texture;
                m_IdlePolls = 0;
                State = Status.Streaming;
                CountFrame();
            }
            Debug.Log("[Desktop] Stream broke; reconnecting");
        }

        void CountFrame()
        {
            m_FpsFrames++;
            var now = Time.unscaledTime;
            if (now - m_FpsStart < 1f)
                return;
            FramesPerSecond = m_FpsFrames / (now - m_FpsStart);
            m_FpsFrames = 0;
            m_FpsStart = now;
            if (now - m_LastStatusLog >= 5f)
            {
                m_LastStatusLog = now;
                Debug.Log($"[Desktop] {FramesPerSecond:F0} fps, {Frame.width}x{Frame.height} from {Host}");
            }
        }

        IEnumerator SendInput()
        {
            while (true)
            {
                string next = null;
                if (m_Input.Count > 0)
                {
                    next = m_Input.Dequeue();
                }
                else if (m_PendingMove != null)
                {
                    next = m_PendingMove;
                    m_PendingMove = null;
                }
                if (next == null || Host == null || State != Status.Streaming)
                {
                    yield return null;
                    continue;
                }
                var request = new UnityWebRequest($"http://{Host}/input", "POST")
                {
                    uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(next)),
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = 2,
                };
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();
                request.Dispose();
            }
        }

        // "192.168.1." for each IPv4 network this phone is on, Wi-Fi first.
        static List<string> LocalPrefixes()
        {
            var prefixes = new List<string>();
            try
            {
                var interfaces = new List<NetworkInterface>(NetworkInterface.GetAllNetworkInterfaces());
                // en0 is the iPhone's Wi-Fi; the cellular interface comes later, if at all.
                interfaces.Sort((a, b) => (b.Name.StartsWith("en") ? 1 : 0) - (a.Name.StartsWith("en") ? 1 : 0));
                foreach (var nic in interfaces)
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    foreach (var address in nic.GetIPProperties().UnicastAddresses)
                        Add(address.Address);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Desktop] Couldn't list network interfaces: {e.Message}");
            }
            if (prefixes.Count == 0)
            {
                try
                {
                    foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
                        Add(address);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Desktop] Couldn't find this phone's address: {e.Message}");
                }
            }
            Debug.Log($"[Desktop] Scanning {string.Join(", ", prefixes)}");
            return prefixes;

            void Add(IPAddress address)
            {
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                    return;
                var b = address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254)
                    return;
                var prefix = $"{b[0]}.{b[1]}.{b[2]}.";
                if (!prefixes.Contains(prefix))
                    prefixes.Add(prefix);
            }
        }
    }
}
