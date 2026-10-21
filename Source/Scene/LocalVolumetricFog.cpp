#include "pch.h"
#include "LocalVolumetricFog.h"
#include "UnityGUI.h"
#include "SceneViewOverlay.h"
#include "SelectionManager.h"
#include "Transform.h"

namespace
{
	std::vector<LocalVolumetricFog*>& Registry()
	{
		static std::vector<LocalVolumetricFog*> r;
		return r;
	}
}

LocalVolumetricFog::LocalVolumetricFog()
{
	m_InspectorTitleName = "Local Volumetric Fog";
	Registry().push_back(this);
}

LocalVolumetricFog::~LocalVolumetricFog()
{
	auto& r = Registry();
	r.erase(std::remove(r.begin(), r.end(), this), r.end());
}

const std::vector<LocalVolumetricFog*>& LocalVolumetricFog::All()
{
	return Registry();
}

bool LocalVolumetricFog::IsActiveInHierarchy() const
{
	for (GameObject* g = m_pGameObject; g != nullptr; g = g->GetParent())
		if (!g->IsActive())
			return false;
	return m_pGameObject != nullptr;
}

void LocalVolumetricFog::Placement(XMMATRIX& worldToLocal, Vec3& halfExtents, Vec3& center)
{
	Transform* t = m_pGameObject->GetTransform();
	const Matrix world = t->GetWorldMatrix();
	const Vec3 half((std::max)(m_Size.x, 0.01f) * 0.5f, (std::max)(m_Size.y, 0.01f) * 0.5f, (std::max)(m_Size.z, 0.01f) * 0.5f);
	const XMMATRIX local = XMMatrixScaling(half.x, half.y, half.z) * XMMATRIX(world);
	worldToLocal = XMMatrixInverse(nullptr, local);
	// 월드 반 크기 = 단위 축이 월드에서 가지는 길이 (Transform 크기 포함)
	halfExtents = Vec3(XMVectorGetX(XMVector3Length(local.r[0])), XMVectorGetX(XMVector3Length(local.r[1])), XMVectorGetX(XMVector3Length(local.r[2])));
	XMFLOAT3 c;
	XMStoreFloat3(&c, local.r[3]);
	center = Vec3(c.x, c.y, c.z);
}

void LocalVolumetricFog::OnInspectorGUI()
{
	static const char* kShapes[] = { "Box", "Ellipsoid" };
	static const char* kFalloff[] = { "Linear", "Exponential" };
	static const char* kMask[] = { "None", "Noise" };
	float albedo[4] = { m_Albedo[0], m_Albedo[1], m_Albedo[2], 1.0f };
	if (UnityGUI::Color("Single Scattering Albedo", albedo))
		for (int i = 0; i < 3; ++i) m_Albedo[i] = albedo[i];
	if (UnityGUI::Float("Fog Distance", &m_FogDistance))
		m_FogDistance = (std::max)(m_FogDistance, 0.05f);
	int shape = (int)m_Shape;
	if (UnityGUI::Dropdown("Shape", &shape, kShapes, 2))
		m_Shape = (Shape)shape;
	float size[3] = { m_Size.x, m_Size.y, m_Size.z };
	if (UnityGUI::Vector3("Size", size))
		m_Size = Vec3((std::max)(size[0], 0.01f), (std::max)(size[1], 0.01f), (std::max)(size[2], 0.01f));
	if (UnityGUI::Float("Blend Distance", &m_BlendDistance))
		m_BlendDistance = (std::max)(m_BlendDistance, 0.0f);
	int falloff = (int)m_Falloff;
	if (UnityGUI::Dropdown("Falloff Mode", &falloff, kFalloff, 2))
		m_Falloff = (Falloff)falloff;
	if (UnityGUI::Float("Distance Fade Start", &m_FadeStart))
		m_FadeStart = (std::max)(m_FadeStart, 0.0f);
	if (UnityGUI::Float("Distance Fade End", &m_FadeEnd))
		m_FadeEnd = (std::max)(m_FadeEnd, m_FadeStart + 0.01f);
	if (UnityGUI::Foldout("Density Mask"))
	{
		int mask = (int)m_Mask;
		if (UnityGUI::Dropdown("Mask Mode", &mask, kMask, 2, 1))
			m_Mask = (Mask)mask;
		if (m_Mask == Mask::Noise)
		{
			if (UnityGUI::Float("Noise Tiling", &m_NoiseTiling, 1))
				m_NoiseTiling = (std::max)(m_NoiseTiling, 0.05f);
			UnityGUI::Slider("Noise Strength", &m_NoiseStrength, 0.0f, 1.0f, 1);
			float scroll[3] = { m_ScrollSpeed.x, m_ScrollSpeed.y, m_ScrollSpeed.z };
			if (UnityGUI::Vector3("Scroll Speed", scroll, false, 1))
				m_ScrollSpeed = Vec3(scroll[0], scroll[1], scroll[2]);
		}
	}
	if (UnityGUI::Foldout("Lighting"))
	{
		UnityGUI::Slider("Anisotropy", &m_Anisotropy, -0.9f, 0.9f, 1);
		UnityGUI::Slider("Ambient Multiplier", &m_Ambient, 0.0f, 2.0f, 1);
		UnityGUI::HelpBox("Lit by the sun (with its shadow), the 8 nearest point and spot lights and the sky. Volumes do not shadow each other.", false, 1);
	}
}

