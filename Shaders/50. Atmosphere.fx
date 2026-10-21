//=============================================================================
// 50. Atmosphere.fx  (NOVA 대기·안개 화면 전체 패스 - AtmospherePass.cpp)
//  불투명 물체·하늘을 그린 뒤, 장면 색 사본 + 깊이에서 월드 위치를 되살려 대기 원근·높이 안개를 입힌다 (49. AtmosphereCommon.fx).
//  물은 자기 셰이더에서 같은 함수로 안개를 입힌다.
//=============================================================================
#include "49. AtmosphereCommon.fx"

cbuffer cbAtmospherePass
{
    float4x4 gAtmInvViewProj;
    float4 gAtmViewport;    // x, y = 왼쪽 위, z, w = 폭, 높이
};

Texture2D gAtmSceneColor;          // 장면 색 사본 (감마)
Texture2D<float> gAtmDepth;        // 장면 깊이 (0~1)

// ---- 볼륨 안개 (HDRP Fog 의 Volumetric Fog): 카메라 ~ 점 사이를 걸으며 해 그림자 맵 (캐스케이드) 으로 빛이 닿는 곳만 산란 —
//  나무 · 산 사이로 빛줄기 (갓 레이). 걸음마다 지터 (프레임마다 바뀌어 TAA 가 매끈하게), 밀도 = 높이 안개와 같은 식 × Multiplier
cbuffer cbAtmVolumetric
{
    float4x4 gAtmShadowT[8];   // 해 (방향광 0) 캐스케이드 변환 (월드 → 맵 uv · 깊이)
    float4 gAtmCascades[8];    // 캐스케이드 구 (xyz 중심, w 반지름²)
    float4 gAtmShadowInfo;     // x 캐스케이드 수 (0 = 그림자 없음 → 모두 빛), y 그림자 세기
    float4 gAtmVol;            // x 거리 (m), y Multiplier, z 걸음 수, w 프레임 (지터)
    float4 gAtmVolAlbedo;      // rgb 알베도 (선형), w 1 = 켜짐
};
Texture2DArray gAtmSunShadow;
SamplerComparisonState samAtmShadow
{
    Filter = COMPARISON_MIN_MAG_LINEAR_MIP_POINT;
    AddressU = BORDER;
    AddressV = BORDER;
    AddressW = BORDER;
    BorderColor = float4(1.0f, 1.0f, 1.0f, 1.0f);
    ComparisonFunc = LESS_EQUAL;
};

// 이 점에 해가 닿나 (1 = 빛). 가장 가까운 캐스케이드 하나 (32 의 SelectCascade 와 같은 고르기, 경계 섞기 없음 — 지터 · TAA 가 가린다)
float AtmSunVisible(float3 p)
{
    const int count = (int)gAtmShadowInfo.x;
    int cascade = -1;
    [unroll]
    for (int c = 7; c >= 0; --c)
    {
        const float3 d = p - gAtmCascades[c].xyz;
        if (c < count && dot(d, d) < gAtmCascades[c].w)
            cascade = c;
    }
    if (cascade < 0)
        return 1.0f;
    const float3 coord = mul(float4(p, 1.0f), gAtmShadowT[cascade]).xyz;
    if (coord.z > 1.0f)
        return 1.0f;
    const float s = gAtmSunShadow.SampleCmpLevelZero(samAtmShadow, float3(coord.xy, (float)cascade), coord.z).r;
    return lerp(1.0f, s, gAtmShadowInfo.y);
}

// 화면 픽셀 · 프레임마다 다른 0 ~ 1 (Interleaved Gradient Noise — 걸음 시작을 흔든다)
float AtmNoise(float2 pix, float frame)
{
    pix += 5.588238f * frame;
    return frac(52.9829189f * frac(dot(pix, float2(0.06711056f, 0.00583715f))));
}

