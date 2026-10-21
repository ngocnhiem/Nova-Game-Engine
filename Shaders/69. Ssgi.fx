//=============================================================================
// 69. Ssgi.fx
// HDRP 의 Screen Space Global Illumination: 굽지 않는 한 번 튕긴 간접광 (큰 월드 — 미리 계산 없음).
//  1) Trace     : 계산 크기 (반 · 전체) 에서 픽셀마다 코사인 분포 광선 (Low 1 · Medium 2 · High 4 개) 을 뷰 공간에서 걷는다 —
//                 깊이 프리패스 (뷰 노멀 · 깊이) 보다 뒤로 Depth Tolerance 안쪽이면 맞음 → 그 자리를 지난 프레임 장면 색 (SSR 히스토리, 감마) 에서
//                 (지난 ViewProj 로 되돌려 찾는다 — 카메라가 움직여도 맞다). 맞은 광선 색의 평균 = 확산 조도 / π (Lambert 에 그대로 곱한다)
//  2) Temporal  : 지난 결과와 섞기 (뷰 깊이가 다르면 버림)
//  3) Blur      : 깊이 · 노멀을 지키는 흐림 (가로 · 세로)
//  4) Composite : 전체 해상도로 늘려 SSAO 결과 텍스처의 gba 에 (r = AO 그대로) — 32 의 ShadeLit 이 확산 환경광에 더한다
// 노멀 · 깊이는 SSAO 와 같은 텍스처 (xyz 뷰 노멀, w 뷰 깊이, 빈 곳 1e5).
//=============================================================================

cbuffer cbSsgi
{
    float4 gProjParams;          // P11, P22, P31, P32 (지터 포함 — 깊이 프리패스와 같은 투영)
    float4x4 gInvView;           // 뷰 → 월드
    float4x4 gPrevViewProj;      // 월드 → 지난 프레임 클립 (장면 색 히스토리를 그린 카메라)
    float4x4 gCurViewToPrevClip; // 이번 뷰 → 지난 프레임 클립 (이 GI 의 히스토리)
    float4 gTrace;               // x 걸음 수, y 최대 길이 (m), z Depth Tolerance (m), w 광선 수
    float4 gSize;                // xy 전체 크기 (px), z 전체 / 계산 (1 · 2), w 프레임
    float4 gTemporal;            // x 히스토리 비중, y 히스토리 있음, z 장면 색 밉 수, w 흐림 반지름
    float4 gBlurDir;             // xy 방향 (계산 텍셀)
    float4 gComposite;           // x Multiplier
};

Texture2D gNormalDepth;     // 전체 해상도
Texture2D gColorHistory;    // 지난 프레임 장면 색 (감마, 밉)
Texture2D gInput;           // Temporal · Blur · Composite 입력 (계산 크기, rgb GI, a 뷰 깊이)
Texture2D gGiHistory;       // 지난 GI (계산 크기)
Texture2D gAoCopy;          // Composite: SSAO 결과 사본 (r = AO)

SamplerState samLinearClamp
{
    Filter = MIN_MAG_MIP_LINEAR;
    AddressU = CLAMP;
    AddressV = CLAMP;
};

struct VertexOut
{
    float4 PosH : SV_POSITION;
    float2 Tex : TEXCOORD;
};

