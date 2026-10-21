#pragma once
#include <nlohmann/json.hpp>

class VolumeStack;
class Ssao;

// HDRP 의 Screen Space Global Illumination (Volume 효과): 굽지 않는 한 번 튕긴 간접광 — 해를 받은 바닥 · 벽의 빛이 둘레를 물들인다.
//  깊이 프리패스 (SSAO 의 뷰 노멀 · 깊이) 를 걷는 광선이 맞은 곳의 지난 프레임 장면 색 (ScreenSpaceReflection 의 히스토리) 을 모아
//  시간 누적 · 흐림 뒤 SSAO 결과 텍스처의 gba 에 넣는다 (r = AO). 32 의 ShadeLit 이 확산 환경광에 더한다 (gSsaoParams.y = Multiplier)
//  셰이더 69. Ssgi.fx, Game · Scene 뷰마다 따로
namespace Ssgi
{
	struct Settings
	{
		bool Enabled = false;
		int Quality = 1;              // Low (광선 1 · 16 걸음) · Medium (2 · 24) · High (4 · 32)
		bool FullResolution = false;  // 기본 = 반 해상도
		float MaxRayLength = 12.0f;   // m
		float Thickness = 0.35f;      // Depth Tolerance (m)
		bool Denoise = true;
		float DenoiserRadius = 1.0f;
		float Intensity = 1.0f;       // Multiplier
		bool Active() const { return Enabled && Intensity > 0.0f; }
		static Settings FromStack(const VolumeStack& stack);
	};

	// SSAO 다음 · 본 패스 전. view · proj = 깊이 프리패스와 같은 (TAA 지터 포함). 돌려준 값 = 셰이더의 GI 세기 (0 = 안 씀)
	float Render(bool editor, const Settings& s, Ssao* ssao, CXMMATRIX view, CXMMATRIX proj);
	// 마지막 프레임 (nova ssgi info)
	nlohmann::json Info(bool editor);
	void RegisterEditor();   // CLI nova ssgi info
}
