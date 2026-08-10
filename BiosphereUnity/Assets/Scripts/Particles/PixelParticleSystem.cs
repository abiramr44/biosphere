using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Biosphere.Core;

namespace Biosphere.Particles
{
    /// <summary>One GPU particle. 32 bytes, must match PixelParticle.shader.</summary>
    public struct ParticleInstance
    {
        public float2 Pos;
        public float  Size;
        public float  Pad;
        public float4 Color;
    }

    /// <summary>CPU-side simulation state, kept in a separate array from the GPU
    /// struct so the upload never carries fields the shader doesn't read.</summary>
    public struct ParticleState
    {
        public float2 Vel;
        public float  Life;        // remaining, seconds
        public float  LifeTotal;
        public float4 ColorStart;
        public float4 ColorEnd;
        public float  SizeStart;
        public float  SizeEnd;
        public float  Drag;
        public float  Gravity;
    }

    public enum FxPreset { Fire, Explosion, Magic, Smoke, Splash, Spark }

    /// <summary>
    /// Pooled, Burst-simulated, single-draw-call pixel particles.
    ///
    /// Design constraints this satisfies:
    ///  - ZERO per-particle GameObjects. Unity's built-in ParticleSystem costs a
    ///    component + a mesh rebuild per system; 200 simultaneous fires would be
    ///    200 systems. Here 200 fires are 200 emitter records feeding ONE pool.
    ///  - ZERO allocation at runtime. The pool is sized once at startup
    ///    (WorldConfig.MaxParticles) and never grows. Emitting past capacity
    ///    recycles the oldest particle instead of allocating.
    ///  - ZERO texture bandwidth. Particles are untextured solid squares, so
    ///    VRAM cost is the instance buffer only: 32 KB per 1,000 particles.
    ///  - CPU-side simulation in Burst, GPU does nothing but expand quads.
    ///    That is the requested CPU-bound-over-GPU-bound tradeoff.
    /// </summary>
    public class PixelParticleSystem : MonoBehaviour, IDisposable
    {
        [SerializeField] private Shader particleShader;
        [Tooltip("Particle square size in screen pixels at zoom 1. 1-3 keeps the " +
                 "retro look; anything larger stops reading as a pixel.")]
        [SerializeField] private float basePixelSize = 2f;

        private WorldConfig _cfg;
        private Material _mat;
        private MaterialPropertyBlock _props;
        private GraphicsBuffer _gpuBuffer;

        private NativeArray<ParticleInstance> _instances;
        private NativeArray<ParticleState> _states;
        private int _capacity;
        private int _alive;           // particles are kept packed in [0, _alive)
        private int _recycleCursor;   // round-robin overwrite when at capacity

        private Unity.Mathematics.Random _rng;
        private Bounds _bounds;

        private static readonly int ParticlesId = Shader.PropertyToID("_Particles");

        public int AliveCount => _alive;
        public int Capacity => _capacity;

        public void Initialize(WorldConfig cfg)
        {
            _cfg = cfg;
            _capacity = cfg.MaxParticles;
            _rng = new Unity.Mathematics.Random(0xC0FFEEu);

            _instances = new NativeArray<ParticleInstance>(_capacity, Allocator.Persistent,
                                                           NativeArrayOptions.ClearMemory);
            _states = new NativeArray<ParticleState>(_capacity, Allocator.Persistent,
                                                     NativeArrayOptions.ClearMemory);

            _gpuBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _capacity, 32);
            _mat = new Material(particleShader != null
                ? particleShader
                : Shader.Find("Biosphere/PixelParticle"));
            _props = new MaterialPropertyBlock();

            _bounds = new Bounds(new Vector3(cfg.GridW * 0.5f, cfg.GridH * 0.5f, 0f),
                                 new Vector3(cfg.GridW + 32, cfg.GridH + 32, 100f));
        }

