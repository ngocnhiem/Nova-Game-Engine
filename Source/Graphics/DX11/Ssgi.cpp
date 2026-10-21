#include "pch.h"
#include "Ssgi.h"
#include "Ssao.h"
#include "ScreenSpaceReflection.h"
#include "VolumeProfile.h"
#include "EditorLog.h"
#include "Profiler.h"
#include "CliServer.h"

namespace Ssgi
{
	namespace
	{
		ComPtr<FxEffect> s_Fx;
		bool s_LoadFailed = false;

		struct Target
		{
			ComPtr<GfxTexture2D> Tex;
			ComPtr<GfxRenderTargetView> RTV;
			ComPtr<GfxShaderResourceView> SRV;
			UINT W = 0, H = 0;
			bool Ensure(UINT w, UINT h)
			{
				if (RTV && W == w && H == h)
					return true;
				Tex.Reset(); RTV.Reset(); SRV.Reset();
				D3D11_TEXTURE2D_DESC d = {};
				d.Width = (std::max)(w, 1u);
				d.Height = (std::max)(h, 1u);
				d.MipLevels = 1;
				d.ArraySize = 1;
				d.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
				d.SampleDesc.Count = 1;
				d.Usage = D3D11_USAGE_DEFAULT;
				d.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
				auto device = Gfx::Device();
				if (FAILED(device->CreateTexture2D(&d, nullptr, Tex.GetAddressOf())) || FAILED(device->CreateShaderResourceView(Tex.Get(), nullptr, SRV.GetAddressOf()))
					|| FAILED(device->CreateRenderTargetView(Tex.Get(), nullptr, RTV.GetAddressOf())))
				{
					Tex.Reset(); RTV.Reset(); SRV.Reset();
					W = H = 0;
					return false;
				}
				W = w;
				H = h;
				return true;
			}
		};

		// 상태 (꺼짐 · 기다림 · 켜짐) 가 바뀔 때만 Editor.log 에 (진단)
		void Note(int view, const char* state)
		{
			static std::string s_State[2];
			if (s_State[view] != state)
			{
				s_State[view] = state;
				EditorLog::Write("SSGI", "%s view: %s", view ? "scene" : "game", state);
			}
		}

		struct View
		{
			Target Trace, Temp, Blur, History[2], AoCopy;
			int HistoryIndex = 0;
			bool HistoryValid = false;
			XMFLOAT4X4 PrevView = {}, PrevProj = {};
			uint32 Frame = 0;
			Settings Last;
			nlohmann::json Info = { { "active", false } };
		};
		View s_Views[2];

		bool Load()
		{
			if (s_Fx || s_LoadFailed)
				return s_Fx != nullptr;
			std::string error;
			s_Fx = FxEffect::Load(L"../Shaders/69. Ssgi.fx", error);
			if (!s_Fx)
			{
				s_LoadFailed = true;
				EditorLog::Write("SSGI", "69. Ssgi.fx failed to load: %s", error.c_str());
			}
			return s_Fx != nullptr;
		}

		FxVar* Var(const char* name)
		{
			FxVar* v = s_Fx->GetVariableByName(name);
			return v && v->IsValid() ? v : nullptr;
		}
		void SetV(const char* name, float x, float y, float z, float w) { const float f[4] = { x, y, z, w }; if (FxVar* v = Var(name)) v->SetFloatVector(f); }
		void SetM(const char* name, const XMFLOAT4X4& m) { if (FxVar* v = Var(name)) v->SetMatrix(&m._11); }
		void SetR(const char* name, GfxShaderResourceView* srv) { if (FxVar* v = Var(name)) v->SetResource(srv); }

