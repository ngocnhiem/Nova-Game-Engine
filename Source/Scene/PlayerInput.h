#pragma once
#include "Component.h"
#include "UIEventList.h"
#include <functional>

namespace InputActions { struct Asset; }

// Unity 의 PlayerInput (Input System) — 동작 에셋 (.inputactions) 하나를 플레이어 하나에.
//  - Actions: 쓸 에셋, Default Scheme · Auto-Switch (자판+마우스 ↔ 게임패드를 쓰는 대로), Default Map
//  - Behavior: Send Messages (OnMove(InputValue)) · Broadcast Messages · Invoke Unity Events (동작마다 이벤트 목록) · Invoke C Sharp Events
// 실행 (맵 켜기 · 스킴 전환 · 알림) 은 C# NovaEngine.InputSystem.PlayerInputRuntime — 여기는 설정 · Inspector · 저장
class NOVA_API PlayerInput : public Component
{
public:
	enum class Behavior { SendMessages = 0, BroadcastMessages = 1, InvokeUnityEvents = 2, InvokeCSharpEvents = 3 };

	struct ActionEvent
	{
		std::string ActionId, ActionName;   // "Player/Move"
		UIEventList Calls;
	};

	PlayerInput();
	~PlayerInput() override;
	static const std::vector<PlayerInput*>& All();
	bool IsActiveInHierarchy() const;

	const std::string& GetActionsPath() const { return m_Actions; }
	void SetActionsPath(const std::string& path) { m_Actions = path; }
	const std::string& GetDefaultScheme() const { return m_DefaultScheme; }
	const std::string& GetDefaultMap() const { return m_DefaultMap; }
	Behavior GetBehavior() const { return m_Behavior; }
	void SetDefaultMap(const std::string& m) { m_DefaultMap = m; }
	void SetDefaultScheme(const std::string& s) { m_DefaultScheme = s; }
	void SetBehavior(Behavior b) { m_Behavior = b; }
	bool GetNeverAutoSwitch() const { return m_NeverAutoSwitch; }
	void SetNeverAutoSwitch(bool v) { m_NeverAutoSwitch = v; }
	// C# 에 넘기는 설정 (NovaPlayerInput_Settings)
	nlohmann::json RuntimeSettings() const;

	void OnInspectorGUI() override;
	bool UsesUnityInspector() const override { return true; }
	const char* InspectorIconName() const override { return "player_input"; }

	// 에디터가 채운다: Inspector 의 Edit Asset · Create Actions… 가 Input Actions 창을 연다
	static inline std::function<void(const std::string&)> OpenEditor;

private:
	std::string m_Actions;           // "Assets\\InputSystem_Actions.inputactions"
	std::string m_DefaultScheme;     // "" = <Any>
	std::string m_DefaultMap = "Player";
	Behavior m_Behavior = Behavior::SendMessages;
	bool m_NeverAutoSwitch = false;
	std::vector<ActionEvent> m_Events;
	UIEventList m_DeviceLost, m_DeviceRegained, m_ControlsChanged;

	void SyncEvents(const InputActions::Asset& asset);

	GENERATE_COMPONENT_BODY(PlayerInput)
};
REGISTER_COMPONENT(PlayerInput)
