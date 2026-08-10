// Terrain plane. One texel = one tile, point sampled.
//
// Day/night grade, cloud shadow and rain sparkle are applied HERE rather than
// baked into the texture, so a full sunset costs zero CPU work and zero texture
// uploads -- just three changing uniforms.
Shader "Biosphere/TerrainUnlit"
{
    Properties
    {
        _MainTex   ("Terrain Albedo", 2D) = "white" {}
        _CloudTex  ("Cloud Density (R)", 2D) = "black" {}
        _SkyTint   ("Sky Tint", Color) = (1,1,1,1)
        _Brightness("Brightness", Range(0,1)) = 1
        _Rain      ("Rain", Range(0,1)) = 0
        _TimeSeed  ("Time Seed", Float) = 0
        _CloudOpacity ("Cloud Opacity", Range(0,1)) = 0.5
        _CloudShadowStrength ("Cloud Shadow Strength", Range(0,1)) = 0.6
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
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
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _CloudTex;
            float4 _MainTex_TexelSize;
            fixed4 _SkyTint;
            float _Brightness;
            float _Rain;
            float _TimeSeed;
            float _CloudOpacity;
            float _CloudShadowStrength;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // Cheap hash for the rain sparkle -- no texture fetch, no noise map.
            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed3 col = tex2D(_MainTex, i.uv).rgb;
                float cloud = tex2D(_CloudTex, i.uv).r;

                // Cloud shadow darkens the ground.
                float light = _Brightness * (1.0 - cloud * _CloudShadowStrength);

                // Ambient tint fills in at low brightness (night blue / dawn orange).
                col = col * light + _SkyTint.rgb * (1.0 - _Brightness) * 0.5;

                // Cloud body drawn over the top.
                col = lerp(col, fixed3(0.88, 0.88, 0.92), cloud * _CloudOpacity);

                // Rain sparkle: sparse per-tile flicker, keyed off tile coords so
                // it lands exactly on the pixel grid instead of screen space.
                if (_Rain > 0.5)
                {
                    float2 tile = floor(i.uv * _MainTex_TexelSize.zw);
                    float s = hash21(tile + floor(_TimeSeed * 20.0));
                    col += step(0.985, s) * 0.3;
                }

                return fixed4(saturate(col), 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