        // ---------------- Emission ----------------
        /// <summary>Emit a burst at a world position using a preset. All presets
        /// funnel through the same pool -- there is no per-effect object.</summary>
        public void Emit(FxPreset preset, float2 worldPos, int count = -1, float scale = 1f)
        {
            switch (preset)
            {
                case FxPreset.Explosion:
                    EmitRadial(worldPos, count < 0 ? 90 : count,
                        speed: 9f * scale, speedJitter: 6f * scale,
                        life: 0.55f, lifeJitter: 0.35f,
                        cStart: new float4(1f, 0.95f, 0.55f, 1f),
                        cEnd:   new float4(0.85f, 0.15f, 0.02f, 0f),
                        sizeStart: 1.6f * scale, sizeEnd: 0.6f * scale,
                        drag: 3.5f, gravity: -1.5f);
                    break;

                case FxPreset.Fire:
                    EmitRadial(worldPos, count < 0 ? 6 : count,
                        speed: 0.6f * scale, speedJitter: 0.9f * scale,
                        life: 0.7f, lifeJitter: 0.4f,
                        cStart: new float4(1f, 0.75f, 0.2f, 1f),
                        cEnd:   new float4(0.6f, 0.05f, 0.0f, 0f),
                        sizeStart: 1.2f * scale, sizeEnd: 0.4f * scale,
                        drag: 1.2f, gravity: 2.2f);       // fire rises
                    break;

                case FxPreset.Magic:
                    EmitRadial(worldPos, count < 0 ? 30 : count,
                        speed: 2.5f * scale, speedJitter: 3f * scale,
                        life: 1.1f, lifeJitter: 0.6f,
                        cStart: new float4(0.65f, 0.35f, 1f, 1f),
                        cEnd:   new float4(0.15f, 0.9f, 1f, 0f),
                        sizeStart: 1.0f * scale, sizeEnd: 1.4f * scale,
                        drag: 0.8f, gravity: 0.6f);
                    break;

                case FxPreset.Smoke:
                    EmitRadial(worldPos, count < 0 ? 8 : count,
                        speed: 0.4f * scale, speedJitter: 0.6f * scale,
                        life: 1.8f, lifeJitter: 0.9f,
                        cStart: new float4(0.35f, 0.34f, 0.36f, 0.7f),
                        cEnd:   new float4(0.2f, 0.2f, 0.22f, 0f),
                        sizeStart: 1.2f * scale, sizeEnd: 3.0f * scale,
                        drag: 1.6f, gravity: 1.1f);
                    break;

                case FxPreset.Splash:
                    EmitRadial(worldPos, count < 0 ? 24 : count,
                        speed: 5f * scale, speedJitter: 3f * scale,
                        life: 0.45f, lifeJitter: 0.25f,
                        cStart: new float4(0.6f, 0.9f, 1f, 1f),
                        cEnd:   new float4(0.15f, 0.45f, 0.8f, 0f),
                        sizeStart: 1.0f * scale, sizeEnd: 0.5f * scale,
                        drag: 2f, gravity: -9f);
                    break;

                default: // Spark
                    EmitRadial(worldPos, count < 0 ? 12 : count,
                        speed: 7f * scale, speedJitter: 5f * scale,
                        life: 0.3f, lifeJitter: 0.2f,
                        cStart: new float4(1f, 1f, 0.85f, 1f),
                        cEnd:   new float4(1f, 0.5f, 0.1f, 0f),
                        sizeStart: 1.0f * scale, sizeEnd: 0.5f * scale,
                        drag: 4f, gravity: -6f);
                    break;
            }
        }

