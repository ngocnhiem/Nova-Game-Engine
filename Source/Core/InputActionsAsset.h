#pragma once
#include <string>
#include <vector>
#include <nlohmann/json.hpp>

// Input Action Asset (.inputactions) — Unity 와 같은 JSON (maps · actions · bindings · controlSchemes), Unity 프로젝트 파일을 그대로 읽고 쓴다.
// Input Actions 편집 창 · PlayerInput Inspector · nova inputactions · C# 클래스 만들기 · Project Settings 가 함께 쓴다 (실행은 C# NovaEngine.InputSystem)
namespace InputActions
{
	struct Binding
	{
		std::string Name, Id, Path, Interactions, Processors, Groups, Action;
		bool IsComposite = false, IsPartOfComposite = false;
	};

	struct Action
	{
		std::string Name, Type = "Button", Id, ExpectedControlType = "Button", Processors, Interactions;
		bool InitialStateCheck = false;
	};

	struct Map
	{
		std::string Name, Id;
		std::vector<Action> Actions;
		std::vector<Binding> Bindings;

		int FindAction(const std::string& name) const;
		// 동작의 바인딩 번호 (합성의 부분 포함, 순서대로)
		std::vector<int> BindingsOf(const std::string& action) const;
		// 합성 binding 의 부분 번호
		std::vector<int> PartsOf(int compositeIndex) const;
		// 합성의 끝 (다음 합성 · 일반 바인딩 번호)
		int EndOfComposite(int compositeIndex) const;
		void RenameAction(const std::string& oldName, const std::string& newName);
		void RemoveAction(const std::string& name);
		void RemoveBinding(int index);   // 합성이면 부분도
		// 동작의 바인딩 끝 (새 바인딩을 넣을 자리)
		int InsertPointFor(const std::string& action) const;
	};

	struct DeviceRequirement
	{
		std::string DevicePath;
		bool IsOptional = false, IsOR = false;
	};

	struct ControlScheme
	{
		std::string Name, BindingGroup;
		std::vector<DeviceRequirement> Devices;
	};

	struct NOVA_API Asset
	{
		std::string Name;
		std::vector<Map> Maps;
		std::vector<ControlScheme> Schemes;

		nlohmann::ordered_json ToJson() const;
		void FromJson(const nlohmann::json& j);
		bool Load(const std::string& relPath, std::string* error = nullptr);
		bool Save(const std::string& relPath, std::string* error = nullptr) const;

		int FindMap(const std::string& name) const;
		int FindScheme(const std::string& name) const;
		// "Player/Move" · "Move" · 동작 id
		const Action* FindAction(const std::string& nameOrId, const Map** map = nullptr) const;
		Action* FindAction(const std::string& nameOrId, Map** map = nullptr);
	};

	NOVA_API std::string NewGuid();
	// Unity 6 의 기본 동작 (Player · UI 맵, Keyboard&Mouse · Gamepad · Touch 스킴)
	NOVA_API Asset DefaultAsset(const std::string& name);
	// Assets 아래 .inputactions (상대 경로, 예: "Assets\\Controls.inputactions")
	NOVA_API std::vector<std::string> FindAll();
	NOVA_API std::wstring FullPath(const std::string& relPath);
	NOVA_API bool IsInputActionsPath(const std::string& path);