// 카메라 → posW 방향 V, 거리 dist 까지의 해 빛 산란 (선형)
float3 AtmVolumetric(float3 V, float dist, float2 pix)
{
    const float len = min(dist, gAtmVol.x);
    const int steps = (int)gAtmVol.z;
    if (len <= 0.0f || steps <= 0)
        return 0.0f;
    const float ds = len / steps;
    const float jitter = AtmNoise(pix, gAtmVol.w);
    const float3 eye = gAtmEye.xyz;
    float transmittance = 1.0f;
    float scatter = 0.0f;
    [loop]
    for (int i = 0; i < steps; ++i)
    {
        const float t = (i + jitter) * ds;
        const float3 p = eye + V * t;
        const float sigma = gAtmFog.x * exp(-clamp((p.y - gAtmFog.y) * gAtmFog.z, -80.0f, 80.0f)) * gAtmVol.y;
        scatter += transmittance * sigma * AtmSunVisible(p) * ds;
        transmittance *= exp(-sigma * ds);
    }
    const float phase = AtmPhaseHG(dot(V, gAtmSunDir.xyz), gAtmFog2.w);
    return gAtmSunColor.rgb * gAtmVolAlbedo.rgb * phase * scatter * 0.25f;
}


// ---- Local Volumetric Fog (HDRP · Godot FogVolume): 상자 · 타원체 안만 걷는다 (볼륨마다 24 걸음, 먼 볼륨부터 섞기)
//  밀도 = 1 / Fog Distance × 가장자리 (Blend Distance) × 노이즈 × 거리 옅어짐, 빛 = 해 (그림자) + 점광 · 스포트광 8 개 + 하늘 환경광
cbuffer cbLocalFog
{
    float4x4 gLFWorldToLocal[16];   // 월드 → 단위 공간 (상자 [-1,1]³ · 타원체 반지름 1)
    float4 gLFAlbedo[16];           // rgb 알베도 (선형), w 소광 (1/m)
    float4 gLFHalf[16];             // xyz 반 크기 (m), w Blend Distance (m)
    float4 gLFShape[16];            // x 0 상자 · 1 타원체, y 0 Linear · 1 Exponential, z Anisotropy, w Ambient
    float4 gLFNoise[16];            // x 1 / Tiling, y Strength, z 거리 옅어짐
    float4 gLFScroll[16];           // xyz 흐름 (m/s)
    float4 gLFLightPos[8];          // xyz, w 범위
    float4 gLFLightColor[8];        // rgb (선형), w 1 = 스포트광
    float4 gLFLightDir[8];          // xyz 방향, w cos(바깥)
    float4 gLFLightCone[8];         // x cos(안)
    float4 gLFInfo;                 // x 볼륨 수, y 시간 (초), z 빛 수, w 걸음 수
};

