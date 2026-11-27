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
            // Marker must end up the same on-screen size as the local player's arrow, whatever the
            // root's scale is right now (Start may not have scaled it yet; settings/resolution
            // changes rescale it). Never trust map.Scale alone: 1/Scale on an unscaled root made a
            // screen-filling arrow.
            Vector3 want = map.Player.transform.lossyScale, rootScale = map.Root.transform.lossyScale;
            bool scaleOk = Mathf.Abs(rootScale.x) > 1e-4f && Mathf.Abs(rootScale.y) > 1e-4f && map.Root.activeInHierarchy;
            Vector3 local = scaleOk ? new Vector3(want.x / rootScale.x, want.y / rootScale.y, 1f) : Vector3.one;
            if (!scaleOk || local.x > 20f || local.y > 20f || float.IsNaN(local.x) || float.IsNaN(local.y)) { HideAll(); return; }
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
                rt.anchoredPosition = new Vector2(g.Position.x, g.Position.z);
                rt.localScale = local;
                rt.localRotation = Quaternion.AngleAxis(g.Yaw, -Vector3.forward);
                Image img = rt.GetComponentInChildren<Image>();
                if (img != null) img.color = g.Downed ? new Color(1f, 0.3f, 0.3f) : new Color(0.3f, 0.95f, 1f);
            }
            foreach (KeyValuePair<string, RectTransform> kv in markers)
                if (kv.Value != null && !seen.Contains(kv.Key) && kv.Value.gameObject.activeSelf) kv.Value.gameObject.SetActive(false);
        }

        private void HideAll()
        {
            foreach (RectTransform rt in markers.Values)
                if (rt != null && rt.gameObject.activeSelf) rt.gameObject.SetActive(false);
        }

        private RectTransform CreateMarker(string name)
        {
            GameObject copy = Instantiate(map.Player, map.Root.transform, false);
            copy.name = "ZMP_Ally_" + name;
            // Only the arrow itself: drop any child decoration of the player marker.
            for (int i = copy.transform.childCount - 1; i >= 0; i--)
                if (copy.transform.GetChild(i).GetComponent<Graphic>() == null) Destroy(copy.transform.GetChild(i).gameObject);
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
