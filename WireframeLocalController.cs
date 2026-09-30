using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace WireframeMod
{
    public class WireframeLocalController : MonoBehaviourPunCallbacks
    {
        // Photon custom property keys. Anyone running this mod reads these.
        private const string KeyOn = "WFM_On";
        private const string KeyRGB = "WFM_RGB";
        private const string KeySpeed = "WFM_Speed";
        private const string KeySpan = "WFM_Span";

        private class WireEntry
        {
            public Material Mat;
            public Material OriginalMat;
            public Renderer Rend;
            public VRRig Rig;
            public bool IsLocal;
            public int ActorNumber;
            // Settings for remote players (local reads from config live)
            public bool Rgb;
            public float Speed;
            public float Span;
        }

        private class WireSettings
        {
            public bool Rgb;
            public float Speed;
            public float Span;
        }

        private readonly Dictionary<Renderer, Material[]> _originalMaterials = new Dictionary<Renderer, Material[]>();
        private readonly Dictionary<Renderer, WireEntry> _entries = new Dictionary<Renderer, WireEntry>();

        // Players waiting to be scanned: actor number -> give-up time.
        // A new player's rig often spawns a moment after they join, so we retry until it exists.
        private readonly Dictionary<int, float> _pending = new Dictionary<int, float>();
        private const float PendingTimeout = 15f;
        private const float PendingInterval = 0.5f;

        public static Material WireframeMaterialTemplate;

        private const string BundleResourceName = "WireframeMod.Resources.wireframe";
        private const string MaterialAssetName = "Wireframe";
        private static AssetBundle _bundle;

        private float _pendingTimer;
        private float _verifyTimer;
        private bool _syncDirty = true;
        private bool _localDirty = true;
        private string _lastSent;
        private VRRig _localRig;

        // ---------- Lifecycle ----------

        private void Start()
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundleResourceName);
            if (stream != null)
            {
                if (_bundle == null)
                    _bundle = AssetBundle.LoadFromStream(stream);

                if (_bundle != null)
                    WireframeMaterialTemplate = _bundle.LoadAsset<Material>(MaterialAssetName);
            }

            Plugin.ConfigEnabled.SettingChanged += OnEnabledChanged;
            Plugin.ConfigShareWithOthers.SettingChanged += OnShareSettingChanged;
            Plugin.ConfigRGB.SettingChanged += OnShareSettingChanged;
            Plugin.ConfigRGBSpeed.SettingChanged += OnShareSettingChanged;
            Plugin.ConfigRGBSpan.SettingChanged += OnShareSettingChanged;
            Plugin.ConfigShowOthers.SettingChanged += OnShowOthersChanged;

            // If the mod loaded while already in a room, do the initial scan now
            if (PhotonNetwork.InRoom) OnJoinedRoom();
        }

        private void OnDestroy()
        {
            Plugin.ConfigEnabled.SettingChanged -= OnEnabledChanged;
            Plugin.ConfigShareWithOthers.SettingChanged -= OnShareSettingChanged;
            Plugin.ConfigRGB.SettingChanged -= OnShareSettingChanged;
            Plugin.ConfigRGBSpeed.SettingChanged -= OnShareSettingChanged;
            Plugin.ConfigRGBSpan.SettingChanged -= OnShareSettingChanged;
            Plugin.ConfigShowOthers.SettingChanged -= OnShowOthersChanged;
        }

        private void Update()
        {
            UpdateRGB();

            if (_syncDirty && PhotonNetwork.InRoom)
            {
                _syncDirty = false;
                SyncLocalProperties();
            }

            _pendingTimer += Time.deltaTime;
            if (_pendingTimer >= PendingInterval)
            {
                _pendingTimer = 0f;

                if (_localDirty) _localDirty = !RefreshLocal();
                ProcessPending();
            }

            _verifyTimer += Time.deltaTime;
            if (_verifyTimer >= 1f)
            {
                _verifyTimer = 0f;
                VerifyEntries();
            }
        }

        // ---------- Config events ----------

        private void OnEnabledChanged(object sender, EventArgs e)
        {
            _localDirty = true;
            _syncDirty = true;
        }

        private void OnShareSettingChanged(object sender, EventArgs e)
        {
            _syncDirty = true;
        }

        private void OnShowOthersChanged(object sender, EventArgs e)
        {
            if (Plugin.ConfigShowOthers.Value) QueueAllOthers();
            else RevertMatching(en => !en.IsLocal);
        }

        // ---------- Photon callbacks ----------

        // We just joined: publish our settings and scan everyone once.
        public override void OnJoinedRoom()
        {
            _lastSent = null;
            _syncDirty = true;
            _localDirty = true;
            QueueAllOthers();
        }

        public override void OnLeftRoom()
        {
            _lastSent = null;
            _pending.Clear();
            RevertMatching(en => !en.IsLocal);
        }

        // Someone new joined: scan just them.
        public override void OnPlayerEnteredRoom(Player newPlayer)
        {
            if (newPlayer == null || newPlayer.IsLocal) return;
            QueuePlayer(newPlayer.ActorNumber);
        }

        public override void OnPlayerLeftRoom(Player otherPlayer)
        {
            if (otherPlayer == null) return;
            _pending.Remove(otherPlayer.ActorNumber);
            int actor = otherPlayer.ActorNumber;
            RevertMatching(en => !en.IsLocal && en.ActorNumber == actor);
        }

        // Someone changed their settings (or their properties arrived late): re-scan just them.
        public override void OnPlayerPropertiesUpdate(Player targetPlayer, Hashtable changedProps)
        {
            if (targetPlayer == null || targetPlayer.IsLocal || changedProps == null) return;

            if (changedProps.ContainsKey(KeyOn) || changedProps.ContainsKey(KeyRGB) ||
                changedProps.ContainsKey(KeySpeed) || changedProps.ContainsKey(KeySpan))
            {
                QueuePlayer(targetPlayer.ActorNumber);
            }
        }

        private void QueuePlayer(int actorNumber)
        {
            if (!Plugin.ConfigShowOthers.Value) return;
            _pending[actorNumber] = Time.time + PendingTimeout;
        }

        private void QueueAllOthers()
        {
            if (!PhotonNetwork.InRoom) return;
            foreach (var p in PhotonNetwork.PlayerList)
            {
                if (p != null && !p.IsLocal) QueuePlayer(p.ActorNumber);
            }
        }

        // ---------- Networking ----------

        // Publishes our settings so other mod users can see them.
        private void SyncLocalProperties()
        {
            bool on = Plugin.ConfigEnabled.Value && Plugin.ConfigShareWithOthers.Value;
            bool rgb = Plugin.ConfigRGB.Value;
            float speed = Plugin.ConfigRGBSpeed.Value;
            float span = Plugin.ConfigRGBSpan.Value;

            string sig = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}", on, rgb, speed, span);
            if (sig == _lastSent) return;
            _lastSent = sig;

            var props = new Hashtable
            {
                { KeyOn, on },
                { KeyRGB, rgb },
                { KeySpeed, speed },
                { KeySpan, span }
            };
            PhotonNetwork.LocalPlayer.SetCustomProperties(props);
        }

        private static Player GetPhotonPlayer(VRRig rig)
        {
            // If this doesn't compile on your Gorilla Tag version, try rig.creator.GetPlayerRef()
            // or rig.photonView.Owner instead.
            return rig.OwningNetPlayer != null ? rig.OwningNetPlayer.GetPlayerRef() : null;
        }

        private static bool TryGetSettings(Player player, out WireSettings settings)
        {
            settings = null;
            if (player == null || player.CustomProperties == null) return false;

            var p = player.CustomProperties;
            if (!p.TryGetValue(KeyOn, out object onObj) || !(onObj is bool on) || !on) return false;

            settings = new WireSettings { Rgb = true, Speed = 0.25f, Span = 1f };
            if (p.TryGetValue(KeyRGB, out object rgbObj) && rgbObj is bool rgb) settings.Rgb = rgb;
            if (p.TryGetValue(KeySpeed, out object spdObj) && spdObj != null) settings.Speed = Convert.ToSingle(spdObj);
            if (p.TryGetValue(KeySpan, out object spanObj) && spanObj != null) settings.Span = Convert.ToSingle(spanObj);
            return true;
        }

        // ---------- Scanning ----------

        // Returns true when the local rig has been handled (so we can stop retrying).
        private bool RefreshLocal()
        {
            if (!Plugin.ConfigEnabled.Value)
            {
                RevertMatching(en => en.IsLocal);
                return true;
            }

            if (WireframeMaterialTemplate == null) return false;

            if (_localRig == null) _localRig = FindLocalRig();
            if (_localRig == null) return false;

            var renderer = FindFurRenderer(_localRig);
            if (renderer == null) return false;

            ApplyWireframe(renderer, _localRig, true, 0, null);
            return true;
        }

        private void ProcessPending()
        {
            if (_pending.Count == 0) return;

            if (!PhotonNetwork.InRoom)
            {
                _pending.Clear();
                return;
            }

            var rigs = UnityEngine.Object.FindObjectsByType<VRRig>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var done = new List<int>();
            var actors = new List<int>(_pending.Keys);

            foreach (int actor in actors)
            {
                Player player = PhotonNetwork.CurrentRoom.GetPlayer(actor);
                if (player == null)
                {
                    done.Add(actor); // they left
                    continue;
                }

                VRRig rig = null;
                foreach (var r in rigs)
                {
                    if (r == null || r.isLocal) continue;
                    var owner = GetPhotonPlayer(r);
                    if (owner != null && owner.ActorNumber == actor) { rig = r; break; }
                }

                if (rig != null && ScanPlayer(rig, player))
                {
                    done.Add(actor);
                }
                else if (Time.time > _pending[actor])
                {
                    done.Add(actor); // rig never showed up, give up
                }
            }

            foreach (int actor in done) _pending.Remove(actor);
        }

        // Applies or removes the wireframe on one remote rig based on that player's config.
        // Returns false if the rig isn't ready yet (try again later).
        private bool ScanPlayer(VRRig rig, Player player)
        {
            var renderer = FindFurRenderer(rig);
            if (renderer == null) return false;

            if (Plugin.ConfigShowOthers.Value && WireframeMaterialTemplate != null &&
                TryGetSettings(player, out WireSettings settings))
            {
                ApplyWireframe(renderer, rig, false, player.ActorNumber, settings);
            }
            else
            {
                RevertRenderer(renderer);
            }
            return true;
        }

        // Once a second: drop wireframes the game wiped, or that landed on a rig now used by someone else.
        private void VerifyEntries()
        {
            if (_entries.Count == 0) return;

            var stale = new List<Renderer>();
            foreach (var kvp in _entries)
            {
                var e = kvp.Value;
                bool bad = e.Mat == null || e.Rend == null || !ContainsMaterial(e.Rend.sharedMaterials, e.Mat);

                if (!bad && !e.IsLocal)
                {
                    var owner = e.Rig != null ? GetPhotonPlayer(e.Rig) : null;
                    bad = !PhotonNetwork.InRoom || owner == null || owner.ActorNumber != e.ActorNumber;
                }

                if (bad) stale.Add(kvp.Key);
            }

            foreach (var key in stale)
            {
                if (!_entries.TryGetValue(key, out var e)) continue;

                bool localWasHit = e.IsLocal;
                Player currentOwner = (!e.IsLocal && e.Rig != null) ? GetPhotonPlayer(e.Rig) : null;

                if (e.Rend != null && e.Mat != null && ContainsMaterial(e.Rend.sharedMaterials, e.Mat))
                    RevertRenderer(key);   // still ours: restore the originals
                else
                    DropEntry(key);        // game already replaced them: just forget

                if (localWasHit) _localDirty = true;
                else if (currentOwner != null && !currentOwner.IsLocal) QueuePlayer(currentOwner.ActorNumber);
            }
        }

        private static bool ContainsMaterial(Material[] mats, Material target)
        {
            if (mats == null) return false;
            foreach (var m in mats) if (m == target) return true;
            return false;
        }

        // ---------- Rendering ----------

        private void UpdateRGB()
        {
            if (_entries.Count == 0) return;

            foreach (var kvp in _entries)
            {
                var e = kvp.Value;
                if (e.Mat == null || e.Rend == null) continue;

                bool rgb = e.IsLocal ? Plugin.ConfigRGB.Value : e.Rgb;
                float speed = e.IsLocal ? Plugin.ConfigRGBSpeed.Value : e.Speed;
                float span = e.IsLocal ? Plugin.ConfigRGBSpan.Value : e.Span;

                e.Mat.SetFloat("_RainbowEnabled", rgb ? 1f : 0f);
                if (!rgb) continue;

                Bounds b = e.Rend.bounds;
                e.Mat.SetFloat("_RainbowTop", b.max.y);
                e.Mat.SetFloat("_RainbowHeight", b.size.y);
                e.Mat.SetFloat("_RainbowSpan", span);
                e.Mat.SetFloat("_RainbowSpeed", speed);
            }
        }

        private void ApplyWireframe(Renderer renderer, VRRig rig, bool isLocal, int actor, WireSettings settings)
        {
            // Already applied: just refresh settings and color
            if (_entries.TryGetValue(renderer, out var existing))
            {
                existing.Rig = rig;
                existing.IsLocal = isLocal;
                existing.ActorNumber = actor;
                if (settings != null)
                {
                    existing.Rgb = settings.Rgb;
                    existing.Speed = settings.Speed;
                    existing.Span = settings.Span;
                }
                if (existing.OriginalMat != null && existing.OriginalMat.HasProperty("_Color"))
                    existing.Mat.color = existing.OriginalMat.GetColor("_Color");
                return;
            }

            var mats = renderer.materials;

            // Skip if our shader is already on it
            foreach (var mat in mats)
            {
                if (mat != null && mat.shader == WireframeMaterialTemplate.shader) return;
            }

            var swapped = new Material[mats.Length];
            WireEntry entry = null;

            for (int i = 0; i < mats.Length; i++)
            {
                var mat = mats[i];
                swapped[i] = mat;
                if (mat == null) continue;

                // Only the main gorilla body fur material
                if (mat.name.ToLower().Contains("gorilla_body"))
                {
                    Color playerColor = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;

                    var wireMat = new Material(WireframeMaterialTemplate);
                    wireMat.color = playerColor;
                    swapped[i] = wireMat;

                    entry = new WireEntry
                    {
                        Mat = wireMat,
                        OriginalMat = mat,
                        Rend = renderer,
                        Rig = rig,
                        IsLocal = isLocal,
                        ActorNumber = actor,
                        Rgb = settings != null ? settings.Rgb : true,
                        Speed = settings != null ? settings.Speed : 0.25f,
                        Span = settings != null ? settings.Span : 1f
                    };
                }
            }

            if (entry != null)
            {
                _originalMaterials[renderer] = (Material[])mats.Clone();
                _entries[renderer] = entry;
                renderer.materials = swapped;
            }
        }

        private void RevertRenderer(Renderer renderer)
        {
            if (renderer == null) return;

            if (_originalMaterials.TryGetValue(renderer, out var originals) && originals != null)
                renderer.materials = originals;

            DropEntry(renderer);
        }

        private void DropEntry(Renderer renderer)
        {
            if (renderer == null) return;

            if (_entries.TryGetValue(renderer, out var e))
            {
                if (e.Mat != null) Destroy(e.Mat);
                _entries.Remove(renderer);
            }
            _originalMaterials.Remove(renderer);
        }

        private void RevertMatching(Func<WireEntry, bool> predicate)
        {
            var keys = new List<Renderer>();
            foreach (var kvp in _entries)
            {
                if (predicate(kvp.Value)) keys.Add(kvp.Key);
            }

            foreach (var key in keys)
            {
                if (key != null) RevertRenderer(key);
                else
                {
                    // Renderer was destroyed; clean up the dictionary entries directly
                    if (_entries.TryGetValue(key, out var e) && e.Mat != null) Destroy(e.Mat);
                    _entries.Remove(key);
                    _originalMaterials.Remove(key);
                }
            }
        }

        // ---------- Helpers ----------

        private VRRig FindLocalRig()
        {
            var rigs = UnityEngine.Object.FindObjectsByType<VRRig>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var rig in rigs)
            {
                if (rig != null && rig.isLocal) return rig;
            }
            return null;
        }

        private Renderer FindFurRenderer(VRRig rig)
        {
            var target = FindChildRecursive(rig.transform, "gorilla_new");
            if (target == null) return null;
            return target.GetComponent<Renderer>();
        }

        private Transform FindChildRecursive(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name.ToLower() == name.ToLower()) return child;
                var found = FindChildRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
