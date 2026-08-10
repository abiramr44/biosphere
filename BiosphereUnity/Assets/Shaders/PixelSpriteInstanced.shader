// Every non-terrain sprite in the game draws through this shader:
// creatures, trees, rocks, buildings, bridges, items.
//
// It is procedurally instanced -- there is no Mesh per entity, no Transform, no
// SpriteRenderer, no GameObject. A single StructuredBuffer of SpriteInstance
// structs is uploaded once per frame and the vertex shader expands each entry
// into a quad. 20,000 creatures = 1 draw call.
//
// SORTING: the vertex shader writes per-instance depth into clip-space Z and
// the pass uses ZWrite On / ZTest LEqual. That means sprite ordering is resolved
// on the GPU by the depth buffer -- no CPU sort, no SortingGroup, no dynamic
// batching breaks. Depth = layer band base + (gridHeight - worldY), so lower-on-
// screen entities draw in front of higher ones, and an entity band always beats
// the structures band below it. Alpha is CUTOUT (clip), not blended, which is
// what makes depth-buffer sorting valid for pixel art.
//
// If you need genuinely translucent sprites (ghosts, glass), draw them in a
// second pass with ZWrite Off in the Transparent queue after this one.
Shader "Biosphere/PixelSpriteInstanced"
{
    Properties
    {
        _MainTex   ("Sprite Atlas", 2D) = "white" {}
        _AtlasCols ("Atlas Columns", Float) = 16
        _AtlasRows ("Atlas Rows", Float) = 16
        _Cutoff    ("Alpha Cutoff", Range(0,1)) = 0.5
        _DepthScale("Depth Scale", Float) = 0.00001
        _SkyTint   ("Sky Tint", Color) = (1,1,1,1)
        _Brightness("Brightness", Range(0,1)) = 1
    }

    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Cull Off
        ZWrite On
        ZTest LEqual
        Lighting Off
        Fog { Mode Off }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct SpriteInstance
            {
                float4 posSize;     // xy world centre, zw size in world units
                float4 color;       // rgba tint
                float2 atlasDepth;  // x = atlas tile index, y = sort depth
            };

            StructuredBuffer<SpriteInstance> _Instances;

            sampler2D _MainTex;
            float _AtlasCols, _AtlasRows, _Cutoff, _DepthScale;
            fixed4 _SkyTint;
            float _Brightness;

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                fixed4 color : COLOR;
            };

            // Corner offsets for a unit quad, indexed by vertexID % 6.
            static const float2 kCorners[6] = {
                float2(-0.5, -0.5), float2(0.5, -0.5), float2(0.5, 0.5),
                float2(-0.5, -0.5), float2(0.5,  0.5), float2(-0.5, 0.5)
            };
            static const float2 kUVs[6] = {
                float2(0, 0), float2(1, 0), float2(1, 1),
                float2(0, 0), float2(1, 1), float2(0, 1)
            };

            v2f vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                SpriteInstance inst = _Instances[iid];
                uint corner = vid % 6;

                float2 local = kCorners[corner] * inst.posSize.zw;
                float3 world = float3(inst.posSize.xy + local, 0);

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(world, 1.0));

                // Push per-instance sort depth into clip Z. Orthographic camera,
                // so this is a pure offset and never affects screen position.
                o.pos.z += inst.atlasDepth.y * _DepthScale * o.pos.w;

                // Atlas UV: index -> (col, row), flipped vertically to match
                // Unity's bottom-left texture origin.
                float idx  = inst.atlasDepth.x;
                float col  = fmod(idx, _AtlasCols);
                float row  = floor(idx / _AtlasCols);
                float2 cell = float2(1.0 / _AtlasCols, 1.0 / _AtlasRows);
                float2 baseUV = float2(col * cell.x, 1.0 - (row + 1.0) * cell.y);
                o.uv = baseUV + kUVs[corner] * cell;

                // Sprites receive the same day/night grade as the terrain so
                // nothing looks lit from a different world.
                fixed3 graded = inst.color.rgb * _Brightness
                              + _SkyTint.rgb * (1.0 - _Brightness) * 0.5;
                o.color = fixed4(graded, inst.color.a);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.uv);
                clip(tex.a - _Cutoff);          // cutout, so depth sorting is valid
                return fixed4(tex.rgb * i.color.rgb, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