	// ---- 컨트롤 레이아웃 (경로 고르기 · 보여 줄 이름)
	struct ControlInfo
	{
		std::string Path;      // 장치 아래 상대 경로 ("leftStick/up")
		std::string Display;   // "Left Stick/Up"
		std::string Short;     // "LS" (없으면 빈 문자열)
		std::string Layout;    // "Button" · "Key" · "Axis" · "Vector2" · "Stick" · "Dpad" · "Delta" · "Integer" · "Touch" …
	};
	struct LayoutInfo
	{
		std::string Name, Display, Base;
		std::vector<ControlInfo> Controls;
	};
	NOVA_API const std::vector<LayoutInfo>& Layouts();
	NOVA_API const LayoutInfo* FindLayout(const std::string& name);
	NOVA_API const ControlInfo* FindControl(const std::string& layout, const std::string& path);
	// "<Keyboard>/w" → "W [Keyboard]", 합성 경로 "2DVector(mode=2)" → "2D Vector"
	NOVA_API std::string DisplayPath(const std::string& path, bool withDevice = true);
	// 동작 바인딩 한 줄 (합성 = "W/A/S/D")
	NOVA_API std::string DisplayBinding(const Map& map, int bindingIndex);
	// 컨트롤 형식이 값 종류로 동작 Control Type 과 맞는가 ("Button" 동작에 "<Gamepad>/leftStick" = 아니다)
	NOVA_API bool ControlTypeMatches(const std::string& expectedControlType, const std::string& controlLayout);
	// 자판 키 이름 ("w", "space", "leftShift", "1") ↔ Windows 가상 키
	NOVA_API int KeyVk(const std::string& keyName);
	NOVA_API std::string KeyNameFromVk(int vk);

	// 합성 종류 (Add 메뉴 · Composite Type) 과 부분 이름
	struct CompositeInfo { const char* Name; const char* Display; std::vector<const char*> Parts; const char* ValueType; };
	NOVA_API const std::vector<CompositeInfo>& Composites();
	NOVA_API const CompositeInfo* FindComposite(const std::string& nameOrPath);

	// interactions · processors 의 이름과 매개변수 (편집 창 + 메뉴)
	struct ParamInfo { const char* Name; const char* Type; const char* Default; };   // Type: "float" · "int" · "bool" · "enum:A,B,C"
	struct NamedInfo { const char* Name; const char* Display; std::vector<ParamInfo> Params; const char* ValueType; };
	NOVA_API const std::vector<NamedInfo>& InteractionTypes();
	NOVA_API const std::vector<NamedInfo>& ProcessorTypes();
	// "Hold(duration=0.5),Tap" ↔ 목록
	struct NameAndParams { std::string Name; std::vector<std::pair<std::string, std::string>> Params; };
	NOVA_API std::vector<NameAndParams> ParseList(const std::string& text);
	NOVA_API std::string JoinList(const std::vector<NameAndParams>& list);

	// ---- Project Settings > Input System Package (ProjectSettings/InputSystem.json)
	struct Settings
	{
		std::string ProjectWideActions;   // "Assets\\InputSystem_Actions.inputactions" (InputSystem.actions)
		int UpdateMode = 1;               // 1 Dynamic Update, 2 Fixed Update, 3 Manual
		int BackgroundBehavior = 0;       // 0 Reset And Disable Non-Background Devices, 1 … All Devices, 2 Ignore Focus
		float DefaultDeadzoneMin = 0.125f, DefaultDeadzoneMax = 0.925f;
		float DefaultButtonPressPoint = 0.5f, ButtonReleaseThreshold = 0.75f;
		float DefaultTapTime = 0.2f, DefaultSlowTapTime = 0.5f, DefaultHoldTime = 0.4f;
		float TapRadius = 5.0f, MultiTapDelayTime = 0.75f;
	};
	NOVA_API Settings& ProjectSettings();
	NOVA_API void SaveProjectSettings();
	NOVA_API void ReloadProjectSettings();

	// ---- .meta 의 가져오기 설정 (Unity 의 Generate C# Class)
	struct ImportSettings
	{
		bool GenerateWrapperCode = false;
		std::string WrapperCodePath;       // 비우면 에셋 옆 <이름>.cs
		std::string WrapperClassName;      // 비우면 파일 이름
		std::string WrapperCodeNamespace;
	};
	NOVA_API ImportSettings LoadImportSettings(const std::string& relPath);
	NOVA_API void SaveImportSettings(const std::string& relPath, const ImportSettings& s);
	// C# 클래스 (Unity 의 생성 클래스와 같은 모양: new Controls().Player.Move, IPlayerActions) — 쓴 파일 (상대 경로)
	NOVA_API bool GenerateCSharp(const std::string& relPath, std::string* outPath, std::string* error);
	// 에셋을 저장할 때: Generate C# Class 가 켜져 있으면 다시 만든다
	NOVA_API void OnAssetSaved(const std::string& relPath);
}
