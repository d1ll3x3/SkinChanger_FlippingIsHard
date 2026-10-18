using System;
using System.Collections.Generic;
using EHS;
using EHS.Skins;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace CharmReplacer
{
    /// <summary>
    /// Renders the icons of the "Custom" menu entries: a copy of the base cover (painted) or of the
    /// custom charm is placed far from the level on an unused layer and captured by a temporary camera
    /// into a transparent texture. The camera angle is copied from the customization camera so the
    /// icon matches how the game frames its own previews.
    /// </summary>
    internal static class IconStudio
    {
        private const int IconSize = 256;
        private const float FieldOfView = 25f;
        private const int StableFramesRequired = 15;
        // Customization cameras blend in; render anyway if the camera never settles.
        private const float MaxWaitSeconds = 4f;
        private static readonly Vector3 StudioPosition = new Vector3(0f, -10000f, 0f);

        private enum Phase { WaitCamera, LoadSubject, Render, Read }

        private sealed class Job
        {
            public SkinCategory Category;
            public int SourceId;
            public Phase Phase;
            public float StartTime;
            public int StableFrames;
            public Vector3 LastCameraPosition;
            public Quaternion LastCameraRotation;
            public Quaternion CameraRotation;
            public Quaternion SubjectRotation;
            public GameObject Subject;
            public AsyncOperationHandle<GameObject> Handle;
            public bool HasHandle;
            public Camera Camera;
            public RenderTexture Target;
        }

        private static readonly Dictionary<SkinCategory, Sprite> _sprites = new Dictionary<SkinCategory, Sprite>();
        private static readonly Dictionary<SkinCategory, int> _spriteSources = new Dictionary<SkinCategory, int>();
        private static readonly Queue<SkinCategory> _queue = new Queue<SkinCategory>();
        private static Job _job;

        public static Sprite GetSprite(SkinCategory category) =>
            _sprites.TryGetValue(category, out var sprite) ? sprite : null;

        /// <summary>Queues a render unless the current icon already shows the current asset.</summary>
        public static void Request(SkinCategory category)
        {
            int source = SourceId(category);
            if (source == 0) return;
            if (_spriteSources.TryGetValue(category, out int done) && done == source) return;
            if (_job != null && _job.Category == category && _job.SourceId == source) return;
            if (_queue.Contains(category)) return;
            _queue.Enqueue(category);
        }

        private static int SourceId(SkinCategory category)
        {
            if (category == SkinCategory.Cover)
                return CharmState.CustomSkinTexture != null ? CharmState.CustomSkinTexture.GetInstanceID() : 0;
            if (CharmState.CustomPrefab != null) return CharmState.CustomPrefab.GetInstanceID();
            return CharmState.CustomMesh != null ? CharmState.CustomMesh.GetInstanceID() : 0;
        }

        public static void Tick()
        {
            if (_job == null)
            {
                if (_queue.Count == 0) return;
                var category = _queue.Dequeue();
                _job = new Job { Category = category, SourceId = SourceId(category), Phase = Phase.WaitCamera, StartTime = Time.time };
            }

            try
            {
                switch (_job.Phase)
                {
                    case Phase.WaitCamera: TickWaitCamera(_job); break;
                    case Phase.LoadSubject: TickLoadSubject(_job); break;
                    case Phase.Render: TickRender(_job); break;
                    case Phase.Read: TickRead(_job); break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Icon] {_job.Category} icon failed: {ex.Message}");
                Cleanup(_job);
                _job = null;
            }
        }

        private static void TickWaitCamera(Job job)
        {
            var dummy = LocalVisuals.ActiveCustomizationDummy;
            var cam = RenderingCamera();
            if (dummy == null || cam == null) return;

            var t = cam.transform;
            bool still = (t.position - job.LastCameraPosition).sqrMagnitude < 1e-6f
                         && Quaternion.Angle(t.rotation, job.LastCameraRotation) < 0.05f;
            job.StableFrames = still ? job.StableFrames + 1 : 0;
            job.LastCameraPosition = t.position;
            job.LastCameraRotation = t.rotation;
            if (job.StableFrames < StableFramesRequired && Time.time - job.StartTime < MaxWaitSeconds) return;

            if (!TryGetLiveRotation(dummy, job.Category, out var subjectRotation)) return;
            job.CameraRotation = t.rotation;
            job.SubjectRotation = subjectRotation;
            job.Phase = Phase.LoadSubject;

            if (job.Category == SkinCategory.Charm)
            {
                job.Subject = CreateCharmSubject(job);
                return;
            }

            var handler = FindHandler(dummy, SkinCategory.Cover);
            var reference = handler?.DefaultSkinUISO?.PrefabReference;
            if (reference == null || !reference.RuntimeKeyIsValid())
                throw new InvalidOperationException("no base cover prefab reference");
            job.Handle = reference.InstantiateAsync(StudioPosition, job.SubjectRotation, null);
            job.HasHandle = true;
        }

        // Rotation of the object as the preview shows it. The custom charm is placed by CharmSystem
        // relative to the original charm mesh, so before it exists that mesh stands in for it.
        private static bool TryGetLiveRotation(PlayerVisualsDummy dummy, SkinCategory category, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            var handler = FindHandler(dummy, category);
            if (handler == null) return false;

            Transform live = null;
            if (handler.TryGetInstance(out var instance) && instance != null)
            {
                live = instance.transform;
                var filter = category == SkinCategory.Charm ? CharmSystem.FindOriginalCharmMesh(instance.transform) : null;
                if (filter != null)
                {
                    // The icon keeps the charm hanging (+Y along the phone's up), while the placed
                    // charm stands up; so rebuild it from the original charm instead of copying it.
                    rotation = filter.transform.rotation;
                    if (CharmSystem.LocalUsesPrefab)
                    {
                        rotation *= CharmSystem.PrefabRotationOffset;
                        var phoneUp = CharmSystem.PhoneUp(filter.transform);
                        if (phoneUp.sqrMagnitude > 0f)
                            rotation = Quaternion.FromToRotation(rotation * Vector3.up, phoneUp) * rotation;
                    }
                    return true;
                }
            }
            if (live == null) live = handler.socket != null ? handler.socket : handler.transform;
            rotation = live.rotation;
            return true;
        }

        private static SkinPrefabHandler FindHandler(PlayerVisualsDummy dummy, SkinCategory category)
        {
            var map = dummy.skinsHandlerMap;
            return map != null && map.ContainsKey(category) ? map[category] : null;
        }

        private static GameObject CreateCharmSubject(Job job)
        {
            GameObject subject;
            if (CharmSystem.LocalUsesPrefab)
            {
                subject = UnityEngine.Object.Instantiate(CharmState.CustomPrefab);
                // The template is inactive, so the copy is too: switch its cloth off before it wakes.
                var cloths = subject.GetComponentsInChildren<MagicaCloth2.MagicaCloth>(true);
                if (cloths != null)
                    for (int i = 0; i < cloths.Count; i++) if (cloths[i] != null) cloths[i].enabled = false;
                CharmSystem.FixMaterialShaders(subject);
            }
            else
            {
                subject = new GameObject("CharmReplacerIconCharm");
                subject.AddComponent<MeshFilter>().sharedMesh = CharmState.CustomMesh;
                var materials = CharmState.BundleMaterials ?? CharmState.CapturedGameMaterials;
                var renderer = subject.AddComponent<MeshRenderer>();
                if (materials != null) renderer.sharedMaterials = materials;
            }
            subject.transform.SetPositionAndRotation(StudioPosition, job.SubjectRotation);
            return subject;
        }

        private static void TickLoadSubject(Job job)
        {
            if (job.HasHandle)
            {
                if (!job.Handle.IsDone) return;
                if (job.Handle.Status != AsyncOperationStatus.Succeeded || job.Handle.Result == null)
                    throw new InvalidOperationException("base cover prefab failed to load");
                job.Subject = job.Handle.Result;
                SkinSystem.PaintPhoneCovers(job.Subject, CharmState.CustomSkinTexture, CharmState.CustomSkinEmissionTexture);
            }

            int layer = FindFreeLayer();
            CharmSystem.SetLayerRecursively(job.Subject, layer);
            job.Subject.SetActive(true);

            var renderers = job.Subject.GetComponentsInChildren<Renderer>(false);
            if (renderers == null || renderers.Count == 0) throw new InvalidOperationException("subject has no renderers");
            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Count; i++) bounds.Encapsulate(renderers[i].bounds);

            job.Target = new RenderTexture(IconSize, IconSize, 24, RenderTextureFormat.ARGB32);
            job.Target.hideFlags = HideFlags.DontUnloadUnusedAsset;

            var cameraObject = new GameObject("CharmReplacerIconCamera");
            job.Camera = cameraObject.AddComponent<Camera>();
            job.Camera.enabled = false;
            job.Camera.clearFlags = CameraClearFlags.SolidColor;
            job.Camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            job.Camera.cullingMask = 1 << layer;
            job.Camera.fieldOfView = FieldOfView;
            job.Camera.allowHDR = false;
            job.Camera.allowMSAA = false;
            job.Camera.targetTexture = job.Target;

            float radius = Mathf.Max(bounds.extents.magnitude, 0.001f);
            float distance = radius / Mathf.Sin(FieldOfView * 0.5f * Mathf.Deg2Rad);
            job.Camera.nearClipPlane = Mathf.Max(distance - radius * 2f, 0.001f);
            job.Camera.farClipPlane = distance + radius * 2f;
            cameraObject.transform.rotation = job.CameraRotation;
            cameraObject.transform.position = bounds.center - cameraObject.transform.forward * distance;

            job.Phase = Phase.Render;
        }

        // The pipeline renders an enabled camera at the end of the frame; read it back next frame.
        private static void TickRender(Job job)
        {
            job.Camera.enabled = true;
            job.Phase = Phase.Read;
        }

        private static void TickRead(Job job)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = job.Target;
            var texture = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
            texture.Apply(false, false);
            RenderTexture.active = previous;

            texture.name = "CharmReplacerIcon_" + job.Category;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var sprite = Sprite.Create(texture, new Rect(0, 0, IconSize, IconSize), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;

            if (_sprites.TryGetValue(job.Category, out var old) && old != null)
            {
                var oldTexture = old.texture;
                UnityEngine.Object.Destroy(old);
                if (oldTexture != null) UnityEngine.Object.Destroy(oldTexture);
            }
            _sprites[job.Category] = sprite;
            _spriteSources[job.Category] = job.SourceId;
            NativeSkinMenu.SetIcon(job.Category, sprite);
            Plugin.Log.LogInfo($"[Icon] ✓ Rendered {job.Category} icon.");

            Cleanup(job);
            _job = null;
        }

        private static void Cleanup(Job job)
        {
            if (job.Camera != null) UnityEngine.Object.Destroy(job.Camera.gameObject);
            if (job.Target != null)
            {
                job.Target.Release();
                UnityEngine.Object.Destroy(job.Target);
            }
            if (job.HasHandle)
                Addressables.ReleaseInstance(job.Handle);
            else if (job.Subject != null)
                UnityEngine.Object.Destroy(job.Subject);
        }

        // The game's camera may not carry the MainCamera tag.
        private static Camera RenderingCamera()
        {
            var main = Camera.main;
            if (main != null) return main;
            Camera best = null;
            var cameras = Camera.allCameras;
            if (cameras == null) return null;
            foreach (var cam in cameras)
            {
                if (cam == null || cam.targetTexture != null) continue;
                if (best == null || cam.depth > best.depth) best = cam;
            }
            return best;
        }

        private static int FindFreeLayer()
        {
            for (int layer = 31; layer >= 8; layer--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(layer))) return layer;
            return 31;
        }
    }
}
