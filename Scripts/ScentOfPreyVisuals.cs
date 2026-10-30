// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Visual side of Scent of Prey - a small drifting mist of red particles attached to each detected
// living creature, replacing the earlier flat 2D screen-marker version with a genuine animated 3D
// effect. Still fundamentally a "see through walls" trick rather than a literal glowing-mesh shader
// (DFU's runtime mod compiler has no access to Unity's shader compiler, so authoring a brand new
// outline/silhouette shader from loose .cs files isn't possible) - but this reuses an EXISTING shader
// the base game already ships and depends on: Daggerfall/UIBlend (see MaterialReader.
// _DaggerfallUIBlendShaderName), the shader DFU's own 2D UI renders through. Read directly from
// Assets/Shaders/DaggerfallUIBlendShader.shader: it already declares "ZTest Always" and "ZWrite Off"
// baked into the shader itself - normally meant for screen-space UI, but nothing stops it being used
// on a real 3D GameObject too, and those two directives are exactly what "ignore scene depth, draw
// through anything" needs. Since the shader has no explicit Queue tag, this sets the material's own
// renderQueue to 4000 (Unity's "Overlay" queue) explicitly, forcing it to draw dead last - after every
// opaque and transparent object in the scene, including whatever wall might be between the camera and
// the target - so a wall rendered afterward can't paint over it.
//
// The mist texture is a small soft radial falloff generated procedurally at runtime (Texture2D.
// SetPixels) rather than loaded from an asset - mods can't ship or reference Editor-baked texture
// assets from loose .cs source, so this draws its own gradient in code instead.
//
// One caveat found by reading the shader closely: its fragment function only multiplies the texture
// by the material's own _ColorTint uniform - it never reads per-vertex color at all. Unity's
// ParticleSystem normally drives per-particle fade-in/fade-out through vertex color (colorOverLifetime),
// which this shader would silently ignore. Worked around by animating _ColorTint's alpha directly on
// each marker's own material instance every frame instead (a slow ambient pulse) - individual particles
// don't fade independently, but the whole mist cloud breathes together, and size/drift are still
// animated normally since those happen at the geometry level, unaffected by the shader's fragment logic.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;

namespace VoxsVampirismMod
{
    public class ScentOfPreyVisuals : MonoBehaviour
    {
        static readonly Color mistColor = new Color(0.85f, 0.1f, 0.35f, 0.5f);
        // Blood Economy Rebalance (§7): a quest target's marker pulses this gold/amber instead of the
        // ordinary red - "the blood of destiny burns brighter."
        static readonly Color questMistColor = new Color(0.95f, 0.75f, 0.15f, 0.5f);
        const float pulseSpeed = 1.6f;
        const float pulseMinAlpha = 0.28f;
        const float pulseMaxAlpha = 0.6f;

        static readonly List<Transform> targets = new List<Transform>();
        static readonly HashSet<Transform> questTargets = new HashSet<Transform>();
        readonly Dictionary<Transform, ParticleSystem> activeMarkers = new Dictionary<Transform, ParticleSystem>();
        static Texture2D mistTexture;

        // Called by ScentOfPreyEffect on its own real-time scan interval ("Blood Sense Refresh
        // Interval" Mod Settings slider, 1-5 seconds - see ScentOfPreyEffect.cs's own header) with
        // the current in-range living-creature list (plus which of those are quest targets) - marker
        // attach/detach and per-frame animation are handled continuously in Update() below, so
        // markers track moving targets and clean up dead/despawned ones smoothly between scans.
        public static void SetTargets(List<Transform> newTargets, HashSet<Transform> newQuestTargets)
        {
            targets.Clear();
            if (newTargets != null)
                targets.AddRange(newTargets);

            questTargets.Clear();
            if (newQuestTargets != null)
                questTargets.UnionWith(newQuestTargets);
        }

        public static void Clear()
        {
            targets.Clear();
            questTargets.Clear();
        }

        void Update()
        {
            RemoveStaleMarkers();
            AddMissingMarkers();
            AnimatePulse();
        }

        void RemoveStaleMarkers()
        {
            List<Transform> toRemove = null;
            foreach (KeyValuePair<Transform, ParticleSystem> pair in activeMarkers)
            {
                if (pair.Key != null && targets.Contains(pair.Key))
                    continue;

                if (toRemove == null)
                    toRemove = new List<Transform>();
                toRemove.Add(pair.Key);
            }

            if (toRemove == null)
                return;

            for (int i = 0; i < toRemove.Count; i++)
            {
                ParticleSystem marker = activeMarkers[toRemove[i]];
                activeMarkers.Remove(toRemove[i]);
                if (marker != null)
                    Destroy(marker.gameObject);
            }
        }