		void Draw(const char* tech, GfxRenderTargetView* rtv, UINT w, UINT h)
		{
			FxTechnique* t = s_Fx->GetTechniqueByName(tech);
			if (!t || !t->IsValid())
				return;
			auto ctx = Application::GetI()->GetDeviceContext();
			GfxRenderTargetView* rtvs[1] = { rtv };
			ctx->OMSetRenderTargets(1, rtvs, nullptr);
			D3D11_VIEWPORT vp = { 0.0f, 0.0f, (float)w, (float)h, 0.0f, 1.0f };
			ctx->RSSetViewports(1, &vp);
			t->GetPassByIndex(0)->Apply(0, ctx);
			ctx->Draw(3, 0);
			GfxShaderResourceView* nullSRV[8] = {};
			ctx->PSSetShaderResources(0, 8, nullSRV);
			ctx->OMSetRenderTargets(0, nullptr, nullptr);
		}
	}

	Settings Settings::FromStack(const VolumeStack& stack)
	{
		Settings s;
		const VolumeComponent* c = stack.Get("ScreenSpaceGlobalIllumination");
		if (!c || !stack.IsActive("ScreenSpaceGlobalIllumination"))
			return s;
		s.Enabled = true;
		s.Quality = std::clamp(c->I("quality"), 0, 2);
		s.FullResolution = c->B("fullResolution");
		s.MaxRayLength = (std::max)(c->F("maxRayLength"), 0.5f);
		s.Thickness = (std::max)(c->F("depthBufferThickness"), 0.01f);
		s.Denoise = c->B("denoise");
		s.DenoiserRadius = std::clamp(c->F("denoiserRadius"), 0.1f, 2.0f);
		s.Intensity = (std::max)(c->F("intensity"), 0.0f);
		return s;
	}

