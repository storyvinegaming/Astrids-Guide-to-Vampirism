// Project:         Astrid's Guide to Vampirism (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Visual side of Mist Form - a small drifting cloud of pale mist parented directly to the main
// camera, hovering near the bottom of the viewport, so it always reads as "your own body dissolving
// into mist" no matter which way the player looks. Parenting the emitter to the camera transform and
// running it in local simulation space (ParticleSystemSimulationSpace.Local) means particles stay
// anchored to the same on-screen position as the camera moves/turns, rather than being left behind in
// world space the way ScentOfPreyVisuals' world-attached markers are.
//
// Same "reuse DFU's own Daggerfall/UIBlend shader plus a procedurally generated soft radial texture"
// technique ScentOfPreyVisuals already established (see that file's own header for exactly why - mods
// can't ship or reference Editor-baked texture/shader assets from loose .cs source). Not strictly
// needed for its depth-ignoring behavior here (this sits a fixed couple of units in front of the
// camera, not something that needs to be seen through walls), but kept anyway for the same reliable
// up-close-to-geometry behavior without z-fighting against nearby walls.
//
// Toggled on/off by MistFormEffect exactly like HuntersSightVisuals/ScentOfPreyVisuals are - created
// lazily on first activation (GameManager.Instance.MainCamera isn't guaranteed to exist yet at mod
// init), left disabled-but-alive between activations rather than destroyed and recreated each time.

using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;

namespace VoxsVampirismMod
{
    public class MistFormVisuals : MonoBehaviour
    {
        static readonly Color mistColor = new Color(0.85f, 0.85f, 0.9f, 0.35f);
        const float pulseSpeed = 0.9f;
        const float pulseMinAlpha = 0.16f;
        const float pulseMaxAlpha = 0.38f;

        static bool wantActive;
        ParticleSystem particleSystemInstance;
        Material materialInstance;
        static Texture2D mistTexture;

        public static void SetActive(bool active)
        {
            wantActive = active;
        }

        // Bug fix - same stale-static-state-across-a-load pattern as HuntersSightVisuals' own fix,
        // see that file's own comment for the full reasoning. MistFormEffect.Resume() only re-enables
        // this if the loaded save genuinely has an active Mist Form to restore; a save with nothing to
        // restore left wantActive however a previous save/session last set it. Reset unconditionally
        // on every load start, before any real restoration gets a chance to turn it back on correctly.
        void OnEnable()
        {
            SaveLoadManager.OnStartLoad += ResetOnStartLoad;
        }

        void OnDisable()
        {
            SaveLoadManager.OnStartLoad -= ResetOnStartLoad;
        }

        static void ResetOnStartLoad(SaveData_v1 saveData)
        {
            wantActive = false;
        }

        void Update()
        {
            EnsureCreated();
            if (particleSystemInstance == null)
                return;

            if (particleSystemInstance.gameObject.activeSelf != wantActive)
                particleSystemInstance.gameObject.SetActive(wantActive);

            if (!wantActive)
                return;

            float alpha = Mathf.Lerp(pulseMinAlpha, pulseMaxAlpha, (Mathf.Sin(Time.time * pulseSpeed) + 1f) * 0.5f);
            Color tint = mistColor;
            tint.a = alpha;
            if (materialInstance != null)
                materialInstance.SetColor("_ColorTint", tint);
        }

        void EnsureCreated()
        {
            if (particleSystemInstance != null)
                return;

            Camera mainCamera = GameManager.Instance.MainCamera;
            if (mainCamera == null)
                return;

            materialInstance = CreateMistMaterialInstance();
            if (materialInstance == null)
                return;

            GameObject markerObject = new GameObject("MistFormVisuals");
            markerObject.transform.SetParent(mainCamera.transform, false);
            // A fixed camera-local offset, low and close, roughly the lower quarter of a typical FOV
            // at this distance - not exact frustum math, just close enough to read as "near the bottom
            // of the viewport" without needing per-camera FOV/aspect queries.
            markerObject.transform.localPosition = new Vector3(0f, -0.55f, 1.0f);
            markerObject.SetActive(false);

            ParticleSystem ps = markerObject.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.startLifetime = 1.4f;
            main.startSpeed = 0.08f;
            main.startSize = 0.14f;
            main.startColor = Color.white;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 30;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 8f;

            // A wide, flat box hugging the bottom edge - mist drifting past your own dissolved feet
            // rather than a cloud centered on the middle of the screen.
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(1.8f, 0.3f, 0.5f);
            shape.position = Vector3.zero;

            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(0.5f, 1f), new Keyframe(1f, 0.6f));
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            ParticleSystem.VelocityOverLifetimeModule velocityOverLifetime = ps.velocityOverLifetime;
            velocityOverLifetime.enabled = true;
            velocityOverLifetime.space = ParticleSystemSimulationSpace.Local;
            velocityOverLifetime.y = new ParticleSystem.MinMaxCurve(0.08f);

            ParticleSystem.NoiseModule noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.22f;
            noise.frequency = 0.3f;
            noise.scrollSpeed = 0.15f;

            ParticleSystemRenderer psRenderer = ps.GetComponent<ParticleSystemRenderer>();
            psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            psRenderer.material = materialInstance;
            psRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            psRenderer.receiveShadows = false;

            particleSystemInstance = ps;
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

            // No explicit Queue tag on this shader - force Overlay (4000) so it draws after every
            // opaque/transparent object in the scene, same reasoning as ScentOfPreyVisuals' own use.
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
