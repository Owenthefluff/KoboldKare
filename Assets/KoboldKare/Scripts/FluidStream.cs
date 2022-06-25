using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using PenetrationTech;
using UnityEngine;
using UnityEngine.VFX;

public class FluidStream : CatmullDeformer {
    private class FluidParticle {
        public Vector3 position;
        public Vector3 lastPosition;

        public Vector3 interpPosition {
            get {
                // extrapolation, because interpolation is delayed by fixedDeltaTime
                float timeSinceLastUpdate = Time.time - Time.fixedTime;
                return Vector3.Lerp(position, position + (position - lastPosition),
                    timeSinceLastUpdate / Time.fixedDeltaTime);
            }
        }
    }

    private GenericReagentContainer container;
    private ReagentContents midairContents;
    private float startClip, endClip = 0f;
    private List<Vector3> points;
    private const float velocity = 0.1f;
    private bool firing = false;
    private bool coroutineRunning = false;
    private WaitForSeconds waitTime;
    private RaycastHit[] raycastHits = new RaycastHit[30];
    private Collider[] colliders = new Collider[30];

    private MaterialPropertyBlock block;
    private static readonly int StartClipID = Shader.PropertyToID("_StartClip");
    private static readonly int EndClipID = Shader.PropertyToID("_EndClip");
    private static readonly int ColorID = Shader.PropertyToID("_Color");
    private static readonly int FluidColor = Shader.PropertyToID("_FluidColor");
    private int counter;
    private List<FluidParticle> particles;
    [SerializeField]
    private float fluidLength = 4f;
    [SerializeField] private Material decalProjector;
    [SerializeField] private Material decalProjectorSubtractive;
    [SerializeField] private VisualEffect splatter;
    [SerializeField] private AudioPack splatterSounds;
    [SerializeField] private AudioPack waterSpraySound;
    private HashSet<GenericReagentContainer> hitContainers;
    private AudioSource audioSource;
    private AudioSource waterHitSource;
    private bool particleCoroutineRunning;

    [SerializeField]
    private Rigidbody body;

    void Start() {
        block = new MaterialPropertyBlock();
        points = new List<Vector3>();
        midairContents = new ReagentContents();
        particles = new List<FluidParticle>();
        waitTime = new WaitForSeconds(0.2f);
        hitContainers = new HashSet<GenericReagentContainer>();
        if (audioSource == null) {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.maxDistance = 20f;
            audioSource.minDistance = 0.2f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.spatialBlend = 1f;
            audioSource.loop = true;
        }

        audioSource.enabled = false;

        if (waterHitSource == null) {
            waterHitSource = splatter.gameObject.AddComponent<AudioSource>();
            waterHitSource.playOnAwake = false;
            waterHitSource.maxDistance = 20f;
            waterHitSource.minDistance = 0.2f;
            waterHitSource.rolloffMode = AudioRolloffMode.Linear;
            waterHitSource.spatialBlend = 1f;
            waterHitSource.loop = false;
        }

        waterHitSource.enabled = false;
    }

    public void OnFire(GenericReagentContainer source) {
        container = source;
        firing = true;
        if (!particleCoroutineRunning) {
            particleCoroutineRunning = true;
            StartCoroutine(FireParticles());
        }

        if (!coroutineRunning) {
            coroutineRunning = true;
            StartCoroutine(Output());
        }
    }

    public void OnEndFire() {
        firing = false;
    }

    private void FixedUpdate() {
        foreach(var particle in particles) {
            Vector3 newPosition = particle.position + (particle.position - particle.lastPosition) + Physics.gravity * (Time.deltaTime * Time.deltaTime);
            particle.lastPosition = particle.position;
            particle.position = newPosition;
        }
    }

    private void Update() {
        points.Clear();
        points.Add(rootBone.position);
        for (int i = particles.Count - 1; i >= 0; i--) {
            points.Add(particles[i].interpPosition);
        }

        if (points.Count <= 1) {
            points.Add(rootBone.position + rootBone.TransformDirection(localRootForward));
        }

        path.SetWeightsFromPoints(points);
        rootBone.transform.localRotation *= Quaternion.AngleAxis(-Time.deltaTime * 100f, localRootForward);
        foreach (var renderMask in GetTargetRenderers()) {
            renderMask.renderer.GetPropertyBlock(block);
            block.SetFloat(StartClipID, startClip);
            block.SetFloat(EndClipID, endClip);
            if (container != null && container.volume > 0.1f) {
                block.SetColor(FluidColor, container.GetColor());
            }

            renderMask.renderer.SetPropertyBlock(block);
        }
    }