        private void EmitRadial(float2 pos, int count, float speed, float speedJitter,
                                float life, float lifeJitter, float4 cStart, float4 cEnd,
                                float sizeStart, float sizeEnd, float drag, float gravity)
        {
            float px = basePixelSize / _cfg.PixelsPerUnit;   // world units per particle pixel
            for (int k = 0; k < count; k++)
            {
                int i = Allocate();
                float ang = _rng.NextFloat(0f, 2f * math.PI);
                float spd = speed + _rng.NextFloat(-speedJitter, speedJitter);
                float lt = math.max(0.05f, life + _rng.NextFloat(-lifeJitter, lifeJitter));

                _instances[i] = new ParticleInstance
                {
                    Pos = pos + new float2(math.cos(ang), math.sin(ang)) * _rng.NextFloat(0f, 0.3f),
                    Size = sizeStart * px,
                    Color = cStart
                };
                _states[i] = new ParticleState
                {
                    Vel = new float2(math.cos(ang), math.sin(ang)) * spd,
                    Life = lt, LifeTotal = lt,
                    ColorStart = cStart, ColorEnd = cEnd,
                    SizeStart = sizeStart * px, SizeEnd = sizeEnd * px,
                    Drag = drag, Gravity = gravity
                };
            }
        }

        /// <summary>Grab a slot. Below capacity this is a bump allocation; at
        /// capacity it round-robins over the oldest region rather than dropping
        /// the emit or growing the pool. Frame cost stays bounded no matter how
        /// much chaos the player causes.</summary>
        private int Allocate()
        {
            if (_alive < _capacity) return _alive++;
            _recycleCursor = (_recycleCursor + 1) % _capacity;
            return _recycleCursor;
        }

        // ---------------- Simulation + draw ----------------
        private void LateUpdate()
        {
            if (_cfg == null || _alive == 0) return;

            var job = new ParticleStepJob
            {
                Instances = _instances,
                States = _states,
                Dt = Time.deltaTime,
                Count = _alive
            };
            job.Schedule(_alive, 256).Complete();

            Compact();

            if (_alive > 0)
            {
                _gpuBuffer.SetData(_instances, 0, 0, _alive);
                _props.SetBuffer(ParticlesId, _gpuBuffer);
#if UNITY_2022_2_OR_NEWER
                var rp = new RenderParams(_mat)
                {
                    worldBounds = _bounds,
                    matProps = _props,
                    receiveShadows = false,
                    shadowCastingMode = ShadowCastingMode.Off
                };
                Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 6, _alive);
#else
                _mat.SetBuffer(ParticlesId, _gpuBuffer);
                Graphics.DrawProcedural(_mat, _bounds, MeshTopology.Triangles,
                                        6, _alive, null, _props);
#endif
            }
        }

        /// <summary>Swap-remove dead particles so the live set stays packed in
        /// [0, _alive) and the GPU upload is contiguous. Serial but trivial --
        /// it is a handful of struct copies over the dead tail.</summary>
        private void Compact()
        {
            int i = 0;
            while (i < _alive)
            {
                if (_states[i].Life > 0f) { i++; continue; }
                int last = _alive - 1;
                if (i != last)
                {
                    _instances[i] = _instances[last];
                    _states[i] = _states[last];
                }
                _alive--;
            }
        }

        public void Clear() => _alive = 0;

        public void Dispose()
        {
            _gpuBuffer?.Dispose();
            if (_instances.IsCreated) _instances.Dispose();
            if (_states.IsCreated) _states.Dispose();
        }

        private void OnDestroy()
        {
            Dispose();
            if (_mat != null) Destroy(_mat);
        }
    }

    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct ParticleStepJob : IJobParallelFor
    {
        public NativeArray<ParticleInstance> Instances;
        public NativeArray<ParticleState> States;
        public float Dt;
        public int Count;

        public void Execute(int i)
        {
            var s = States[i];
            if (s.Life <= 0f) return;

            s.Life -= Dt;
            var inst = Instances[i];

            s.Vel.y += s.Gravity * Dt;
            s.Vel -= s.Vel * math.min(1f, s.Drag * Dt);
            inst.Pos += s.Vel * Dt;

            float t = 1f - math.saturate(s.Life / math.max(1e-4f, s.LifeTotal));
            inst.Color = math.lerp(s.ColorStart, s.ColorEnd, t);
            inst.Size = math.lerp(s.SizeStart, s.SizeEnd, t);

            if (s.Life <= 0f) inst.Color.w = 0f;

            States[i] = s;
            Instances[i] = inst;
        }
    }
}
