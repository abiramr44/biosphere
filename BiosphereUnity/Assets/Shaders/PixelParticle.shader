// Pixel particles: additive, unlit, no texture fetch at all.
//
// Each particle is a solid axis-aligned square of 1-3 world "pixels". There is
// no sprite atlas lookup, no soft-particle depth read, no per-particle material.
// The entire cost is one StructuredBuffer read and a colour write, which is why
// 30,000 of them still cost a fraction of a millisecond of GPU time.
//
// ZWrite is Off and ZTest is Off: particles always draw over the world, in the
// order the emitter wrote them. That is deliberate -- depth-testing chaotic FX
// against a cutout depth buffer produces visible popping at pixel scale.
Shader "Biosphere/PixelParticle"
{
    Properties
    {
        _Softness ("Edge Softness", Range(0,0.5)) = 0.0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+100" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha One          // additive: fire/magic glow stacks naturally
        Lighting Off
        Fog { Mode Off }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            struct ParticleInstance
            {
                float2 pos;     // world position
                float  size;    // world units (square)
                float  pad;
                float4 color;   // rgba, alpha already faded by the sim job
            };

            StructuredBuffer<ParticleInstance> _Particles;
            float _Softness;

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 local : TEXCOORD0;
                fixed4 color : COLOR;
            };

            static const float2 kCorners[6] = {
                float2(-0.5, -0.5), float2(0.5, -0.5), float2(0.5, 0.5),
                float2(-0.5, -0.5), float2(0.5,  0.5), float2(-0.5, 0.5)
            };

            v2f vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                ParticleInstance p = _Particles[iid];
                float2 c = kCorners[vid % 6];

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(p.pos + c * p.size, 0, 1));
                o.local = c;
                o.color = p.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = i.color;
                if (_Softness > 0.001)
                {
                    // Optional round falloff. Off by default -- hard square
                    // pixels are the correct look and cost one instruction less.
                    float d = length(i.local) * 2.0;
                    c.a *= saturate((1.0 - d) / max(0.001, _Softness));
                }
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