    private IEnumerator FireParticles() {
        while (firing) {
            Vector3 vel = rootBone.TransformDirection(localRootForward) * velocity;
            if (body != null) {
                vel += body.GetPointVelocity(rootBone.position) * (Time.fixedDeltaTime * 0.5f);
            }

            Vector3 pos = rootBone.transform.position + rootBone.TransformDirection(localRootForward) * 0.08f;
            particles.Add(new FluidParticle() {
                position = pos,
                lastPosition = pos - vel
            });
            if (particles.Count > 6) {
                particles.RemoveAt(0);
            }
            yield return waitTime;
        }
        particleCoroutineRunning = false;
    }

    private bool RaycastStream(float startDistance, float endDistance, out RaycastHit hit, out float distance) {
        float distanceAcc = 0f;
        for (int i = 0; i < points.Count-1; i++) {
            Vector3 diff = points[i+1] - points[i];
            float dist = diff.magnitude;
            int hits = Physics.SphereCastNonAlloc(points[i], 0.25f, diff.normalized, raycastHits, dist, GameManager.instance.waterSprayHitMask, QueryTriggerInteraction.Ignore);
            if (hits > 0) {
                float closestDist = float.MaxValue;
                int closestHit = -1;
                for (int j = 0; j < hits; j++) {
                    if (raycastHits[j].distance < closestDist && (distanceAcc+raycastHits[j].distance) > startDistance
                                                              && (distanceAcc+raycastHits[j].distance) < endDistance) {
                        closestDist = raycastHits[j].distance;
                        closestHit = j;
                    }
                }

                if (closestHit != -1) {
                    hit = raycastHits[closestHit];
                    distance = distanceAcc + hit.distance;
                    return true;
                }
            }
            distanceAcc += dist;
        }

        hit = new RaycastHit();
        distance = 0f;
        return false;
    }

    private void OnSplash(RaycastHit hit, float percentageLoss) {
        if (counter++ % 3 == 0) {
            splatter.transform.position = hit.point + hit.normal * 0.1f;
            splatter.transform.rotation = Quaternion.LookRotation(hit.normal, Vector3.up);
            splatter.SendEvent("Splatter");
            if (!waterHitSource.isPlaying) {
                splatterSounds.Play(waterHitSource);
            }
        }

        if (midairContents.volume <= 0f) {
            return;
        }

        Color color = midairContents.GetColor();
        decalProjector.SetColor(ColorID, color);
        decalProjectorSubtractive.SetColor(ColorID, color);
        SkinnedMeshDecals.PaintDecal.RenderDecalInSphere(hit.point, 0.5f, midairContents.IsCleaningAgent() ? decalProjectorSubtractive : decalProjector,
            Quaternion.identity,
            GameManager.instance.decalHitMask);

        hitContainers.Clear();
        int hits = Physics.OverlapSphereNonAlloc(hit.point, 0.5f, colliders, GameManager.instance.waterSprayHitMask);
        for (int i = 0; i < hits; i++) {
            GenericReagentContainer cont = colliders[i].GetComponentInParent<GenericReagentContainer>();
            if (cont != null) {
                hitContainers.Add(cont);
            }
        }
        float perVolume = ((midairContents.volume * percentageLoss) + 1f) / hitContainers.Count;
        foreach (GenericReagentContainer cont in hitContainers) {
            cont.AddMix(midairContents.Spill(perVolume), GenericReagentContainer.InjectType.Spray);
        }

    }

    private IEnumerator Output() {
        audioSource.enabled = true;
        waterHitSource.enabled = true;
        startClip = endClip = 0f;
        while (firing || Math.Abs(startClip - endClip) > 0.01f) {
            endClip = Mathf.MoveTowards(endClip, 1f, Time.deltaTime);
            if (container.volume > 0f && firing) {
                if (!audioSource.isPlaying) {
                    waterSpraySound.Play(audioSource);
                }
                Color reagentColor = container.GetColor();
                splatter.SetVector4("Color",
                    new Vector4(reagentColor.r, reagentColor.g, reagentColor.b, 1f));
                midairContents.AddMix(container.Spill(Time.deltaTime * 5f));
                startClip = 0f;
            } else {
                audioSource.Stop();
                startClip = Mathf.MoveTowards(startClip, endClip, Time.deltaTime);
            }

            if (RaycastStream(startClip*fluidLength, endClip*fluidLength, out RaycastHit hit, out float distance)) {
                float newEndClip = distance / fluidLength;
                if (newEndClip > startClip && newEndClip < endClip) {
                    OnSplash(hit, 0.1f);
                    endClip = newEndClip;
                }
            }
            
            if (Math.Abs(endClip - 1f) < 0.01f) {
                midairContents.Spill(Time.deltaTime * 5f);
            }

            yield return null;
        }

        midairContents.Spill(midairContents.volume);
        coroutineRunning = false;
        particles.Clear();
        audioSource.enabled = false;
        waterHitSource.enabled = false;
    }
}
