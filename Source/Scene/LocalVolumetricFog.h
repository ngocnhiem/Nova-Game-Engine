#pragma once
#include "Component.h"

// Unity HDRP 의 Local Volumetric Fog (Godot 의 FogVolume): 상자 · 타원체 안에만 있는 안개 — 늪 · 동굴 · 성 안 · 골짜기 안개.
//  - Fog Distance (평균 자유 경로, m): 이 거리를 지나면 빛의 63 % 가 가려진다 (작을수록 짙다)
//  - Single Scattering Albedo: 안개 색 (빛을 얼마나 흩뜨리나)
//  - Blend Distance: 가장자리 안쪽 이 폭에서 0 → 1 (Falloff: Linear · Exponential)
//  - Distance Fade Start · End: 카메라에서 멀어지면 옅어진다
//  - Mask = Noise: 3D 노이즈로 덩어리지게 (Noise Tiling m · Strength), Scroll Speed 로 흐른다 (HDRP 의 Density Mask Texture 자리)
//  - 해 (방향광 0, 캐스케이드 그림자) · 가까운 점광 · 스포트광 8 개 · 하늘 환경광 (Ambient Multiplier) 이 비춘다
//  - 크기 = Size × Transform 크기 · 회전 (HDRP 처럼 상자는 회전한다). 그리기는 AtmospherePass (화면 전체, 볼륨 구간만 걷기)
class NOVA_API LocalVolumetricFog : public Component
{
public:
	enum class Shape { Box = 0, Ellipsoid = 1 };
	enum class Falloff { Linear = 0, Exponential = 1 };
	enum class Mask { None = 0, Noise = 1 };

	LocalVolumetricFog();
	~LocalVolumetricFog() override;
	static const std::vector<LocalVolumetricFog*>& All();
	bool IsActiveInHierarchy() const;

	const float* GetAlbedo() const { return m_Albedo; }
	float GetFogDistance() const { return m_FogDistance; }
	const Vec3& GetSize() const { return m_Size; }
	Shape GetShape() const { return m_Shape; }
	float GetBlendDistance() const { return m_BlendDistance; }
	Falloff GetFalloff() const { return m_Falloff; }
	float GetDistanceFadeStart() const { return m_FadeStart; }
	float GetDistanceFadeEnd() const { return m_FadeEnd; }
	Mask GetMask() const { return m_Mask; }
	float GetNoiseTiling() const { return m_NoiseTiling; }
	float GetNoiseStrength() const { return m_NoiseStrength; }
	const Vec3& GetScrollSpeed() const { return m_ScrollSpeed; }
	float GetAnisotropy() const { return m_Anisotropy; }
	float GetAmbientMultiplier() const { return m_Ambient; }
	// 월드 → 단위 공간 (상자 [-1,1]³, 타원체 = 반지름 1 구) 과 월드 반 크기 (m)
	void Placement(XMMATRIX& worldToLocal, Vec3& halfExtents, Vec3& center);

	void OnInspectorGUI() override;
	bool UsesUnityInspector() const override { return true; }
	const char* InspectorIconName() const override { return "volume_profile"; }
	void OnDrawGizmos() override;

private:
	float m_Albedo[3] = { 1.0f, 1.0f, 1.0f };
	float m_FogDistance = 5.0f;
	Vec3 m_Size = Vec3(10, 10, 10);
	Shape m_Shape = Shape::Box;
	float m_BlendDistance = 1.0f;
	Falloff m_Falloff = Falloff::Linear;
	float m_FadeStart = 1000.0f, m_FadeEnd = 1200.0f;
	Mask m_Mask = Mask::None;
	float m_NoiseTiling = 4.0f;          // 노이즈 한 덩어리 크기 (m)
	float m_NoiseStrength = 0.7f;        // 0 = 고른 안개, 1 = 빈 곳이 생긴다
	Vec3 m_ScrollSpeed = Vec3(0.3f, 0.0f, 0.15f);   // m/s
	float m_Anisotropy = 0.3f;           // Henyey-Greenstein (빛 쪽을 볼 때 밝게)
	float m_Ambient = 1.0f;              // 하늘 환경광 몫 (동굴 = 0 가까이)

	GENERATE_COMPONENT_BODY(LocalVolumetricFog)
};
REGISTER_COMPONENT(LocalVolumetricFog)
