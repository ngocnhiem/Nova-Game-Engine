#include "pch.h"
#include "InputDevices.h"
#include "GameViewEditorWindow.h"
#include "ScriptEngine.h"
#include <cmath>

#if !defined(NOVA_ANDROID) && !defined(NOVA_WEB)
#define NOVA_INPUT_WIN32 1
#include <Xinput.h>
#endif
#ifndef VK_XBUTTON1
#define VK_XBUTTON1 0x05   // 안드로이드 · 웹의 Win32 대체 헤더에는 없다
#define VK_XBUTTON2 0x06
#endif

namespace
{
	InputDevices::PadState s_Pads[8];
	InputDevices::PadState s_InjectedPads[4];
	int s_PadFrames[4] = {};
	InputDevices::PadRelease s_PadRelease[4];
	uint8_t s_InjectedKeys[256] = {};
	int s_KeyFrames[256] = {};             // > 0 = 이 프레임 수 뒤 놓는다
	bool s_MouseSet = false;
	float s_MouseX = 0, s_MouseY = 0;
	float s_InjDX = 0, s_InjDY = 0;
	int s_DeltaFrames = 0;
	float s_InjScroll = 0;
	int s_ScrollFrames = 0;
	float s_DX = 0, s_DY = 0;              // 이번 프레임 마우스 이동량 (게임 화면 픽셀)
	float s_LastX = 0, s_LastY = 0;
	bool s_HaveLast = false;
	int s_LockMode = 0;                    // 0 None, 1 Locked, 2 Confined
	bool s_Visible = true;
	bool s_Clipped = false;
	bool s_Playing = false;
	bool s_LockSuspended = false;          // 에디터에서 Esc 로 푼 상태 (Game 뷰를 다시 누르면 다시 묶는다)

#if NOVA_INPUT_WIN32
	typedef DWORD(WINAPI* GetStateFn)(DWORD, XINPUT_STATE*);
	typedef DWORD(WINAPI* SetStateFn)(DWORD, XINPUT_VIBRATION*);
	GetStateFn s_GetState = nullptr;
	SetStateFn s_SetState = nullptr;
	bool s_XInputTried = false;
	double s_NextProbe[4] = {};            // 연결 안 된 칸은 가끔만 묻는다 (XInputGetState 가 느리다)

	void LoadXInput()
	{
		if (s_XInputTried)
			return;
		s_XInputTried = true;
		const wchar_t* dlls[] = { L"xinput1_4.dll", L"xinput1_3.dll", L"xinput9_1_0.dll" };
		for (const wchar_t* name : dlls)
			if (HMODULE m = ::LoadLibraryW(name))
			{
				s_GetState = (GetStateFn)::GetProcAddress(m, "XInputGetState");
				s_SetState = (SetStateFn)::GetProcAddress(m, "XInputSetState");
				if (s_GetState)
				{
					EditorLog::Write("Input", "XInput: %ls", name);
					return;
				}
			}
		EditorLog::Write("Input", "XInput not available (no gamepads)");
	}

	double NowSeconds()
	{
		static LARGE_INTEGER freq = [] { LARGE_INTEGER f; ::QueryPerformanceFrequency(&f); return f; }();
		LARGE_INTEGER t;
		::QueryPerformanceCounter(&t);
		return (double)t.QuadPart / (double)freq.QuadPart;
	}

	float Stick(SHORT v) { return v < 0 ? (std::max)(-1.0f, v / 32768.0f) : (std::min)(1.0f, v / 32767.0f); }

	// 이 프로세스의 창이 앞에 있나 (에디터는 창 밖으로 뺀 ImGui 창도 같은 프로세스)
	bool AppForeground()
	{
		HWND fg = ::GetForegroundWindow();
		if (fg == nullptr)
			return false;
		DWORD pid = 0;
		::GetWindowThreadProcessId(fg, &pid);
		return pid == ::GetCurrentProcessId();
	}

