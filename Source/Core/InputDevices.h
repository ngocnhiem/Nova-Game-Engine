#pragma once
#include <string>

// Input System (C# NovaEngine.InputSystem · 옛 Input) 이 프레임마다 읽는 장치 상태.
//  - 게임패드: XInput (Xbox 호환 패드 4 개 — 칸 0 ~ 3), NOVA CLI 로 넣은 가상 패드 4 개 (칸 4 ~ 7, nova input gamepad)
//  - 자판 · 마우스 단추: ScriptEngine 의 키 상태 (게임 화면에 입력 포커스가 있을 때) + CLI 로 넣은 키 (포커스 없이도 — 숨긴 검사 에디터)
//  - 마우스 이동량 · 휠, 커서 잠금 (Cursor.lockState: Locked = 숨기고 가운데로, Confined = 창 안에만)
// 하드웨어 게임패드는 앱이 앞에 있을 때만 (다른 창에서 쓰는 패드가 게임에 들어가지 않게 — Unity 의 Background Behavior 기본값)
namespace InputDevices
{
	struct PadState
	{
		bool Connected = false;
		uint32 Buttons = 0;              // XInput 비트 (A 0x1000, B 0x2000, X 0x4000, Y 0x8000, …)
		float LX = 0, LY = 0, RX = 0, RY = 0, LT = 0, RT = 0;
	};

	// 프레임마다 (ScriptEngine::BeginFrame): 패드 · 마우스 이동량 · 커서 잠금 · 주입 수명
	NOVA_API void Poll(bool inputFocus);
	// 키 상태 256 칸에 CLI 로 넣은 키를 더한다 (마우스 단추 = VK_LBUTTON 1, VK_RBUTTON 2, VK_MBUTTON 4, VK_XBUTTON1 5, VK_XBUTTON2 6)
	NOVA_API void MergeKeys(uint8_t* keys256);
	NOVA_API const PadState& Pad(int slot);
	// XInput 을 바로 읽는다 (에디터의 Listen — Play 가 아닐 때). 칸 0 ~ 3
	NOVA_API bool ReadHardwarePad(int slot, PadState& out);
	NOVA_API void SetMotors(int slot, float low, float high);

	// 게임 화면 좌표 (픽셀, 왼쪽 아래 0,0). 주입한 위치가 있으면 그것
	NOVA_API bool MousePosition(float& x, float& y);
	NOVA_API void MouseDelta(float& dx, float& dy);
	NOVA_API float ScrollY();

	// ---- NOVA CLI (nova input key · mouse · gamepad · release)
	// frames > 0 이면 그 프레임 수 뒤 저절로 놓는다
	NOVA_API void InjectKey(int vk, bool down, int frames = 0);
	// index 0 ~ 3 → 칸 4 ~ 7. frames > 0 이면 그 뒤 release 에 적은 것만 놓는다 (단추 비트 · 스틱 · 트리거 — 다른 값은 그대로)
	struct PadRelease { uint32 Buttons = 0; bool Sticks = false, LT = false, RT = false; };
	NOVA_API void InjectPad(int index, const PadState& s, int frames = 0, const PadRelease& release = PadRelease());
	NOVA_API PadState InjectedPad(int index);
	NOVA_API void InjectMousePosition(bool set, float x = 0, float y = 0);
	NOVA_API void InjectMouseDelta(float dx, float dy, int frames = 1);
	NOVA_API void InjectScroll(float y, int frames = 1);
	NOVA_API void ReleaseAll();
	NOVA_API std::string Describe();   // JSON: 주입 상태 · 연결된 패드 · 커서

	// ---- 커서 (C# Cursor.lockState · visible)
	NOVA_API void SetCursor(int lockMode, bool visible);
	NOVA_API int CursorLockMode();
	NOVA_API bool WantsHiddenCursor();
	// Game 뷰 · 플레이어 화면이 그려질 때 (ImGui 프레임 안): 커서 숨기기
	NOVA_API void ApplyCursorStyle(bool hovered);
	NOVA_API void OnPlayModeChanged(bool playing);
}
