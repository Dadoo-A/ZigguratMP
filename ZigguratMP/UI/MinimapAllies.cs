using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ZigguratMP.Sync;

namespace ZigguratMP.UI
{
    /// <summary>
    /// Ally markers on the minimap / big map. The map root is laid out in world X/Z units, so a
    /// marker is a copy of the local player's arrow placed at the ally's world position.
    /// </summary>
    internal sealed class MinimapAllies : MonoBehaviour
    {
        private Minimap map;
        private readonly Dictionary<string, RectTransform> markers = new Dictionary<string, RectTransform>();
        private readonly HashSet<string> seen = new HashSet<string>();

        private void Awake() => map = GetComponent<Minimap>();

        private void LateUpdate()
        {
            if (map == null || map.Root == null || map.Player == null) return;
            seen.Clear();
            foreach (RemotePlayers.GhostView g in RemotePlayers.VisibleGhosts())
            {
                seen.Add(g.Name);
                if (!markers.TryGetValue(g.Name, out RectTransform rt) || rt == null)
                {
                    rt = CreateMarker(g.Name);
                    if (rt == null) continue;
                    markers[g.Name] = rt;
                }
                if (!rt.gameObject.activeSelf) rt.gameObject.SetActive(true);
                float scale = Mathf.Max(0.01f, map.Scale);
                rt.anchoredPosition = new Vector2(g.Position.x, g.Position.z);
                rt.localScale = Vector3.one / scale; // root is scaled by Scale; keep marker at UI size
                rt.localRotation = Quaternion.AngleAxis(g.Yaw, -Vector3.forward);
                Image img = rt.GetComponentInChildren<Image>();
                if (img != null) img.color = g.Downed ? new Color(1f, 0.3f, 0.3f) : new Color(0.3f, 0.95f, 1f);
            }
            foreach (KeyValuePair<string, RectTransform> kv in markers)
                if (kv.Value != null && !seen.Contains(kv.Key) && kv.Value.gameObject.activeSelf) kv.Value.gameObject.SetActive(false);
        }

        private RectTransform CreateMarker(string name)
        {
            GameObject copy = Instantiate(map.Player, map.Root.transform, false);
            copy.name = "ZMP_Ally_" + name;
            foreach (MonoBehaviour mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
                if (!(mb is Graphic)) Destroy(mb); // keep only the visuals
            RectTransform rt = copy.GetComponent<RectTransform>();
            if (rt == null) { Destroy(copy); return null; }
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.SetAsLastSibling();
            return rt;
        }

        private void OnDestroy()
        {
            foreach (RectTransform rt in markers.Values) if (rt != null) Destroy(rt.gameObject);
        }
    }
}