VertexOut VS(uint id : SV_VertexID)
{
    VertexOut vout;
    vout.Tex = float2((id << 1) & 2, id & 2);
    vout.PosH = float4(vout.Tex * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    return vout;
}

float4 NormalDepthAt(int2 p)
{
    p = clamp(p, int2(0, 0), int2(gSize.xy) - 1);
    return gNormalDepth.Load(int3(p, 0));
}

// 전체 해상도 픽셀 (가운데) · 뷰 깊이 → 뷰 공간 위치
float3 ViewPos(float2 pix, float z)
{
    const float2 ndc = float2((pix.x + 0.5f) / gSize.x * 2.0f - 1.0f, 1.0f - (pix.y + 0.5f) / gSize.y * 2.0f);
    return float3((ndc.x - gProjParams.z) * z / gProjParams.x, (ndc.y - gProjParams.w) * z / gProjParams.y, z);
}

// 뷰 공간 점 → 화면 uv (0 ~ 1)
float2 ViewToUv(float3 v)
{
    const float2 ndc = float2(v.x * gProjParams.x / v.z + gProjParams.z, v.y * gProjParams.y / v.z + gProjParams.w);
    return float2(ndc.x * 0.5f + 0.5f, 0.5f - ndc.y * 0.5f);
}

float Ign(float2 p, float frame)
{
    p += 5.588238f * frame;
    return frac(52.9829189f * frac(dot(p, float2(0.06711056f, 0.00583715f))));
}

float Hash21(float2 p)
{
    p = frac(p * float2(0.1031f, 0.11369f));
    p += dot(p, p.yx + 19.19f);
    return frac((p.x + p.y) * p.x);
}

// ---------------------------------------------------------------- 1) Trace
float4 PS_Trace(VertexOut pin) : SV_Target
{
    const float2 cp = floor(pin.PosH.xy);
    const float2 pix = cp * gSize.z + (gSize.z - 1.0f) * 0.5f;   // 계산 텍셀이 덮는 전체 픽셀의 가운데
    const float4 nd = NormalDepthAt(int2(pix));
    if (nd.w > 1e4f)
        return float4(0.0f, 0.0f, 0.0f, nd.w);
    const float3 N = normalize(nd.xyz);
    const float3 P = ViewPos(pix, nd.w);
    const float3 T = normalize(abs(N.y) < 0.95f ? cross(N, float3(0, 1, 0)) : cross(N, float3(1, 0, 0)));
    const float3 B = cross(N, T);
    const int steps = (int)gTrace.x;
    const int rays = (int)gTrace.w;
    const float maxLen = gTrace.y;
    const float tolerance = gTrace.z;
    const float mipLevel = min(1.5f, gTemporal.z - 1.0f);
    float3 sum = float3(0.0f, 0.0f, 0.0f);
    [loop]
    for (int r = 0; r < rays; ++r)
    {
        // 코사인 분포 방향 (픽셀 · 프레임 · 광선마다 다르게 — 시간 누적이 고르게 만든다)
        const float u1 = Ign(cp + float2(r * 17.0f, r * 5.0f), gSize.w);
        const float u2 = Hash21(cp * 1.37f + float2(gSize.w * 3.17f, r * 7.11f));
        const float phi = 6.2831853f * u2;
        const float sr = sqrt(u1);
        const float3 dir = normalize(T * (cos(phi) * sr) + B * (sin(phi) * sr) + N * sqrt(max(1.0f - u1, 0.0f)));
        const float3 origin = P + N * (0.02f + 0.002f * P.z);
        const float jitter = Hash21(cp + float2(r * 3.0f, gSize.w * 1.7f));
        [loop]
        for (int i = 0; i < steps; ++i)
        {
            const float f = (i + jitter) / steps;
            const float t = maxLen * f * f + 0.05f;   // 가까이 촘촘히
            const float3 S = origin + dir * t;
            if (S.z <= 0.05f)
                break;
            const float2 uv = ViewToUv(S);
            if (any(uv < 0.0f) || any(uv > 1.0f))
                break;
            const float4 hnd = NormalDepthAt(int2(uv * gSize.xy));
            if (hnd.w > 1e4f)
                continue;   // 하늘: 지나간다 (하늘빛은 환경광이 이미 준다)
            const float behind = S.z - hnd.w;
            if (behind > 0.01f && behind < tolerance * (1.0f + t * 0.05f))
            {
                // 뒷면 (광선과 같은 쪽을 보는 면) 은 빛을 주지 않는다 — 벽 너머로 새지 않게
                if (dot(normalize(hnd.xyz), dir) < 0.2f)
                {
                    const float3 hv = ViewPos(uv * gSize.xy - 0.5f, hnd.w);
                    const float4 world = mul(float4(hv, 1.0f), gInvView);
                    const float4 prev = mul(world, gPrevViewProj);
                    if (prev.w > 1e-4f)
                    {
                        const float2 puv = float2(prev.x / prev.w * 0.5f + 0.5f, 0.5f - prev.y / prev.w * 0.5f);
                        if (all(puv >= 0.0f) && all(puv <= 1.0f))
                        {
                            const float3 c = gColorHistory.SampleLevel(samLinearClamp, puv, mipLevel).rgb;
                            sum += min(pow(max(c, 0.0f), 2.2f), 8.0f);   // 감마 → 선형, 아주 밝은 점 (해 반사) 은 잘라 깜빡임 줄이기
                        }
                    }
                }
                break;
            }
        }
    }
    return float4(sum / max(rays, 1), nd.w);
}

// ---------------------------------------------------------------- 2) Temporal
float4 PS_Temporal(VertexOut pin) : SV_Target
{
    const int2 cp = int2(pin.PosH.xy);
    const float4 cur = gInput.Load(int3(cp, 0));
    if (cur.a > 1e4f || gTemporal.y < 0.5f)
        return cur;
    const float2 pix = float2(cp) * gSize.z + (gSize.z - 1.0f) * 0.5f;
    const float3 P = ViewPos(pix, cur.a);
    const float4 prev = mul(float4(P, 1.0f), gCurViewToPrevClip);
    if (prev.w <= 1e-4f)
        return cur;
    const float2 puv = float2(prev.x / prev.w * 0.5f + 0.5f, 0.5f - prev.y / prev.w * 0.5f);
    if (any(puv < 0.0f) || any(puv > 1.0f))
        return cur;
    const float4 hist = gGiHistory.SampleLevel(samLinearClamp, puv, 0.0f);
    // 같은 면인가: 지난 프레임에서 이 점의 뷰 깊이 (prev.w) 와 히스토리가 기억한 깊이
    const float dz = abs(hist.a - prev.w) / max(prev.w, 1e-3f);
    const float w = gTemporal.x * saturate(1.0f - dz * 12.0f);
    return float4(lerp(cur.rgb, hist.rgb, w), cur.a);
}

// ---------------------------------------------------------------- 3) Blur
float4 PS_Blur(VertexOut pin) : SV_Target
{
    const int2 cp = int2(pin.PosH.xy);
    const float4 c = gInput.Load(int3(cp, 0));
    if (c.a > 1e4f)
        return c;
    const float2 pix = float2(cp) * gSize.z + (gSize.z - 1.0f) * 0.5f;
    const float3 n0 = normalize(NormalDepthAt(int2(pix)).xyz);
    static const float kW[4] = { 0.30f, 0.23f, 0.11f, 0.04f };
    float3 sum = c.rgb * kW[0];
    float wsum = kW[0];
    const int2 lim = int2(gSize.xy / gSize.z) - 1;
    [unroll]
    for (int k = 1; k <= 3; ++k)
    {
        [unroll]
        for (int s = -1; s <= 1; s += 2)
        {
            const int2 q = clamp(cp + int2(gBlurDir.xy * (k * s) * gTemporal.w), int2(0, 0), lim);
            const float4 v = gInput.Load(int3(q, 0));
            const float3 nq = normalize(NormalDepthAt(int2(float2(q) * gSize.z + (gSize.z - 1.0f) * 0.5f)).xyz);
            const float w = kW[k] * saturate(1.0f - abs(v.a - c.a) / max(c.a, 1e-3f) * 30.0f) * pow(saturate(dot(n0, nq)), 8.0f);
            sum += v.rgb * w;
            wsum += w;
        }
    }
    return float4(sum / wsum, c.a);
}

// ---------------------------------------------------------------- 4) Composite (전체 해상도)
float4 PS_Composite(VertexOut pin) : SV_Target
{
    const int2 p = int2(pin.PosH.xy);
    const float ao = gAoCopy.Load(int3(p, 0)).r;
    const float4 nd = NormalDepthAt(p);
    if (nd.w > 1e4f)
        return float4(ao, 0.0f, 0.0f, 0.0f);
    // 깊이 · 노멀이 비슷한 계산 텍셀만 섞기 (SSAO 의 Upsample 과 같다)
    const float2 f = pin.PosH.xy / gSize.z - 0.5f;
    const float2 base = floor(f);
    const float2 t = f - base;
    const int2 lim = int2(gSize.xy / gSize.z) - 1;
    float3 sum = float3(0.0f, 0.0f, 0.0f);
    float wsum = 0.0f;
    float3 best = float3(0.0f, 0.0f, 0.0f);
    float bestDz = 1e9f;
    [unroll]
    for (int j = 0; j <= 1; ++j)
    {
        [unroll]
        for (int i = 0; i <= 1; ++i)
        {
            const int2 q = clamp(int2(base) + int2(i, j), int2(0, 0), lim);
            const float4 g = gInput.Load(int3(q, 0));
            const float bw = (i ? t.x : 1.0f - t.x) * (j ? t.y : 1.0f - t.y);
            const float dz = abs(g.a - nd.w) / max(nd.w, 1e-3f);
            const float w = bw * saturate(1.0f - dz * 20.0f);
            sum += g.rgb * w;
            wsum += w;
            if (dz < bestDz) { bestDz = dz; best = g.rgb; }
        }
    }
    const float3 gi = wsum > 1e-4f ? sum / wsum : best;
    return float4(ao, gi * gComposite.x);
}

#define SSGI_TECH(NAME, PSFUNC) \
technique11 NAME \
{ \
    pass P0 \
    { \
        SetVertexShader(CompileShader(vs_5_0, VS())); \
        SetGeometryShader(NULL); \
        SetPixelShader(CompileShader(ps_5_0, PSFUNC())); \
    } \
}

SSGI_TECH(TraceTech, PS_Trace)
SSGI_TECH(TemporalTech, PS_Temporal)
SSGI_TECH(BlurTech, PS_Blur)
SSGI_TECH(CompositeTech, PS_Composite)
