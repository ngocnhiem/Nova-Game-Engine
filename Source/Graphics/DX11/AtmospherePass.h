#pragma once

class VolumeStack;
struct DirectionalLight;

// 대기·안개 (Volume 의 Fog / Atmosphere 오버라이드).
//  - FromStack: 카메라 위치에서 섞은 Volume 값 + 해(첫 방향광)로 셰이더 값을 만든다
//  - Draw: 불투명 물체·하늘 다음에 화면 전체 패스 (장면 색을 복사해 읽고, 깊이로 월드 위치를 되살려 선형 공간에서 입힌다)
//  - Bind: 다른 효과(물)가 같은 cbAtmosphere 를 쓰도록 값을 넣는다 (49. AtmosphereCommon.fx)
namespace AtmospherePass
{
	struct Params
	{
		bool Fog = false, Aerial = false;
		XMFLOAT4 FogA = {}, FogB = {}, FogColor = {}, Rayleigh = {}, Mie = {}, SunDir = { 0, 1, 0, 0 }, SunColor = {};
		// 볼륨 안개 (Fog > Volumetric Fog): x 거리, y Multiplier, z 걸음 수 / albedo rgb · w 켜짐
		XMFLOAT4 Volumetric = {}, VolumetricAlbedo = {};
		// 해 그림자 (그리는 쪽이 넣는다 — ShadowRenderer 의 방향광 0)
		GfxShaderResourceView* SunShadow = nullptr;
		XMMATRIX SunShadowTransforms[8];
		XMFLOAT4 CascadeSpheres[8] = {};
		XMFLOAT4 ShadowInfo = {};   // x 캐스케이드 수, y 세기
		// Local Volumetric Fog (HDRP): 먼 것부터 (GatherLocalFog)
		static constexpr int kMaxLocalFog = 16, kMaxFogLights = 8;
		int LocalFogCount = 0;
		XMMATRIX LocalFogW2L[kMaxLocalFog];   // 월드 → 단위 공간
		XMFLOAT4 LocalFogAlbedo[kMaxLocalFog] = {};   // rgb 알베도 (선형), w 소광 (1 / Fog Distance)
		XMFLOAT4 LocalFogHalf[kMaxLocalFog] = {};     // xyz 월드 반 크기 (m), w Blend Distance
		XMFLOAT4 LocalFogShape[kMaxLocalFog] = {};    // x 0 상자 · 1 타원체, y 0 Linear · 1 Exponential, z Anisotropy, w Ambient
		XMFLOAT4 LocalFogNoise[kMaxLocalFog] = {};    // x 1 / Tiling, y Strength (0 = 노이즈 없음), z 거리 옅어짐 (0 ~ 1)
		XMFLOAT4 LocalFogScroll[kMaxLocalFog] = {};   // xyz m/s
		// 안개를 비추는 가까운 점광 · 스포트광 (면광은 가운데 점광으로)
		int FogLightCount = 0;
		XMFLOAT4 FogLightPos[kMaxFogLights] = {};     // xyz, w 범위
		XMFLOAT4 FogLightColor[kMaxFogLights] = {};   // rgb (선형), w 0 점광 · 1 스포트광
		XMFLOAT4 FogLightDir[kMaxFogLights] = {};     // xyz 방향, w cos(바깥)
		XMFLOAT4 FogLightCone[kMaxFogLights] = {};    // x cos(안)
		float Time = 0.0f;
		bool Active() const { return Fog || Aerial || LocalFogCount > 0; }
	};

	Params FromStack(const VolumeStack& stack, const DirectionalLight* sun);
	// 씬의 Local Volumetric Fog (켜진 것, 카메라에서 가까운 16 개 — 먼 것부터 그린다) 와 비추는 빛. editorView = Scene 뷰 빛 목록
	void GatherLocalFog(Params& p, const XMFLOAT3& eye, bool editorView);
	void Bind(FxEffect* fx, const Params& p, const XMFLOAT3& eye, GfxShaderResourceView* sky);
	// target 에 그린다. depthSRV = 장면 깊이 (DSV 는 잠시 풀고 끝나면 dsv 로 되돌린다)
	void Draw(GfxContext* dc, const Params& p, GfxRenderTargetView* target, GfxDepthStencilView* dsv, GfxShaderResourceView* depthSRV,
		const D3D11_VIEWPORT& viewport, CXMMATRIX viewProj, const XMFLOAT3& eye, GfxShaderResourceView* sky);
}
