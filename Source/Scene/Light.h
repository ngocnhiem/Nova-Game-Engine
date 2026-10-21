#pragma once
#include "Component.h"
#include "LightHelper.h"

// 면광 (Rectangle · Disc — HDRP 식 실시간, LTC): 빛은 Transform 의 forward 쪽 반구로만 나간다. 그림자 없음
struct AreaLightDesc
{
	XMFLOAT4 Diffuse = XMFLOAT4(1.0f, 1.0f, 1.0f, 1.0f);
	float Range = 10.0f;                         // 이 거리 (빛 면의 가장 가까운 점에서) 에서 0 으로
	XMFLOAT2 Size = XMFLOAT2(1.0f, 1.0f);        // Rectangle: 가로 · 세로 (m), Disc: x = 반지름
	// ViewUpdate 가 Transform 에서 채운다
	XMFLOAT3 Position = XMFLOAT3(0, 0, 0);
	XMFLOAT3 Right = XMFLOAT3(1, 0, 0), Up = XMFLOAT3(0, 1, 0), Forward = XMFLOAT3(0, 0, 1);
};

class Light
	: public Component
{
private:
	LightType m_LightType = LightType::Directional;
	AreaLightDesc m_AreaDesc;

	// ������ ������ �ִ� ����ü ������ Transform�̶� �����ؾ��ұ�?
	DirectionalLight m_DirectionalDesc;
	PointLight m_PointDesc;
	SpotLight m_SpotDesc;

	// Unity(URP) Light Inspector 항목. Color/Intensity/Type/Range 는 실제로 적용되고, 나머지는 값 저장용이다.
	float m_Intensity = 1.0f;
	float m_IndirectMultiplier = 1.0f;
	int   m_Mode = 0;            // Realtime / Mixed / Baked
	int   m_ColorMode = 0;       // 0 Color, 1 Filter and Temperature
	float m_Temperature = 6570.0f;
	float m_Filter[4] = { 1.0f, 1.0f, 1.0f, 1.0f };
	uint32 m_CullingMask = 0xFFFFFFFFu;   // Unity Light.cullingMask (layer bits)
	int   m_ShadowType = 0;      // No Shadows / Hard / Soft
	// Unity URP Light > Shadows
	float m_ShadowStrength = 1.0f;
	int   m_ShadowBiasMode = 0;       // 0 = Use settings from Render Pipeline (Volume > Shadows), 1 = Custom
	float m_ShadowDepthBias = 1.0f;   // 텍셀 단위 (Custom)
	float m_ShadowNormalBias = 1.0f;
	float m_ShadowNearPlane = 0.2f;   // 스포트/점광 그림자 투영의 가까운 면

	vector<XMMATRIX> m_LightView;
	XMMATRIX m_LightProj;

	vector<XMMATRIX> m_EditorLightView;
	XMMATRIX m_EditorLightProj;

	// shadowTransform => world * (lightV * lightP * toTexSpace)

	void ProjUpdate();
	void ApplyIntensity(XMFLOAT4& c) const { c.x *= m_Intensity; c.y *= m_Intensity; c.z *= m_Intensity; }
	XMFLOAT4* CurrentDiffuse();
	void EditorProjUpdate();
	string GetStringLightType(LightType type);

public:
	Light();
	~Light();

	InstanceID GetInstanceID()
	{
		return make_tuple((uint64)&m_DirectionalDesc, (uint64)&m_PointDesc, (uint64)&m_SpotDesc);
	}

	virtual void Awake() override;
	virtual void Update() override;
	virtual void LateUpdate() override;
	virtual void FixedUpdate() override;
	virtual void LastUpdate() override;
	virtual void Render() override;

	virtual void OnDestroy() override;

	virtual void OnInspectorGUI() override;
	virtual bool UsesUnityInspector() const override { return true; }
	virtual const char* InspectorIconName() const override;
	virtual void OnDrawGizmos() override;

	void ViewUpdate();
	void EditorViewUpdate();

	void SetDirLight(DirectionalLight light);
	void SetPointLight(PointLight light);
	void SetSpotLight(SpotLight light);

	// 렌더링에 넘기는 값은 Intensity 가 곱해진 복사본
	DirectionalLight GetDirLight() { DirectionalLight d = m_DirectionalDesc; ApplyIntensity(d.Diffuse); ApplyIntensity(d.Specular); return d; }
	PointLight GetPointLight() { PointLight d = m_PointDesc; ApplyIntensity(d.Diffuse); ApplyIntensity(d.Specular); return d; }
	SpotLight GetSpotLight() { SpotLight d = m_SpotDesc; ApplyIntensity(d.Diffuse); ApplyIntensity(d.Specular); return d; }
	AreaLightDesc GetAreaLight() { AreaLightDesc d = m_AreaDesc; ApplyIntensity(d.Diffuse); return d; }
	bool IsAreaLight() const { return m_LightType == LightType::Rectangle || m_LightType == LightType::Disc; }
	// Unity Light.areaSize (Rectangle: 가로 · 세로, Disc: x = 반지름)
	XMFLOAT2 GetAreaSize() const { return m_AreaDesc.Size; }
	void SetAreaSize(const XMFLOAT2& s) { m_AreaDesc.Size = XMFLOAT2((std::max)(s.x, 0.01f), (std::max)(s.y, 0.01f)); }
	void SetAreaLight(LightType type, const AreaLightDesc& d) { m_LightType = type; m_AreaDesc = d; }

	float GetRange();
	// C# Light.intensity · color · shadowStrength (스크립트 · 트윈)
	float GetIntensity() const { return m_Intensity; }
	void SetIntensity(float v) { m_Intensity = v < 0.0f ? 0.0f : v; }
	XMFLOAT4 GetColor() { return *CurrentDiffuse(); }
	void SetColor(const XMFLOAT4& c) { XMFLOAT4* d = CurrentDiffuse(); d->x = c.x; d->y = c.y; d->z = c.z; }
	void SetShadowStrength(float v) { m_ShadowStrength = v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v); }

	vector<XMMATRIX> GetEditorLightViewArray() { return m_EditorLightView; }
	XMMATRIX GetEditorLightView() { return m_EditorLightView[0]; }
	XMMATRIX GetEditorLightProjection() { return m_EditorLightProj; }
	XMMATRIX GetEditorLightViewProjection(int index) { return m_EditorLightView[index] * m_EditorLightProj; }

	vector<XMMATRIX> GetLightViewArray() { return m_LightView; }
	XMMATRIX GetLightView() { return m_LightView[0]; }
	XMMATRIX GetLightProjection() { return m_LightProj; }
	XMMATRIX GetLightViewProjection(int index) { return m_LightView[index] * m_LightProj; }

	// LightV * LightP * toTexSpace
	LightType GetLightType() { return m_LightType; }
	uint32 GetCullingMaskBits() const { return m_CullingMask; }
	void SetCullingMaskBits(uint32 m) { m_CullingMask = m; }

	// Shadow Type 이 No Shadows 가 아니면 그림자를 드리운다
	bool CastsShadows() const { return m_ShadowType != 0; }
	bool SoftShadows() const { return m_ShadowType == 2; }
	void SetShadowType(int type) { m_ShadowType = type; }
	float GetShadowStrength() const { return m_ShadowStrength; }
	bool UsesCustomShadowBias() const { return m_ShadowBiasMode == 1; }
	float GetShadowDepthBias() const { return m_ShadowDepthBias; }
	float GetShadowNormalBias() const { return m_ShadowNormalBias; }
	float GetShadowNearPlane() const { return m_ShadowNearPlane; }
	float GetSpotAngle() const { return m_SpotDesc.Spot; }

	GENERATE_COMPONENT_BODY(Light)
};

REGISTER_COMPONENT(Light)