void LocalVolumetricFog::OnDrawGizmos()
{
	if (!m_pGameObject || !SceneViewOverlay::IsActive())
		return;
	const bool selected = SelectionManager::GetSelectedObjectType() == SelectionType::GAMEOBJECT && SelectionManager::GetSelectedGameObject() == m_pGameObject;
	XMMATRIX w2l;
	Vec3 half, center;
	Placement(w2l, half, center);
	const XMMATRIX l2w = XMMatrixInverse(nullptr, w2l);
	auto W = [&](float x, float y, float z) { XMFLOAT3 o; XMStoreFloat3(&o, XMVector3TransformCoord(XMVectorSet(x, y, z, 1.0f), l2w)); return o; };
	const ImU32 color = selected ? IM_COL32(170, 220, 255, 255) : IM_COL32(170, 220, 255, 120);
	if (m_Shape == Shape::Box)
	{
		static const int edges[12][2] = { {0,1},{2,3},{4,5},{6,7},{0,2},{1,3},{4,6},{5,7},{0,4},{1,5},{2,6},{3,7} };
		XMFLOAT3 c[8];
		for (int i = 0; i < 8; ++i)
			c[i] = W((i & 1) ? 1.0f : -1.0f, (i & 2) ? 1.0f : -1.0f, (i & 4) ? 1.0f : -1.0f);
		for (const auto& e : edges)
			SceneViewOverlay::DrawLine(c[e[0]], c[e[1]], color, selected ? 1.6f : 1.0f);
	}
	else
	{
		const int seg = 40;
		for (int axis = 0; axis < 3; ++axis)
		{
			XMFLOAT3 prev = {};
			for (int k = 0; k <= seg; ++k)
			{
				const float a = XM_2PI * k / seg, ca = cosf(a), sa = sinf(a);
				const XMFLOAT3 cur = axis == 0 ? W(ca, sa, 0) : (axis == 1 ? W(ca, 0, sa) : W(0, ca, sa));
				if (k > 0)
					SceneViewOverlay::DrawLine(prev, cur, color, selected ? 1.6f : 1.0f);
				prev = cur;
			}
		}
	}
}

GENERATE_COMPONENT_FUNC_TOJSON(LocalVolumetricFog)
{
	json j;
	SERIALIZE_TYPE(j, LocalVolumetricFog);
	j["enabled"] = m_Enabled;
	j["albedo"] = { m_Albedo[0], m_Albedo[1], m_Albedo[2] };
	j["fogDistance"] = m_FogDistance;
	j["shape"] = m_Shape == Shape::Box ? "Box" : "Ellipsoid";
	j["size"] = { m_Size.x, m_Size.y, m_Size.z };
	j["blendDistance"] = m_BlendDistance;
	j["falloffMode"] = m_Falloff == Falloff::Linear ? "Linear" : "Exponential";
	j["distanceFadeStart"] = m_FadeStart;
	j["distanceFadeEnd"] = m_FadeEnd;
	j["maskMode"] = m_Mask == Mask::None ? "None" : "Noise";
	j["noiseTiling"] = m_NoiseTiling;
	j["noiseStrength"] = m_NoiseStrength;
	j["scrollSpeed"] = { m_ScrollSpeed.x, m_ScrollSpeed.y, m_ScrollSpeed.z };
	j["anisotropy"] = m_Anisotropy;
	j["ambientMultiplier"] = m_Ambient;
	return j;
}

GENERATE_COMPONENT_FUNC_FROMJSON(LocalVolumetricFog)
{
	auto vec = [&](const char* key, float* out, int n) {
		if (j.contains(key) && j[key].is_array() && (int)j[key].size() == n)
			for (int i = 0; i < n; ++i) out[i] = j[key][i].get<float>();
	};
	auto pick = [&](const char* key, const char* a, const char* b, int def) {
		if (!j.contains(key)) return def;
		if (j[key].is_number_integer()) return std::clamp(j[key].get<int>(), 0, 1);
		const std::string s = j[key].is_string() ? j[key].get<std::string>() : std::string();
		return _stricmp(s.c_str(), a) == 0 ? 0 : (_stricmp(s.c_str(), b) == 0 ? 1 : def);
	};
	m_Enabled = j.value("enabled", true);
	vec("albedo", m_Albedo, 3);
	m_FogDistance = (std::max)(0.05f, j.value("fogDistance", 5.0f));
	m_Shape = (Shape)pick("shape", "Box", "Ellipsoid", 0);
	vec("size", &m_Size.x, 3);
	m_BlendDistance = (std::max)(0.0f, j.value("blendDistance", 1.0f));
	m_Falloff = (Falloff)pick("falloffMode", "Linear", "Exponential", 0);
	m_FadeStart = (std::max)(0.0f, j.value("distanceFadeStart", 1000.0f));
	m_FadeEnd = (std::max)(m_FadeStart + 0.01f, j.value("distanceFadeEnd", 1200.0f));
	m_Mask = (Mask)pick("maskMode", "None", "Noise", 0);
	m_NoiseTiling = (std::max)(0.05f, j.value("noiseTiling", 4.0f));
	m_NoiseStrength = std::clamp(j.value("noiseStrength", 0.7f), 0.0f, 1.0f);
	vec("scrollSpeed", &m_ScrollSpeed.x, 3);
	m_Anisotropy = std::clamp(j.value("anisotropy", 0.3f), -0.9f, 0.9f);
	m_Ambient = std::clamp(j.value("ambientMultiplier", 1.0f), 0.0f, 4.0f);
}