	// ImGui 좌표 → 화면 좌표 (창 밖 뷰포트가 켜져 있으면 같다)
	POINT ToScreen(const ImVec2& p)
	{
		POINT pt{ (LONG)std::lround(p.x), (LONG)std::lround(p.y) };
		if (ImGui::GetCurrentContext() && !(ImGui::GetIO().ConfigFlags & ImGuiConfigFlags_ViewportsEnable))
			::ClientToScreen(Application::GetI()->GetMainHwnd(), &pt);
		return pt;
	}

	bool GameRect(RECT& r)
	{
		int w = 0, h = 0;
		GameViewEditorWindow::GameSize(w, h);
		if (w <= 0 || h <= 0)
			return false;
		const POINT a = ToScreen(GameViewEditorWindow::GameToScreen(0.0f, (float)h));
		const POINT b = ToScreen(GameViewEditorWindow::GameToScreen((float)w, 0.0f));
		r = { (std::min)(a.x, b.x), (std::min)(a.y, b.y), (std::max)(a.x, b.x), (std::max)(a.y, b.y) };
		return r.right - r.left > 2 && r.bottom - r.top > 2;
	}

	void Unclip()
	{
		if (s_Clipped)
		{
			::ClipCursor(nullptr);
			s_Clipped = false;
		}
	}
#endif

	// 화면 픽셀 → 게임 화면 픽셀 배율
	float GameScale()
	{
		int w = 0, h = 0;
		GameViewEditorWindow::GameSize(w, h);
		if (w <= 0)
			return 1.0f;
		const ImVec2 a = GameViewEditorWindow::GameToScreen(0.0f, 0.0f), b = GameViewEditorWindow::GameToScreen((float)w, 0.0f);
		const float sw = std::fabs(b.x - a.x);
		return sw > 1.0f ? (float)w / sw : 1.0f;
	}
}