        void AddMissingMarkers()
        {
            for (int i = 0; i < targets.Count; i++)
            {
                Transform target = targets[i];
                if (target == null || activeMarkers.ContainsKey(target))
                    continue;

                ParticleSystem marker = CreateMistEmitter(target);
                if (marker != null)
                    activeMarkers[target] = marker;
            }
        }

        void AnimatePulse()
        {
            float alpha = Mathf.Lerp(pulseMinAlpha, pulseMaxAlpha, (Mathf.Sin(Time.time * pulseSpeed) + 1f) * 0.5f);

            foreach (KeyValuePair<Transform, ParticleSystem> pair in activeMarkers)
            {
                if (pair.Value == null)
                    continue;

                Color tint = questTargets.Contains(pair.Key) ? questMistColor : mistColor;
                tint.a = alpha;

                ParticleSystemRenderer psRenderer = pair.Value.GetComponent<ParticleSystemRenderer>();
                if (psRenderer != null && psRenderer.material != null)
                    psRenderer.material.SetColor("_ColorTint", tint);
            }
        }

        ParticleSystem CreateMistEmitter(Transform target)
        {
            Material material = CreateMistMaterialInstance();
            if (material == null)
                return null;

            GameObject markerObject = new GameObject("ScentOfPreyMarker");
            markerObject.transform.SetParent(target, false);
            // Parented at the target's own root transform, which is at ground/feet level for
            // humanoid creatures - the shape module below spans upward from there to roughly cover
            // the whole body, rather than emitting from one fixed height (which read as "attached to
            // the top of their head" when this used a small sphere centered a fixed 1 unit up).
            markerObject.transform.localPosition = Vector3.zero;

            ParticleSystem ps = markerObject.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.startLifetime = 1.6f;
            main.startSpeed = 0.2f;
            main.startSize = 0.16f;
            main.startColor = Color.white;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 40;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 12f;

            // A vertical box roughly spanning a humanoid's height (feet at y=0, the root transform's
            // own position, up to a bit above head height) rather than a small sphere at one fixed
            // height - particles spawn anywhere in that column, so the mist reads as covering the
            // whole body instead of hovering at a single point. A fixed height is an approximation
            // that won't scale down for something much shorter than human-sized, but is a reasonable
            // default for the common case (people and most enemies).
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            // 40% of the original (0.6, 2.1, 0.6) box (20% smaller, then another 50% smaller on top),
            // kept grounded at the target's feet (bottom of the box stays at y=0, so position.y is
            // half the current height, not the original).
            shape.scale = new Vector3(0.24f, 0.84f, 0.24f);
            shape.position = new Vector3(0f, 0.42f, 0f);

            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(new Keyframe(0f, 0.4f), new Keyframe(1f, 1.2f));
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            ParticleSystem.VelocityOverLifetimeModule velocityOverLifetime = ps.velocityOverLifetime;
            velocityOverLifetime.enabled = true;
            velocityOverLifetime.space = ParticleSystemSimulationSpace.Local;
            velocityOverLifetime.y = new ParticleSystem.MinMaxCurve(0.15f);

            ParticleSystem.NoiseModule noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.3f;
            noise.frequency = 0.4f;
            noise.scrollSpeed = 0.2f;

            ParticleSystemRenderer psRenderer = ps.GetComponent<ParticleSystemRenderer>();
            psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            psRenderer.material = material;
            psRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            psRenderer.receiveShadows = false;

            return ps;
        }

        static Material CreateMistMaterialInstance()
        {
            if (mistTexture == null)
                mistTexture = CreateMistTexture();

            Shader shader = Shader.Find(MaterialReader._DaggerfallUIBlendShaderName);
            if (shader == null)
                return null;

            Material material = new Material(shader);
            material.mainTexture = mistTexture;
            material.SetColor("_ColorTint", mistColor);

            // No explicit Queue tag on this shader (it's normally driven by DFU's own immediate-mode
            // UI blit, not Unity's regular scene render queue) - force it to Overlay (4000) explicitly
            // so it draws after every opaque/transparent object in the scene, walls included.
            material.renderQueue = 4000;

            return material;
        }

        static Texture2D CreateMistTexture()
        {
            const int size = 32;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[size * size];
            Vector2 center = new Vector2(size / 2f, size / 2f);
            float maxDist = size / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float alpha = Mathf.Clamp01(1f - dist / maxDist);
                    alpha *= alpha;
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }
    }
}