float LFHash(float3 p)
{
    p = frac(p * 0.3183099f + float3(0.71f, 0.113f, 0.419f));
    p *= 17.0f;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float LFValueNoise(float3 x)
{
    const float3 i = floor(x);
    float3 f = frac(x);
    f = f * f * (3.0f - 2.0f * f);
    const float n000 = LFHash(i), n100 = LFHash(i + float3(1, 0, 0)), n010 = LFHash(i + float3(0, 1, 0)), n110 = LFHash(i + float3(1, 1, 0));
    const float n001 = LFHash(i + float3(0, 0, 1)), n101 = LFHash(i + float3(1, 0, 1)), n011 = LFHash(i + float3(0, 1, 1)), n111 = LFHash(i + float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y), lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
}

// 0 ~ 1 덩어리 (두 옥타브)
float LFNoise(float3 x)
{
    return LFValueNoise(x) * 0.65f + LFValueNoise(x * 2.03f + 17.1f) * 0.35f;
}

float3 LFLightAt(float3 p, float3 V, float g)
{
    float3 sum = float3(0.0f, 0.0f, 0.0f);
    const int n = (int)gLFInfo.z;
    [loop]
    for (int k = 0; k < n; ++k)
    {
        const float3 toL = gLFLightPos[k].xyz - p;
        const float d = length(toL);
        const float3 L = toL / max(d, 1e-4f);
        float r = saturate(1.0f - pow(d / max(gLFLightPos[k].w, 1e-4f), 4.0f));
        float att = r * r / (d * d + 1.0f);
        if (gLFLightColor[k].w > 0.5f)
            att *= smoothstep(gLFLightDir[k].w, gLFLightCone[k].x, dot(-L, gLFLightDir[k].xyz));
        sum += gLFLightColor[k].rgb * (att * AtmPhaseHG(dot(V, L), g));
    }
    return sum;
}

// 카메라 → (V, dist) 길에 지역 안개를 입힌다 (선형 색)
float3 ApplyLocalFog(float3 c, float3 V, float dist, float2 pix)
{
    const int count = (int)gLFInfo.x;
    const int steps = max((int)gLFInfo.w, 1);
    const float jitter = AtmNoise(pix + 37.0f, gAtmVol.w);
    const float3 eye = gAtmEye.xyz;
    const float3 sky = AtmSkyColor(float3(0.0f, 1.0f, 0.0f)) * 0.6f + AtmSkyColor(float3(1.0f, 0.05f, 0.0f)) * 0.4f;   // 둘레 하늘 (등방 산란)
    [loop]
    for (int v = 0; v < count; ++v)
    {
        const float3 ro = mul(float4(eye, 1.0f), gLFWorldToLocal[v]).xyz;
        const float3 rd = mul(float4(V, 0.0f), gLFWorldToLocal[v]).xyz;   // 단위 공간에서도 t 는 월드 거리
        float tn = 0.0f, tf = -1.0f;
        if (gLFShape[v].x < 0.5f)
        {
            const float3 inv = 1.0f / (abs(rd) > 1e-7f ? rd : (rd >= 0.0f ? 1e-7f : -1e-7f));
            const float3 t0 = (-1.0f - ro) * inv, t1 = (1.0f - ro) * inv;
            const float3 lo = min(t0, t1), hi = max(t0, t1);
            tn = max(max(lo.x, lo.y), lo.z);
            tf = min(min(hi.x, hi.y), hi.z);
        }
        else
        {
            const float a = dot(rd, rd), b = dot(ro, rd), cc = dot(ro, ro) - 1.0f;
            const float disc = b * b - a * cc;
            if (disc > 0.0f && a > 1e-12f)
            {
                const float s = sqrt(disc);
                tn = (-b - s) / a;
                tf = (-b + s) / a;
            }
        }
        tn = max(tn, 0.0f);
        tf = min(tf, dist);
        if (tf <= tn)
            continue;
        const float ds = (tf - tn) / steps;
        const float3 ext3 = gLFHalf[v].xyz;
        const float blend = gLFHalf[v].w;
        const float g = gLFShape[v].z;
        const float sunPhase = AtmPhaseHG(dot(V, gAtmSunDir.xyz), g);
        float transmittance = 1.0f;
        float3 scatter = float3(0.0f, 0.0f, 0.0f);
        [loop]
        for (int i = 0; i < steps; ++i)
        {
            const float t = tn + (i + jitter) * ds;
            const float3 p = eye + V * t;
            const float3 pl = ro + rd * t;
            // 가장자리까지 (m) → Blend Distance 안쪽에서 0 → 1
            float edge = gLFShape[v].x < 0.5f ? min(min((1.0f - abs(pl.x)) * ext3.x, (1.0f - abs(pl.y)) * ext3.y), (1.0f - abs(pl.z)) * ext3.z)
                                              : (1.0f - length(pl)) * min(min(ext3.x, ext3.y), ext3.z);
            float fade = blend > 1e-4f ? saturate(edge / blend) : (edge > 0.0f ? 1.0f : 0.0f);
            if (gLFShape[v].y > 0.5f)
                fade = (1.0f - exp(-4.0f * fade)) / (1.0f - exp(-4.0f));
            float density = gLFAlbedo[v].w * fade * gLFNoise[v].z;
            if (gLFNoise[v].y > 0.0f)
            {
                const float n = LFNoise((p - gLFScroll[v].xyz * gLFInfo.y) * gLFNoise[v].x);
                density *= lerp(1.0f, saturate(n * 2.2f - 0.6f), gLFNoise[v].y);
            }
            if (density <= 0.0f)
                continue;
            const float3 light = gAtmSunColor.rgb * (AtmSunVisible(p) * sunPhase * 0.25f) + LFLightAt(p, V, g) * 0.25f + sky * gLFShape[v].w;
            const float stepT = exp(-density * ds);
            // 걸음 안에서 해석적 적분: ∫ σ e^{-σx} dx = 1 - e^{-σ ds}
            scatter += transmittance * (1.0f - stepT) * light;
            transmittance *= stepT;
        }
        c = c * transmittance + scatter * gLFAlbedo[v].rgb;
    }
    return c;
}

struct AtmVSOut
{
    float4 PosH : SV_POSITION;
};

AtmVSOut AtmVS(uint vid : SV_VertexID)
{
    AtmVSOut o;
    const float2 uv = float2((vid << 1) & 2, vid & 2);
    o.PosH = float4(uv * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    return o;
}

float4 AtmPS(AtmVSOut pin) : SV_Target
{
    const int2 pix = int2(pin.PosH.xy);
    const float4 src = gAtmSceneColor.Load(int3(pix, 0));
    const float z = gAtmDepth.Load(int3(pix, 0));
    // 하늘 = 지운 깊이 (정확히 1) 만. 0.99999 는 near 0.1 이면 10 km 너머의 실제 지형도 하늘로 보아 안개 · 대기가 빠졌다 (50 km 월드의 먼 산)
    const bool sky = z >= 1.0f;
    if (sky && src.a < 0.5f)
        return src;   // Scene 뷰의 빈 배경 (하늘 끔): 뒤의 그라디언트가 비치게 그대로
    const float2 ndc = float2((pin.PosH.x - gAtmViewport.x) / gAtmViewport.z * 2.0f - 1.0f, 1.0f - (pin.PosH.y - gAtmViewport.y) / gAtmViewport.w * 2.0f);
    float4 wp = mul(float4(ndc, sky ? 0.5f : z, 1.0f), gAtmInvViewProj);
    float3 posW = wp.xyz / wp.w;
    if (sky)
        posW = gAtmEye.xyz + normalize(posW - gAtmEye.xyz) * gAtmFog2.x;
    float3 c = AtmosphereApply(AtmToLinear(src.rgb), posW, sky);
    if (gAtmVolAlbedo.w > 0.5f || gLFInfo.x > 0.5f)
    {
        const float3 d = posW - gAtmEye.xyz;
        const float dist = length(d);
        if (dist > 1e-3f)
        {
            if (gAtmVolAlbedo.w > 0.5f)
                c += AtmVolumetric(d / dist, dist, pin.PosH.xy);
            if (gLFInfo.x > 0.5f)
                c = ApplyLocalFog(c, d / dist, sky ? 1e6f : dist, pin.PosH.xy);
        }
    }
    return float4(AtmToGamma(c), src.a);
}

DepthStencilState AtmNoDepth
{
    DepthEnable = FALSE;
    DepthWriteMask = ZERO;
};

RasterizerState AtmNoCull
{
    CullMode = None;
};

BlendState AtmNoBlend
{
    BlendEnable[0] = FALSE;
};

technique11 AtmosphereTech
{
    pass P0
    {
        SetVertexShader(CompileShader(vs_5_0, AtmVS()));
        SetGeometryShader(NULL);
        SetPixelShader(CompileShader(ps_5_0, AtmPS()));
        SetDepthStencilState(AtmNoDepth, 0);
        SetRasterizerState(AtmNoCull);
        SetBlendState(AtmNoBlend, float4(0, 0, 0, 0), 0xFFFFFFFF);
    }
}