namespace InputDevices
{
	void Poll(bool inputFocus)
	{
		if (Application::IsPlaying() != s_Playing)
			OnPlayModeChanged(Application::IsPlaying());   // Play 시작 (플레이어는 처음 프레임)
		// ---- 주입 수명
		for (int vk = 0; vk < 256; ++vk)
			if (s_KeyFrames[vk] > 0 && --s_KeyFrames[vk] == 0)
				s_InjectedKeys[vk] = 0;
		for (int i = 0; i < 4; ++i)
			if (s_PadFrames[i] > 0 && --s_PadFrames[i] == 0)
			{
				// 이 주입이 누른 것만 놓는다 (붙잡고 있는 스틱은 그대로)
				PadState& p = s_InjectedPads[i];
				const PadRelease& r = s_PadRelease[i];
				p.Buttons &= ~r.Buttons;
				if (r.Sticks) p.LX = p.LY = p.RX = p.RY = 0.0f;
				if (r.LT) p.LT = 0.0f;
				if (r.RT) p.RT = 0.0f;
				s_PadRelease[i] = PadRelease();
			}
		if (s_DeltaFrames > 0 && --s_DeltaFrames == 0) s_InjDX = s_InjDY = 0;
		if (s_ScrollFrames > 0 && --s_ScrollFrames == 0) s_InjScroll = 0;

		// ---- 게임패드
#if NOVA_INPUT_WIN32
		LoadXInput();
		const bool fg = AppForeground();
		const double now = NowSeconds();
		for (int i = 0; i < 4; ++i)
		{
			PadState& p = s_Pads[i];
			if (!s_GetState) { p = PadState(); continue; }
			if (!p.Connected && now < s_NextProbe[i])
				continue;
			XINPUT_STATE xs = {};
			if (s_GetState((DWORD)i, &xs) != ERROR_SUCCESS)
			{
				if (p.Connected)
					EditorLog::Write("Input", "gamepad %d disconnected", i);
				p = PadState();
				s_NextProbe[i] = now + 1.5 + i * 0.1;
				continue;
			}
			if (!p.Connected)
				EditorLog::Write("Input", "gamepad %d connected (XInput)", i);
			p.Connected = true;
			if (!fg)
			{
				// 다른 앱이 앞에 있으면 놓은 것으로 (연결은 그대로)
				p = PadState();
				p.Connected = true;
				continue;
			}
			p.Buttons = xs.Gamepad.wButtons;
			p.LX = Stick(xs.Gamepad.sThumbLX);
			p.LY = Stick(xs.Gamepad.sThumbLY);
			p.RX = Stick(xs.Gamepad.sThumbRX);
			p.RY = Stick(xs.Gamepad.sThumbRY);
			p.LT = xs.Gamepad.bLeftTrigger / 255.0f;
			p.RT = xs.Gamepad.bRightTrigger / 255.0f;
		}
#endif
		for (int i = 0; i < 4; ++i)
			s_Pads[4 + i] = s_InjectedPads[i];

		// ---- 마우스 이동량 · 커서 잠금
		float x = 0, y = 0;
		const bool inside = GameViewEditorWindow::MouseToGame(x, y);
		s_DX = s_DY = 0;
		const bool focus = inputFocus && s_Playing;
#if NOVA_INPUT_WIN32
		const bool locked = s_LockMode == 1 && focus && !s_LockSuspended && AppForeground();
		if (s_LockMode != 0 && s_Playing && !Application::IsPlayer() && inputFocus && (::GetAsyncKeyState(VK_ESCAPE) & 0x8000) && !s_LockSuspended)
		{
			// Unity 에디터: Esc 로 커서를 돌려준다 (Game 뷰를 다시 누르면 다시 묶인다)
			s_LockSuspended = true;
			Unclip();
		}
		if (s_LockSuspended && inputFocus && (::GetAsyncKeyState(VK_LBUTTON) & 0x8000) && inside)
			s_LockSuspended = false;
		RECT rect;
		const bool haveRect = (s_LockMode != 0 && focus && !s_LockSuspended && AppForeground()) && GameRect(rect);
		if (haveRect && !s_Clipped)
		{
			::ClipCursor(&rect);
			s_Clipped = true;
		}
		else if (!haveRect)
			Unclip();
		if (locked && haveRect)
		{
			POINT c{ (rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2 }, cur{};
			::GetCursorPos(&cur);
			if (s_HaveLast)
			{
				const float k = GameScale();
				s_DX = (cur.x - c.x) * k;
				s_DY = -(cur.y - c.y) * k;   // Unity: 위가 +
			}
			::SetCursorPos(c.x, c.y);
			s_HaveLast = true;
			GameViewEditorWindow::MouseToGame(x, y);
			s_LastX = x;
			s_LastY = y;
		}
		else
#endif
		{
			if (focus && s_HaveLast)
			{
				s_DX = x - s_LastX;
				s_DY = y - s_LastY;
			}
			s_LastX = x;
			s_LastY = y;
			s_HaveLast = true;
		}
		if (!focus)
			s_DX = s_DY = 0;
		s_DX += s_InjDX;
		s_DY += s_InjDY;
	}

	void MergeKeys(uint8_t* keys)
	{
		for (int vk = 1; vk < 256; ++vk)
			if (s_InjectedKeys[vk])
				keys[vk] = 1;
	}

	const PadState& Pad(int slot)
	{
		static PadState empty;
		return slot >= 0 && slot < 8 ? s_Pads[slot] : empty;
	}

	bool ReadHardwarePad(int slot, PadState& out)
	{
		out = PadState();
#if NOVA_INPUT_WIN32
		LoadXInput();
		if (slot < 0 || slot >= 4 || !s_GetState)
			return false;
		XINPUT_STATE xs = {};
		if (s_GetState((DWORD)slot, &xs) != ERROR_SUCCESS)
			return false;
		out.Connected = true;
		out.Buttons = xs.Gamepad.wButtons;
		out.LX = Stick(xs.Gamepad.sThumbLX);
		out.LY = Stick(xs.Gamepad.sThumbLY);
		out.RX = Stick(xs.Gamepad.sThumbRX);
		out.RY = Stick(xs.Gamepad.sThumbRY);
		out.LT = xs.Gamepad.bLeftTrigger / 255.0f;
		out.RT = xs.Gamepad.bRightTrigger / 255.0f;
		return true;
#else
		(void)slot;
		return false;
#endif
	}

	void SetMotors(int slot, float low, float high)
	{
#if NOVA_INPUT_WIN32
		LoadXInput();
		if (slot < 0 || slot >= 4 || !s_SetState)
			return;
		XINPUT_VIBRATION v{ (WORD)(std::clamp(low, 0.0f, 1.0f) * 65535.0f), (WORD)(std::clamp(high, 0.0f, 1.0f) * 65535.0f) };
		s_SetState((DWORD)slot, &v);
#else
		(void)slot; (void)low; (void)high;
#endif
	}

	bool MousePosition(float& x, float& y)
	{
		if (s_MouseSet)
		{
			x = s_MouseX;
			y = s_MouseY;
			return true;
		}
		return GameViewEditorWindow::MouseToGame(x, y);
	}

	void MouseDelta(float& dx, float& dy) { dx = s_DX; dy = s_DY; }

	float ScrollY()
	{
		const float hw = GameViewEditorWindow::HasInputFocus() ? GameViewEditorWindow::ScrollDelta() : 0.0f;
		return hw + s_InjScroll;
	}

	void InjectKey(int vk, bool down, int frames)
	{
		if (vk <= 0 || vk >= 256)
			return;
		s_InjectedKeys[vk] = down ? 1 : 0;
		s_KeyFrames[vk] = down ? frames : 0;
	}

	void InjectPad(int index, const PadState& s, int frames, const PadRelease& release)
	{
		if (index < 0 || index >= 4)
			return;
		s_InjectedPads[index] = s;
		s_PadFrames[index] = s.Connected ? frames : 0;
		s_PadRelease[index] = frames > 0 ? release : PadRelease();
		s_Pads[4 + index] = s;
	}

	PadState InjectedPad(int index) { return index >= 0 && index < 4 ? s_InjectedPads[index] : PadState(); }

	void InjectMousePosition(bool set, float x, float y)
	{
		s_MouseSet = set;
		s_MouseX = x;
		s_MouseY = y;
	}

	void InjectMouseDelta(float dx, float dy, int frames)
	{
		s_InjDX = dx;
		s_InjDY = dy;
		s_DeltaFrames = frames;
	}

	void InjectScroll(float y, int frames)
	{
		s_InjScroll = y;
		s_ScrollFrames = frames;
	}

	void ReleaseAll()
	{
		memset(s_InjectedKeys, 0, sizeof(s_InjectedKeys));
		memset(s_KeyFrames, 0, sizeof(s_KeyFrames));
		for (int i = 0; i < 4; ++i)
		{
			s_InjectedPads[i] = PadState();
			s_PadFrames[i] = 0;
			s_Pads[4 + i] = PadState();
		}
		s_MouseSet = false;
		s_InjDX = s_InjDY = s_InjScroll = 0;
		s_DeltaFrames = s_ScrollFrames = 0;
	}

	std::string Describe()
	{
		nlohmann::json keys = nlohmann::json::array();
		for (int vk = 1; vk < 256; ++vk)
			if (s_InjectedKeys[vk])
				keys.push_back(vk);
		nlohmann::json pads = nlohmann::json::array();
		for (int i = 0; i < 8; ++i)
		{
			const PadState& p = s_Pads[i];
			if (!p.Connected)
				continue;
			pads.push_back({ { "slot", i }, { "injected", i >= 4 }, { "buttons", p.Buttons }, { "leftStick", { p.LX, p.LY } }, { "rightStick", { p.RX, p.RY } }, { "triggers", { p.LT, p.RT } } });
		}
		nlohmann::json j = { { "injectedKeys", keys }, { "pads", pads }, { "mouseDelta", { s_DX, s_DY } }, { "cursorLock", s_LockMode }, { "cursorVisible", s_Visible }, { "lockSuspended", s_LockSuspended } };
		if (s_MouseSet)
			j["mousePosition"] = { s_MouseX, s_MouseY };
		return j.dump();
	}

	void SetCursor(int lockMode, bool visible)
	{
		s_LockMode = std::clamp(lockMode, 0, 2);
		s_Visible = visible;
		s_LockSuspended = false;
#if NOVA_INPUT_WIN32
		if (s_LockMode == 0)
			Unclip();
#endif
	}

	int CursorLockMode() { return s_LockSuspended ? 0 : s_LockMode; }

	bool WantsHiddenCursor() { return s_Playing && !s_LockSuspended && (s_LockMode == 1 || !s_Visible); }

	void ApplyCursorStyle(bool hovered)
	{
		if (hovered && WantsHiddenCursor() && GameViewEditorWindow::HasInputFocus() && ImGui::GetCurrentContext())
			ImGui::SetMouseCursor(ImGuiMouseCursor_None);
	}

	void OnPlayModeChanged(bool playing)
	{
		s_Playing = playing;
		s_LockMode = 0;
		s_Visible = true;
		s_LockSuspended = false;
		s_HaveLast = false;
		s_DX = s_DY = 0;
#if NOVA_INPUT_WIN32
		Unclip();
		if (!playing)
			for (int i = 0; i < 4; ++i)
				SetMotors(i, 0.0f, 0.0f);
#endif
		if (!playing)
			ReleaseAll();
	}
}

// ---------------------------------------------------------------- C# (NovaEngine.InputSystem.InputNative)
struct NovaInputState
{
	int version;
	int flags;
	float mouseX, mouseY, mouseDX, mouseDY, scrollX, scrollY;
	int mouseButtons;
	int padConnected[8];
	uint32 padButtons[8];
	float padAxes[48];
	uint8_t keys[256];
};

NOVA_PACKAGE_EXPORT int NovaInputSys_GetState(NovaInputState* out, int size)
{
	if (out == nullptr || size < (int)sizeof(NovaInputState))
		return 0;
	memset(out, 0, sizeof(NovaInputState));
	out->version = 1;
	const bool focus = GameViewEditorWindow::HasInputFocus();
	float x = 0, y = 0;
	const bool inside = InputDevices::MousePosition(x, y);
	out->flags = (focus ? 1 : 0) | (inside ? 2 : 0);
#if NOVA_INPUT_WIN32
	out->flags |= AppForeground() ? 4 : 0;
#endif
#if defined(NOVA_ANDROID)
	out->flags |= 8 | 4;
#endif
	out->mouseX = x;
	out->mouseY = y;
	InputDevices::MouseDelta(out->mouseDX, out->mouseDY);
	out->scrollY = InputDevices::ScrollY();
	for (int vk = 1; vk < 256; ++vk)
		out->keys[vk] = ScriptEngine::KeyState(vk, 0) ? 1 : 0;
	const int mvk[5] = { VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2 };
	for (int b = 0; b < 5; ++b)
		if (out->keys[mvk[b]])
			out->mouseButtons |= 1 << b;
	for (int i = 0; i < 8; ++i)
	{
		const InputDevices::PadState& p = InputDevices::Pad(i);
		out->padConnected[i] = p.Connected ? 1 : 0;
		out->padButtons[i] = p.Buttons;
		float* a = out->padAxes + i * 6;
		a[0] = p.LX; a[1] = p.LY; a[2] = p.RX; a[3] = p.RY; a[4] = p.LT; a[5] = p.RT;
	}
	return 1;
}

NOVA_PACKAGE_EXPORT void NovaInputSys_SetMotors(int slot, float low, float high) { InputDevices::SetMotors(slot, low, high); }

NOVA_PACKAGE_EXPORT void NovaInputSys_WarpCursor(float x, float y)
{
#if NOVA_INPUT_WIN32
	const POINT p = ToScreen(GameViewEditorWindow::GameToScreen(x, y));
	::SetCursorPos(p.x, p.y);
#else
	(void)x; (void)y;
#endif
}

NOVA_PACKAGE_EXPORT void NovaInputSys_SetCursor(int lockMode, int visible) { InputDevices::SetCursor(lockMode, visible != 0); }
NOVA_PACKAGE_EXPORT int NovaInputSys_GetCursorLock() { return InputDevices::CursorLockMode(); }