	float Render(bool editor, const Settings& s, Ssao* ssao, CXMMATRIX view, CXMMATRIX proj)
	{
		View& v = s_Views[editor ? 1 : 0];
		const Settings prevSettings = v.Last;
		v.Last = s;
		if (!s.Active() || !ssao)
		{
			v.HistoryValid = false;
			v.Info = { { "active", false } };
			Note(editor ? 1 : 0, "off");
			return 0.0f;
		}
		ScreenSpaceReflection::KeepHistory(editor);   // 이번 장면 색을 다음 프레임이 쓴다
		GfxShaderResourceView* color = nullptr;
		XMFLOAT4X4 colorViewProj;
		UINT colorMips = 1;
		GfxShaderResourceView* normalDepth = ssao->NormalDepthSRV().Get();
		GfxRenderTargetView* aoRTV = ssao->AmbientRTV();
		GfxTexture2D* aoTex = ssao->AmbientTexture();
		XMFLOAT4X4 P;
		XMStoreFloat4x4(&P, proj);
		const bool ortho = fabsf(P._34) < 1e-6f;
		if (ortho || !normalDepth || !aoRTV || !aoTex || !Load() || !ScreenSpaceReflection::GetHistory(editor, &color, &colorViewProj, &colorMips))
		{
			const char* why = ortho ? "orthographic camera" : !normalDepth || !aoRTV || !aoTex ? "no SSAO targets" : !s_Fx ? "shader not loaded" : "waiting for the scene color history";
			v.Info = { { "active", false }, { "waiting", why } };
			Note(editor ? 1 : 0, why);
			return 0.0f;   // 켠 첫 프레임 (지난 장면 색 없음) · 직교 카메라
		}
		PROFILE_GPU("SSGI");
		D3D11_TEXTURE2D_DESC ad;
		aoTex->GetDesc(&ad);
		const UINT fullW = ad.Width, fullH = ad.Height;
		const UINT scale = s.FullResolution ? 1u : 2u;
		const UINT w = (std::max)(1u, (fullW + scale - 1) / scale), h = (std::max)(1u, (fullH + scale - 1) / scale);
		if (!v.Trace.Ensure(w, h) || !v.Temp.Ensure(w, h) || !v.Blur.Ensure(w, h) || !v.History[0].Ensure(w, h) || !v.History[1].Ensure(w, h) || !v.AoCopy.Ensure(fullW, fullH))
			return 0.0f;
		if (prevSettings.FullResolution != s.FullResolution || prevSettings.Quality != s.Quality)
			v.HistoryValid = false;
		++v.Frame;

		auto ctx = Application::GetI()->GetDeviceContext();
		ctx->IASetInputLayout(nullptr);
		ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		ctx->IASetVertexBuffers(0, 0, nullptr, nullptr, nullptr);
		ctx->OMSetDepthStencilState(nullptr, 0);
		ctx->OMSetBlendState(nullptr, nullptr, 0xffffffff);
		ctx->RSSetState(nullptr);

		static const float kRays[3] = { 1.0f, 2.0f, 4.0f }, kSteps[3] = { 16.0f, 24.0f, 32.0f };
		const XMMATRIX invView = XMMatrixInverse(nullptr, view);
		XMFLOAT4X4 iv, cur2prev;
		XMStoreFloat4x4(&iv, invView);
		XMStoreFloat4x4(&cur2prev, invView * XMLoadFloat4x4(&v.PrevView) * XMLoadFloat4x4(&v.PrevProj));
		SetV("gProjParams", P._11, P._22, P._31, P._32);
		SetM("gInvView", iv);
		SetM("gPrevViewProj", colorViewProj);
		SetM("gCurViewToPrevClip", cur2prev);
		SetV("gTrace", kSteps[s.Quality], s.MaxRayLength, s.Thickness, kRays[s.Quality]);
		SetV("gSize", (float)fullW, (float)fullH, (float)scale, (float)(v.Frame % 64u));
		SetV("gTemporal", 0.9f, v.HistoryValid ? 1.0f : 0.0f, (float)colorMips, s.DenoiserRadius);
		SetV("gComposite", s.Intensity, 0, 0, 0);

		// 1) 광선
		SetR("gNormalDepth", normalDepth);
		SetR("gColorHistory", color);
		Draw("TraceTech", v.Trace.RTV.Get(), w, h);
		// 2) 시간 누적
		const int cur = v.HistoryIndex;
		SetR("gNormalDepth", normalDepth);
		SetR("gInput", v.Trace.SRV.Get());
		SetR("gGiHistory", v.History[cur ^ 1].SRV.Get());
		Draw("TemporalTech", v.History[cur].RTV.Get(), w, h);
		GfxShaderResourceView* result = v.History[cur].SRV.Get();
		// 3) 흐림 (가로 · 세로)
		if (s.Denoise)
		{
			SetR("gNormalDepth", normalDepth);
			SetR("gInput", result);
			SetV("gBlurDir", 1, 0, 0, 0);
			Draw("BlurTech", v.Temp.RTV.Get(), w, h);
			SetR("gNormalDepth", normalDepth);
			SetR("gInput", v.Temp.SRV.Get());
			SetV("gBlurDir", 0, 1, 0, 0);
			Draw("BlurTech", v.Blur.RTV.Get(), w, h);
			result = v.Blur.SRV.Get();
		}
		// 4) SSAO 결과 텍스처의 gba 로 (r = AO 그대로 — 사본에서 읽는다)
		ctx->CopyResource(v.AoCopy.Tex.Get(), aoTex);
		SetR("gNormalDepth", normalDepth);
		SetR("gInput", result);
		SetR("gAoCopy", v.AoCopy.SRV.Get());
		Draw("CompositeTech", aoRTV, fullW, fullH);

		XMStoreFloat4x4(&v.PrevView, view);
		XMStoreFloat4x4(&v.PrevProj, proj);
		v.HistoryValid = true;
		v.HistoryIndex ^= 1;
		Note(editor ? 1 : 0, "on");
		v.Info = { { "active", true }, { "size", { w, h } }, { "full", { fullW, fullH } }, { "rays", (int)kRays[s.Quality] }, { "steps", (int)kSteps[s.Quality] },
			{ "intensity", s.Intensity }, { "denoise", s.Denoise }, { "frame", v.Frame } };
		return s.Intensity;
	}

	nlohmann::json Info(bool editor) { return s_Views[editor ? 1 : 0].Info; }

	void RegisterEditor()
	{
		CliServer::Register("ssgi", "Screen Space Global Illumination (Volume): {op: info, view?: game|scene}", [](const nlohmann::json& a, nlohmann::json& r, std::string&) {
			const bool editor = a.value("view", std::string("game")) == "scene";
			r = Info(editor);
			return true;
		});
	}
}
