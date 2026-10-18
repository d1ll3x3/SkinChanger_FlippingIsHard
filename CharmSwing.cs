using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace CharmReplacer
{
    /// <summary>
    /// Swing physics for mesh-only custom charms. The game's charm cloth is a MeshCloth whose vertex
    /// selection is painted for the phone charm mesh: transferred to another mesh most vertices end up
    /// invalid (no motion), and a selection generated for the whole mesh simulates it as cloth (the model
    /// deforms). A charm is rigid, so instead the custom mesh hangs from the authored attachment point
    /// as a damped pendulum driven by the phone's motion and gravity.
    /// </summary>
    internal static class CharmSwing
    {
        // Pull back toward the authored hanging pose (1/s²), on top of real pendulum gravity (g / length).
        private const float Stiffness = 40f;
        private const float Damping = 6f;
        private const float GravityScale = 1f;
        private const float MaxAngleDegrees = 100f;
        // Flips accelerate the phone hard; cap what the charm feels so it whips instead of spinning.
        private const float MaxPivotAcceleration = 60f;
        // A larger jump between frames is a teleport/respawn, not motion.
        private const float TeleportDistance = 1f;
        private const float MaxStep = 1f / 30f;

        private sealed class State
        {
            public Transform Pivot;
            public Transform Parent;
            public Vector3 RestDirectionLocal;
            public float Length;
            public Vector3 Direction;
            public Vector3 AngularVelocity;
            public Vector3 LastPivotPosition;
            public Vector3 LastPivotVelocity;
        }

        private static readonly List<State> _states = new List<State>();

        /// <summary>
        /// Creates the swinging copy of <paramref name="original"/> showing <paramref name="mesh"/>, hung
        /// from <paramref name="anchorWorld"/>. Returns its root (named like the other placed charms).
        /// </summary>
        public static GameObject Attach(MeshFilter original, Mesh mesh, Il2CppReferenceArray<Material> materials,
            Vector3 anchorWorld, string name)
        {
            var parent = original.transform.parent;
            var pivot = new GameObject(name);
            pivot.layer = original.gameObject.layer;
            pivot.transform.SetParent(parent, false);
            pivot.transform.position = anchorWorld;
            pivot.transform.localRotation = Quaternion.identity;

            // The mesh keeps exactly the pose a plain mesh swap would have shown.
            var body = new GameObject(name + "_mesh");
            body.layer = original.gameObject.layer;
            body.transform.SetParent(pivot.transform, false);
            body.transform.SetPositionAndRotation(original.transform.position, original.transform.rotation);
            body.transform.localScale = original.transform.localScale;
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = body.AddComponent<MeshRenderer>();
            var originalRenderer = original.GetComponent<MeshRenderer>();
            if (originalRenderer != null) renderer.sharedMaterials = originalRenderer.sharedMaterials;
            CharmSystem.ApplyCharmMaterials(renderer, materials);

            var centre = body.transform.TransformPoint(mesh.bounds.center);
            var hang = centre - anchorWorld;
            float length = hang.magnitude;
            if (length < 1e-4f)
            {
                // Anchor at the mesh centre: hang along the parent's down axis instead.
                hang = parent != null ? -parent.up : Vector3.down;
                length = Mathf.Max(mesh.bounds.extents.magnitude * body.transform.lossyScale.x, 1e-3f);
            }

            var restLocal = parent != null ? parent.InverseTransformDirection(hang.normalized) : hang.normalized;
            _states.Add(new State
            {
                Pivot = pivot.transform,
                Parent = parent,
                RestDirectionLocal = restLocal.normalized,
                Length = length,
                Direction = hang.normalized,
                LastPivotPosition = anchorWorld,
            });
            return pivot;
        }

        /// <summary>Advances every swinging charm. Call from LateUpdate, after the phone has moved.</summary>
        public static void Tick(float deltaTime)
        {
            if (_states.Count == 0 || deltaTime <= 0f) return;
            float dt = Mathf.Min(deltaTime, MaxStep);

            for (int i = _states.Count - 1; i >= 0; i--)
            {
                var s = _states[i];
                if (s.Pivot == null)
                {
                    _states.RemoveAt(i);
                    continue;
                }
                if (!s.Pivot.gameObject.activeInHierarchy) continue;

                var pivotPosition = s.Pivot.position;
                var restDirection = s.Parent != null
                    ? s.Parent.TransformDirection(s.RestDirectionLocal).normalized
                    : s.RestDirectionLocal;

                var pivotVelocity = (pivotPosition - s.LastPivotPosition) / deltaTime;
                if ((pivotPosition - s.LastPivotPosition).magnitude > TeleportDistance)
                {
                    s.Direction = restDirection;
                    s.AngularVelocity = Vector3.zero;
                    pivotVelocity = Vector3.zero;
                    s.LastPivotVelocity = Vector3.zero;
                }
                var pivotAcceleration = Vector3.ClampMagnitude((pivotVelocity - s.LastPivotVelocity) / deltaTime, MaxPivotAcceleration);
                s.LastPivotPosition = pivotPosition;
                s.LastPivotVelocity = pivotVelocity;

                // In the pivot's frame the tip feels gravity minus the pivot's own acceleration.
                var felt = (Physics.gravity * GravityScale - pivotAcceleration) / s.Length;
                var force = Vector3.ProjectOnPlane(felt, s.Direction)
                            + (restDirection - s.Direction) * Stiffness
                            - s.AngularVelocity * Damping;
                s.AngularVelocity += force * dt;
                s.AngularVelocity = Vector3.ProjectOnPlane(s.AngularVelocity, s.Direction);

                var direction = (s.Direction + s.AngularVelocity * dt).normalized;
                direction = Vector3.RotateTowards(restDirection, direction, MaxAngleDegrees * Mathf.Deg2Rad, 0f);
                s.Direction = direction;

                var parentRotation = s.Parent != null ? s.Parent.rotation : Quaternion.identity;
                s.Pivot.rotation = Quaternion.FromToRotation(restDirection, direction) * parentRotation;
            }
        }
    }
}
